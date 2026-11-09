namespace Polaris.Windows;

/// <summary>Access rights of the store: only the system and administrators – a pupil can neither read nor change it.</summary>
public static class Security
{
    /// <summary>Protected list: SYSTEM and Administrators full control, inherited by everything inside.</summary>
    public const string SystemAndAdmins = "D:PAI(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)";

    public static void Protect(string path, string sddl = SystemAndAdmins)
    {
        Check.Win32(Advapi32.ConvertSddl(sddl, 1, out var descriptor, out _), "Bezpečnostný popis");
        try
        {
            const uint dacl = 0x4;
            const uint protectedDacl = 0x80000000;
            Check.Win32(Advapi32.SetFileSecurity(path, dacl | protectedDacl, descriptor), "Práva priečinka " + path);
        }
        finally
        {
            Kernel32.LocalFree(descriptor);
        }
    }
}

/// <summary>A lock across processes and sessions (the service in session 0, the owner's window in session 1), so the
/// state is never written by two at once.</summary>
public sealed unsafe class SystemLock : IDisposable
{
    private readonly nint _handle;

    public SystemLock(string name)
    {
        Check.Win32(Advapi32.ConvertSddl("D:(A;;GA;;;SY)(A;;GA;;;BA)", 1, out var descriptor, out _), "Popis zámku");
        try
        {
            var sa = new Kernel32.SecurityAttributes { Length = sizeof(Kernel32.SecurityAttributes), Descriptor = descriptor };
            _handle = Kernel32.CreateMutex(&sa, false, @"Global\" + name);
            Check.Win32(_handle != 0, "Zámok " + name);
        }
        finally
        {
            Kernel32.LocalFree(descriptor);
        }

        var wait = Kernel32.WaitForSingleObject(_handle, 60_000);
        if (wait is not (0 or 0x80)) // signaled, or abandoned by a process that ended
        {
            Kernel32.CloseHandle(_handle);
            throw new TimeoutException("Stav POLARIS práve zapisuje iný proces.");
        }
    }

    public void Dispose()
    {
        Kernel32.ReleaseMutex(_handle);
        Kernel32.CloseHandle(_handle);
    }
}
