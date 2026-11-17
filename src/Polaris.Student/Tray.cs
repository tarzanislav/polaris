using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32;
using Polaris.Recipes;

namespace Polaris.Student;

/// <summary>The icon in the notification area and its menu. One hidden window receives the clicks; the requests to the
/// store run in the background and their results come back to the window as notices. The window ignores requests to
/// close – the icon ends only with the session (and the agent's service starts it again if it ever falls).</summary>
[SupportedOSPlatform("windows")]
internal sealed unsafe class Tray
{
    private const uint IconMessage = Native.WM_APP + 1;
    private const uint NoticeMessage = Native.WM_APP + 2;

    private static Tray? _current;

    private readonly ConcurrentQueue<(string Title, string Text, bool Warning)> _notices = new();
    private readonly HashSet<string> _pending = [];
    private readonly Lock _gate = new();
    private readonly uint _taskbarCreated = Native.RegisterWindowMessage("TaskbarCreated");
    private readonly SessionCleaning? _cleaning = ReadCleaning();
    private StoreAnswer? _store;
    private nint _window;
    private nint _icon;

    public static int Run()
    {
        _ = Native.SetProcessDpiAwarenessContext(-4); // per monitor v2: a sharp icon on every screen
        var tray = _current = new Tray();
        tray.Start();
        while (Native.GetMessage(out var msg, 0, 0, 0) > 0)
        {
            Native.TranslateMessage(in msg);
            Native.DispatchMessage(in msg);
        }

        return 0;
    }

    private void Start()
    {
        fixed (char* className = "POLARIS.ikona")
        {
            var wc = new Native.WNDCLASSEXW
            {
                cbSize = (uint)sizeof(Native.WNDCLASSEXW),
                lpfnWndProc = &WindowProc,
                hInstance = Native.GetModuleHandle(0),
                lpszClassName = className,
            };
            if (Native.RegisterClassEx(ref wc) == 0)
            {
                throw new InvalidOperationException("Trieda okna sa nedá zaregistrovať (" + Marshal.GetLastPInvokeError() + ").");
            }

            // a hidden top-level window (not message-only): only such a window hears that the taskbar was created again
            _window = Native.CreateWindowEx(0, className, className, 0, 0, 0, 0, 0, 0, 0, wc.hInstance, 0);
        }

        if (_window == 0)
        {
            throw new InvalidOperationException("Okno ikony sa nedá vytvoriť (" + Marshal.GetLastPInvokeError() + ").");
        }

        _icon = IconHandle.Create(Math.Max(16, Native.GetSystemMetrics(Native.SM_CXSMICON)));
        AddIcon();
        if (TrayMenu.Notice(_cleaning) is { } notice)
        {
            Notify("POLARIS", notice, warning: true);
        }

        RefreshInBackground();
    }

    private void AddIcon()
    {
        var data = Data(Native.NIF_MESSAGE | Native.NIF_ICON | Native.NIF_TIP | Native.NIF_SHOWTIP);
        Native.Put(data.szTip, 128, TrayMenu.Tooltip(_cleaning));
        _ = Native.Shell_NotifyIcon(Native.NIM_ADD, ref data);
        data.uVersion = Native.NOTIFYICON_VERSION_4;
        _ = Native.Shell_NotifyIcon(Native.NIM_SETVERSION, ref data);
    }

    private Native.NOTIFYICONDATAW Data(uint flags) => new()
    {
        cbSize = sizeof(Native.NOTIFYICONDATAW),
        hWnd = _window,
        uID = 1,
        uFlags = flags,
        uCallbackMessage = IconMessage,
        hIcon = _icon,
    };

    private void Notify(string title, string text, bool warning = false)
    {
        _notices.Enqueue((title, text, warning));
        _ = Native.PostMessage(_window, NoticeMessage, 0, 0);
    }

    private void ShowNotices()
    {
        // the icon's own window, not a balloon: Windows mutes balloons in the first hour of a new profile
        while (_notices.TryDequeue(out var n))
        {
            NoticeWindow.Show(n.Title, n.Text, n.Warning);
        }
    }

    [UnmanagedCallersOnly]
    private static nint WindowProc(nint hwnd, uint message, nint wParam, nint lParam)
    {
        try
        {
            return _current?.Handle(hwnd, message, wParam, lParam) ?? Native.DefWindowProc(hwnd, message, wParam, lParam);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return 0; // an exception must never cross into Windows; the icon goes on
        }
    }

    private nint Handle(nint hwnd, uint message, nint wParam, nint lParam)
    {
        switch (message)
        {
            case IconMessage:
                var what = (uint)(lParam & 0xFFFF);
                if (what is Native.NIN_SELECT or Native.NIN_KEYSELECT or Native.WM_CONTEXTMENU)
                {
                    ShowMenu((short)(wParam & 0xFFFF), (short)((wParam >> 16) & 0xFFFF));
                }

                return 0;
            case NoticeMessage:
                ShowNotices();
                return 0;
            case Native.WM_CLOSE:
            case Native.WM_ENDSESSION:
                return 0; // not closed on request; the session's end ends the process
            case Native.WM_QUERYENDSESSION:
                return 1; // never stands in the way of signing out
            default:
                if (message == _taskbarCreated && _taskbarCreated != 0)
                {
                    AddIcon(); // Explorer started again – the icon comes back
                    return 0;
                }

                return Native.DefWindowProc(hwnd, message, wParam, lParam);
        }
    }

    private void ShowMenu(int x, int y)
    {
        StoreAnswer? store;
        List<string> pending;
        lock (_gate)
        {
            store = _store;
            pending = [.. _pending];
        }

        var sandboxes = TrayMenu.Sandboxes(SandboxFiles());
        var entries = TrayMenu.Build(store, pending, sandboxes.Select(s => s.Name).ToList(), _cleaning);
        var menu = Native.CreatePopupMenu();
        try
        {
            Fill(menu, entries);
            _ = Native.SetForegroundWindow(_window); // so the menu closes when the pupil clicks elsewhere
            var command = Native.TrackPopupMenuEx(menu, Native.TPM_RETURNCMD | Native.TPM_RIGHTBUTTON | Native.TPM_NONOTIFY, x, y, _window, 0);
            _ = Native.PostMessage(_window, Native.WM_NULL, 0, 0);
            Run(command, store, sandboxes);
        }
        finally
        {
            Native.DestroyMenu(menu); // destroys the submenus too
        }

        RefreshInBackground(); // the next opening shows a fresh offer
    }

    private static void Fill(nint menu, IReadOnlyList<MenuEntry> entries)
    {
        foreach (var e in entries)
        {
            if (e.IsSeparator)
            {
                Native.AppendMenu(menu, Native.MF_SEPARATOR, 0, null);
            }
            else if (e.Children is { } children)
            {
                var sub = Native.CreatePopupMenu();
                Fill(sub, children);
                Native.AppendMenu(menu, Native.MF_POPUP, (nuint)sub, e.Text);
            }
            else
            {
                var flags = Native.MF_STRING | (e.Checked ? Native.MF_CHECKED : 0) | (e.Enabled ? 0 : Native.MF_GRAYED);
                Native.AppendMenu(menu, flags, (nuint)e.Command, e.Text.Replace("&", "&&", StringComparison.Ordinal));
            }
        }
    }

    private void Run(int command, StoreAnswer? store, List<(string Name, string File)> sandboxes)
    {
        if (command == TrayMenu.Refresh)
        {
            lock (_gate)
            {
                _store = null;
            }
        }
        else if (command >= TrayMenu.SandboxFirst && command - TrayMenu.SandboxFirst < sandboxes.Count)
        {
            OpenSandbox(sandboxes[command - TrayMenu.SandboxFirst]);
        }
        else if (command >= TrayMenu.StoreFirst && store is not null && command - TrayMenu.StoreFirst < store.Items.Count)
        {
            var offer = store.Items[command - TrayMenu.StoreFirst];
            if (offer.Installed)
            {
                Launch(offer);
            }
            else
            {
                Install(offer);
            }
        }
    }

    private void Install(StoreOffer offer)
    {
        lock (_gate)
        {
            if (!_pending.Add(offer.Id))
            {
                return;
            }
        }

        Notify("Školský obchod", $"Inštalujem {offer.Title}… Dám vedieť, keď bude hotový.");
        _ = Task.Run(() =>
        {
            var answer = StoreClient.Ask(new StoreRequest { Command = "install", Item = offer.Id }, StoreClient.InstallLimit);
            lock (_gate)
            {
                _pending.Remove(offer.Id);
                _store = null;
            }

            Notify("Školský obchod", answer.Message, warning: !answer.Ok);
            Refresh();
        });
    }

    /// <summary>An installed program of the store is started through its shortcut in the Start menu, by the shell.</summary>
    private void Launch(StoreOffer offer)
    {
        var shortcut = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), StoreRules.ShortcutFolder, StoreRules.ShortcutFile(offer.Title));
        if (!File.Exists(shortcut))
        {
            Notify("Školský obchod", $"{offer.Title} je nainštalovaný – nájdeš ho v ponuke Štart.");
            return;
        }

        try
        {
            using var _ = Process.Start(new ProcessStartInfo
            {
                FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"),
                ArgumentList = { shortcut },
                UseShellExecute = false,
            });
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Notify("Školský obchod", $"{offer.Title} sa nedá spustiť: {e.Message}", warning: true);
        }
    }

    /// <summary>The Sandbox is opened by the shell of the session (Explorer), as if the pupil clicked the file on the
    /// desktop – it does not become a child of the icon.</summary>
    private void OpenSandbox((string Name, string File) sandbox)
    {
        try
        {
            using var _ = Process.Start(new ProcessStartInfo
            {
                FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"),
                ArgumentList = { sandbox.File },
                UseShellExecute = false,
            });
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Notify("Pieskovisko", $"{sandbox.Name} sa nedá spustiť: {e.Message}", warning: true);
        }
    }

    private void RefreshInBackground() => _ = Task.Run(Refresh);

    private void Refresh()
    {
        var answer = StoreClient.Ask(new StoreRequest { Command = "list" }, StoreClient.ListLimit, connectMilliseconds: 3000);
        lock (_gate)
        {
            _store = answer;
        }
    }

    private static IEnumerable<string> SandboxFiles()
    {
        try
        {
            return Directory.GetFiles(Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory), "*.wsb");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>The rule of this account's session, as the agent keeps it (readable by every user).</summary>
    private static SessionCleaning? ReadCleaning()
    {
        try
        {
            using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = root.OpenSubKey(@"SOFTWARE\POLARIS\Agent\Relacie");
            return key?.GetValue(Environment.UserName) is string value && Enum.TryParse<SessionCleaning>(value, out var mode) ? mode : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return null;
        }
    }
}
