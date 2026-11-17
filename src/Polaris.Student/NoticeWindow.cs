using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Polaris.Student;

/// <summary>The icon's own notice: a small window above the clock for a few seconds. Windows mutes the notices of tray
/// icons during the first hour of a new profile ("quiet time"), and a pupil's profile is always new – so the icon shows
/// its notices itself. It never takes the focus; a click closes it. One at a time, the others wait their turn.</summary>
[SupportedOSPlatform("windows")]
internal static unsafe partial class NoticeWindow
{
    private const string ClassName = "POLARIS.oznamenie";
    private const uint WsPopup = 0x80000000;
    private const uint WsExTopmost = 0x8, WsExToolWindow = 0x80, WsExNoActivate = 0x08000000;
    private const uint WmPaint = 0x000F, WmTimer = 0x0113, WmLButtonUp = 0x0202, WmDestroy = 0x0002;
    private const int ShowNoActivate = 4;
    private const uint DtWordBreak = 0x10, DtNoPrefix = 0x800, DtEndEllipsis = 0x8000;
    private const uint Background = 0x5C2A10; // COLORREF 0x00BBGGRR: the night blue of the icon
    private const uint WarningBackground = 0x0A3C8C;
    private const uint TitleColor = 0x66D6FF;
    private const uint TextColor = 0xFFFFFF;
    private static readonly TimeSpan Shown = TimeSpan.FromSeconds(8);

    private static readonly Queue<(string Title, string Text, bool Warning)> Waiting = new();
    private static (string Title, string Text, bool Warning) _current;
    private static nint _window;
    private static bool _registered;

    /// <summary>Shows the notice now, or after the one on the screen. Called on the icon's window thread.</summary>
    public static void Show(string title, string text, bool warning)
    {
        Waiting.Enqueue((title, text, warning));
        if (_window == 0)
        {
            Next();
        }
    }

    private static void Next()
    {
        if (!Waiting.TryDequeue(out _current))
        {
            return;
        }

        var instance = GetModuleHandleW(0);
        fixed (char* name = ClassName)
        {
            if (!_registered)
            {
                var wc = new Native.WNDCLASSEXW
                {
                    cbSize = (uint)sizeof(Native.WNDCLASSEXW),
                    lpfnWndProc = &WindowProc,
                    hInstance = instance,
                    lpszClassName = name,
                };
                _registered = Native.RegisterClassEx(ref wc) != 0;
            }

            var scale = Math.Max(96, GetDpiForSystem()) / 96.0;
            var width = (int)(380 * scale);
            var height = (int)(112 * scale);
            var margin = (int)(12 * scale);
            Rect area;
            if (SystemParametersInfoW(0x0030 /* SPI_GETWORKAREA */, 0, &area, 0) == 0)
            {
                area = new Rect { Right = 1024, Bottom = 768 };
            }

            _window = Native.CreateWindowEx(WsExTopmost | WsExToolWindow | WsExNoActivate, name, name, WsPopup,
                area.Right - width - margin, area.Bottom - height - margin, width, height, 0, 0, instance, 0);
        }

        if (_window == 0)
        {
            Waiting.Clear(); // without a window there is no way to show anything
            return;
        }

        _ = ShowWindow(_window, ShowNoActivate);
        _ = SetTimer(_window, 1, (uint)Shown.TotalMilliseconds, 0);
    }

    [UnmanagedCallersOnly]
    private static nint WindowProc(nint hwnd, uint message, nint wParam, nint lParam)
    {
        try
        {
            switch (message)
            {
                case WmPaint:
                    Paint(hwnd);
                    return 0;
                case WmTimer:
                case WmLButtonUp:
                    _ = KillTimer(hwnd, 1);
                    _ = DestroyWindow(hwnd);
                    return 0;
                case WmDestroy:
                    _window = 0;
                    Next();
                    return 0;
                default:
                    return Native.DefWindowProc(hwnd, message, wParam, lParam);
            }
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return 0; // never into Windows
        }
    }

    private static void Paint(nint hwnd)
    {
        PaintStruct ps;
        var dc = BeginPaint(hwnd, &ps);
        try
        {
            Rect client;
            _ = GetClientRect(hwnd, &client);
            var brush = CreateSolidBrush(_current.Warning ? WarningBackground : Background);
            _ = FillRect(dc, &client, brush);
            _ = Native.DeleteObject(brush);
            _ = SetBkMode(dc, 1 /* TRANSPARENT */);

            var scale = Math.Max(96, GetDpiForSystem()) / 96.0;
            var pad = (int)(14 * scale);
            var titleFont = CreateFontW(-(int)(15 * scale), 0, 0, 0, 700, 0, 0, 0, 1, 0, 0, 5 /* CLEARTYPE */, 0, "Segoe UI");
            var textFont = CreateFontW(-(int)(13 * scale), 0, 0, 0, 400, 0, 0, 0, 1, 0, 0, 5, 0, "Segoe UI");
            var old = SelectObject(dc, titleFont);
            _ = SetTextColor(dc, TitleColor);
            var titleBox = new Rect { Left = pad, Top = pad, Right = client.Right - pad, Bottom = pad + (int)(22 * scale) };
            fixed (char* t = "★  " + _current.Title)
            {
                _ = DrawTextW(dc, t, -1, &titleBox, DtNoPrefix | DtEndEllipsis);
            }

            _ = SelectObject(dc, textFont);
            _ = SetTextColor(dc, TextColor);
            var textBox = new Rect { Left = pad, Top = titleBox.Bottom + (int)(4 * scale), Right = client.Right - pad, Bottom = client.Bottom - pad };
            fixed (char* t = _current.Text)
            {
                _ = DrawTextW(dc, t, -1, &textBox, DtWordBreak | DtNoPrefix | DtEndEllipsis);
            }

            _ = SelectObject(dc, old);
            _ = Native.DeleteObject(titleFont);
            _ = Native.DeleteObject(textFont);
        }
        finally
        {
            _ = EndPaint(hwnd, &ps);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PaintStruct
    {
        public nint Hdc;
        public int Erase;
        public Rect Paint;
        public int Restore;
        public int IncUpdate;
        public fixed byte Reserved[32];
    }

    [LibraryImport("kernel32.dll")]
    private static partial nint GetModuleHandleW(nint name);

    [LibraryImport("user32.dll")]
    private static partial uint GetDpiForSystem();

    [LibraryImport("user32.dll")]
    private static partial int SystemParametersInfoW(uint action, uint param, void* data, uint winIni);

    [LibraryImport("user32.dll")]
    private static partial int ShowWindow(nint hwnd, int command);

    [LibraryImport("user32.dll")]
    private static partial nuint SetTimer(nint hwnd, nuint id, uint milliseconds, nint callback);

    [LibraryImport("user32.dll")]
    private static partial int KillTimer(nint hwnd, nuint id);

    [LibraryImport("user32.dll")]
    private static partial int DestroyWindow(nint hwnd);

    [LibraryImport("user32.dll")]
    private static partial nint BeginPaint(nint hwnd, PaintStruct* paint);

    [LibraryImport("user32.dll")]
    private static partial int EndPaint(nint hwnd, PaintStruct* paint);

    [LibraryImport("user32.dll")]
    private static partial int GetClientRect(nint hwnd, Rect* rect);

    [LibraryImport("user32.dll")]
    private static partial int FillRect(nint dc, Rect* rect, nint brush);

    [LibraryImport("user32.dll", EntryPoint = "DrawTextW")]
    private static partial int DrawTextW(nint dc, char* text, int count, Rect* rect, uint format);

    [LibraryImport("gdi32.dll")]
    private static partial nint CreateSolidBrush(uint color);

    [LibraryImport("gdi32.dll")]
    private static partial int SetBkMode(nint dc, int mode);

    [LibraryImport("gdi32.dll")]
    private static partial uint SetTextColor(nint dc, uint color);

    [LibraryImport("gdi32.dll")]
    private static partial nint SelectObject(nint dc, nint obj);

    [LibraryImport("gdi32.dll", EntryPoint = "CreateFontW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint CreateFontW(int height, int width, int escapement, int orientation, int weight, uint italic, uint underline, uint strikeOut, uint charSet, uint outPrecision, uint clipPrecision, uint quality, uint pitchAndFamily, string face);
}
