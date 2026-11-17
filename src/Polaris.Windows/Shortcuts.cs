using System.Runtime.InteropServices;

namespace Polaris.Windows;

/// <summary>Windows shortcuts (.lnk) through the shell's own object (IShellLink + IPersistFile), called directly over
/// their tables of functions – the shell writes the target the way every part of Windows reads it.</summary>
public static unsafe partial class Shortcuts
{
    private static readonly Guid ShellLinkClass = new("00021401-0000-0000-C000-000000000046");
    private static readonly Guid ShellLinkW = new("000214F9-0000-0000-C000-000000000046");
    private static readonly Guid PersistFile = new("0000010B-0000-0000-C000-000000000046");

    // places in the tables of functions (IUnknown first: 0 QueryInterface, 1 AddRef, 2 Release)
    private const int GetPath = 3, SetWorkingDirectory = 9, SetPath = 20;
    private const int Save = 6;

    public static void Create(string file, string target, string? workingFolder = null)
    {
        var init = CoInitializeEx(0, 0 /* COINIT_MULTITHREADED */);
        nint link = 0, persist = 0;
        try
        {
            var clsid = ShellLinkClass;
            var iid = ShellLinkW;
            Check(CoCreateInstance(&clsid, 0, 1 /* CLSCTX_INPROC_SERVER */, &iid, &link), "Objekt odkazu");
            fixed (char* t = target)
            fixed (char* w = workingFolder ?? Path.GetDirectoryName(target) ?? "")
            {
                Check(((delegate* unmanaged<nint, char*, int>)Method(link, SetPath))(link, t), "Cieľ odkazu");
                Check(((delegate* unmanaged<nint, char*, int>)Method(link, SetWorkingDirectory))(link, w), "Priečinok odkazu");
            }

            var persistIid = PersistFile;
            Check(((delegate* unmanaged<nint, Guid*, nint*, int>)Method(link, 0))(link, &persistIid, &persist), "Ukladanie odkazu");
            fixed (char* f = Path.GetFullPath(file))
            {
                Check(((delegate* unmanaged<nint, char*, int, int>)Method(persist, Save))(persist, f, 1), "Zápis odkazu " + file);
            }
        }
        finally
        {
            Release(persist);
            Release(link);
            if (init >= 0)
            {
                CoUninitialize();
            }
        }
    }

    /// <summary>The target of a shortcut as the shell reads it (empty when it has none).</summary>
    public static string Target(string file)
    {
        var init = CoInitializeEx(0, 0);
        nint link = 0, persist = 0;
        try
        {
            var clsid = ShellLinkClass;
            var iid = ShellLinkW;
            Check(CoCreateInstance(&clsid, 0, 1, &iid, &link), "Objekt odkazu");
            var persistIid = PersistFile;
            Check(((delegate* unmanaged<nint, Guid*, nint*, int>)Method(link, 0))(link, &persistIid, &persist), "Čítanie odkazu");
            fixed (char* f = Path.GetFullPath(file))
            {
                Check(((delegate* unmanaged<nint, char*, uint, int>)Method(persist, 5 /* Load */))(persist, f, 0), "Načítanie odkazu " + file);
            }

            var buffer = stackalloc char[1024];
            Check(((delegate* unmanaged<nint, char*, int, nint, uint, int>)Method(link, GetPath))(link, buffer, 1024, 0, 0), "Cieľ odkazu");
            return new string(buffer);
        }
        finally
        {
            Release(persist);
            Release(link);
            if (init >= 0)
            {
                CoUninitialize();
            }
        }
    }

    private static nint Method(nint obj, int index) => (*(nint**)obj)[index];

    private static void Release(nint obj)
    {
        if (obj != 0)
        {
            _ = ((delegate* unmanaged<nint, uint>)Method(obj, 2))(obj);
        }
    }

    private static void Check(int hr, string what)
    {
        if (hr < 0)
        {
            throw new SystemCallException(what, hr);
        }
    }

    [LibraryImport("ole32.dll")]
    private static partial int CoInitializeEx(nint reserved, uint coInit);

    [LibraryImport("ole32.dll")]
    private static partial void CoUninitialize();

    [LibraryImport("ole32.dll")]
    private static partial int CoCreateInstance(Guid* clsid, nint outer, uint context, Guid* iid, nint* obj);
}
