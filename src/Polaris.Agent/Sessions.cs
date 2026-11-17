using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using Microsoft.Win32;
using Polaris.Recipes;

namespace Polaris.Agent;

/// <summary>Values written into the template of profiles (the profile "Default"): every new profile gets them – the pupil's
/// after every cleaning too. The registry file of the template is opened privately for this process (an application hive,
/// no special privileges, nothing mounted for others) once for a run, and closed at its end.</summary>
[SupportedOSPlatform("windows")]
public sealed partial class ProfileTemplateHandler(string? hiveFile = null) : IElementHandler, IDisposable
{
    private RegistryKey? _root;

    private const string Schemes = @"AppEvents\Schemes";

    public bool CanHandle(Element element) => element is UserRegistryElement or SoundsElement;

    public Observation Observe(Element element)
    {
        if (element is SoundsElement)
        {
            var loud = Sounding(Root()).Count;
            return loud == 0 && SchemeName(Root()) == ".None"
                ? new Observation(Observed.Present, "bez zvukov")
                : new Observation(Observed.Different, $"zvuková schéma {SchemeName(Root()) ?? "(nenastavená)"}, udalostí so zvukom: {loud}");
        }

        var u = (UserRegistryElement)element;
        return RegistryValues.Observe(Root(), UserRegistryPath.Normalize(u.Path), u.Name, u.Present, u.Kind, u.Data);
    }

    public void Apply(Element element)
    {
        var root = Root();
        if (element is SoundsElement)
        {
            // what the dialog "Zvuk" does when the scheme "Bez zvukov" is chosen: the scheme's name and every event silent
            using (var schemes = root.CreateSubKey(Schemes))
            {
                schemes.SetValue("", ".None", RegistryValueKind.String);
            }

            foreach (var current in Sounding(root))
            {
                using var key = root.OpenSubKey(current, writable: true);
                key?.SetValue("", "", RegistryValueKind.String);
            }

            root.Flush();
            return;
        }

        var u = (UserRegistryElement)element;
        RegistryValues.Apply(root, UserRegistryPath.Normalize(u.Path), u.Name, u.Present, u.Kind, u.Data);
        root.Flush();
    }

    private static string? SchemeName(RegistryKey root)
    {
        using var schemes = root.OpenSubKey(Schemes);
        return schemes?.GetValue("") as string;
    }

    /// <summary>The ".Current" keys of events that still have a sound (AppEvents\Schemes\Apps\APP\EVENT\.Current).</summary>
    private static List<string> Sounding(RegistryKey root)
    {
        var found = new List<string>();
        using var apps = root.OpenSubKey(Schemes + @"\Apps");
        foreach (var app in apps?.GetSubKeyNames() ?? [])
        {
            using var appKey = apps!.OpenSubKey(app);
            foreach (var ev in appKey?.GetSubKeyNames() ?? [])
            {
                var path = $@"{Schemes}\Apps\{app}\{ev}\.Current";
                using var current = root.OpenSubKey(path);
                if (current?.GetValue("") is string { Length: > 0 })
                {
                    found.Add(path);
                }
            }
        }

        return found;
    }

    public void Dispose()
    {
        _root?.Dispose(); // closing the last handle unloads the file
        _root = null;
    }

    /// <summary>NTUSER.DAT of the profile "Default", as Windows says where it is.</summary>
    public static string DefaultHiveFile()
    {
        using var list = Registry.LocalMachine.OpenSubKey(Profiles.ProfileListKey)
            ?? throw new InvalidOperationException("Zoznam profilov Windows sa nedá otvoriť.");
        var folder = list.GetValue("Default") as string ?? throw new InvalidOperationException("Windows neuvádza predlohu profilov (Default).");
        return Path.Combine(Environment.ExpandEnvironmentVariables(folder), "NTUSER.DAT");
    }

    private RegistryKey Root()
    {
        if (_root is null)
        {
            var file = hiveFile ?? DefaultHiveFile();
            var status = RegLoadAppKey(file, out var handle, 0xF003F /* KEY_ALL_ACCESS */, 0x1 /* REG_PROCESS_APPKEY */, 0);
            if (status != 0)
            {
                handle.Dispose();
                throw new SystemCallException($"Otvorenie predlohy profilov {file} (nemá ju otvorenú iný program?)", status);
            }

            _root = RegistryKey.FromHandle(handle);
        }

        return _root;
    }

    [LibraryImport("advapi32.dll", EntryPoint = "RegLoadAppKeyW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int RegLoadAppKey(string file, out Microsoft.Win32.SafeHandles.SafeRegistryHandle key, uint access, uint options, uint reserved);
}

/// <summary>Accounts and their profiles: who is an administrator, whether a profile exists, is loaded, when it was used, and
/// deleting it through Windows.</summary>
[SupportedOSPlatform("windows")]
public static partial class Profiles
{
    public const string ProfileListKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList";

    public sealed record ProfileState(string Sid, bool Exists, bool Loaded, DateTime? LastUsed);

    /// <summary>The SID of a local account, or null when there is no such account.</summary>
    public static string? SidOf(string account)
    {
        try
        {
            var sid = (SecurityIdentifier)new NTAccount(Environment.MachineName, account).Translate(typeof(SecurityIdentifier));
            return sid.Value;
        }
        catch (IdentityNotMappedException)
        {
            return null;
        }
    }

    public static ProfileState State(string sid)
    {
        var loaded = false;
        using (var users = Registry.Users.OpenSubKey(sid))
        {
            loaded = users is not null;
        }

        using var entry = Registry.LocalMachine.OpenSubKey(ProfileListKey + "\\" + sid);
        if (entry is null)
        {
            return new ProfileState(sid, false, loaded, null);
        }

        DateTime? lastUsed = null;
        if (entry.GetValue("LocalProfileLoadTimeHigh") is int high && entry.GetValue("LocalProfileLoadTimeLow") is int low)
        {
            var fileTime = ((long)(uint)high << 32) | (uint)low;
            if (fileTime > 0)
            {
                lastUsed = DateTime.FromFileTimeUtc(fileTime).ToLocalTime();
            }
        }

        if (lastUsed is null && entry.GetValue("ProfileImagePath") is string folder)
        {
            var hive = Path.Combine(Environment.ExpandEnvironmentVariables(folder), "NTUSER.DAT");
            lastUsed = File.Exists(hive) ? File.GetLastWriteTime(hive) : null;
        }

        return new ProfileState(sid, true, loaded, lastUsed);
    }

    /// <summary>Whether the account is a member of the local Administrators (also through another group).</summary>
    public static bool IsAdministrator(string account)
    {
        var admins = ((NTAccount)new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null).Translate(typeof(NTAccount))).Value;
        var adminsName = admins[(admins.IndexOf('\\') + 1)..];
        var status = NetUserGetLocalGroups(null, account, 0, 1 /* LG_INCLUDE_INDIRECT */, out var buffer, -1, out var read, out _);
        if (status != 0)
        {
            throw new SystemCallException($"Skupiny konta {account}", status);
        }

        try
        {
            for (var i = 0; i < read; i++)
            {
                var name = Marshal.PtrToStringUni(Marshal.ReadIntPtr(buffer, i * IntPtr.Size));
                if (string.Equals(name, adminsName, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }
        finally
        {
            _ = NetApiBufferFree(buffer);
        }
    }

    public static void Delete(string sid)
    {
        if (!DeleteProfile(sid, null, null))
        {
            throw new SystemCallException("Zmazanie profilu " + sid, Marshal.GetLastPInvokeError());
        }
    }

    [LibraryImport("netapi32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int NetUserGetLocalGroups(string? server, string user, int level, int flags, out IntPtr buffer, int maxLength, out int read, out int total);

    [LibraryImport("netapi32.dll")]
    private static partial int NetApiBufferFree(IntPtr buffer);

    [LibraryImport("userenv.dll", EntryPoint = "DeleteProfileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeleteProfile(string sid, string? profilePath, string? computerName);
}

/// <summary>The rules of sessions on this PC, in HKLM\SOFTWARE\POLARIS\Agent\Relacie (only administrators and the agent
/// change them; a recipe cannot touch this part of the registry directly).</summary>
[SupportedOSPlatform("windows")]
public static class SessionRules
{
    public const string Key = @"SOFTWARE\POLARIS\Agent\Relacie";

    public static Dictionary<string, SessionCleaning> Read()
    {
        var rules = new Dictionary<string, SessionCleaning>(StringComparer.OrdinalIgnoreCase);
        using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var key = root.OpenSubKey(Key);
        foreach (var name in key?.GetValueNames() ?? [])
        {
            if (Enum.TryParse<SessionCleaning>(key!.GetValue(name) as string, out var mode))
            {
                rules[name] = mode;
            }
        }

        return rules;
    }

    public static void Set(string account, SessionCleaning? mode)
    {
        using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var key = root.CreateSubKey(Key, writable: true);
        if (mode is { } m)
        {
            key.SetValue(account, m.ToString(), RegistryValueKind.String);
        }
        else
        {
            key.DeleteValue(account, throwOnMissingValue: false);
        }
    }
}

/// <summary>The session element: writes the rule for the cleaner. A missing account or an account of an administrator is
/// refused – the profile of an administrator is never deleted.</summary>
[SupportedOSPlatform("windows")]
public sealed class SessionRuleHandler : IElementHandler
{
    public bool CanHandle(Element element) => element is SessionElement;

    public Observation Observe(Element element)
    {
        var s = (SessionElement)element;
        var account = s.Account.Trim();
        if (!SessionRules.Read().TryGetValue(account, out var mode))
        {
            return new Observation(Observed.Absent, "pravidlo nie je nastavené");
        }

        return mode == s.Clean ? new Observation(Observed.Present, Describe(mode)) : new Observation(Observed.Different, $"je {Describe(mode)}, má byť {Describe(s.Clean)}");
    }

    public void Apply(Element element)
    {
        var s = (SessionElement)element;
        var account = s.Account.Trim();
        if (s.Present)
        {
            if (Profiles.SidOf(account) is null)
            {
                throw new InvalidOperationException($"Konto {account} na tomto PC neexistuje – najprv ho vytvor (bez práv správcu).");
            }

            if (Profiles.IsAdministrator(account))
            {
                throw new InvalidOperationException($"Konto {account} je správca – jeho profil POLARIS nikdy nemaže.");
            }
        }

        SessionRules.Set(account, s.Present ? s.Clean : null);
    }

    public static string Describe(SessionCleaning mode) => mode switch
    {
        SessionCleaning.AtSignOut => "čistiť po odhlásení",
        SessionCleaning.EndOfDay => "čistiť po skončení dňa",
        SessionCleaning.EndOfWeek => "čistiť po skončení týždňa",
        _ => "nečistiť",
    };
}

/// <summary>Deletes the profile of a shared account when its rule says so – never while somebody is signed in to it and
/// never the profile of an administrator. The next sign-in makes a fresh profile from the template.</summary>
[SupportedOSPlatform("windows")]
public static class SessionCleaner
{
    public const string AlreadyClean = "profil je už čistý";

    public sealed record Result(bool Cleaned, string Message);

    /// <param name="beforeDelete">Runs just before the profile is deleted (the programs of the School store of the session
    /// are removed there); its failure is reported, the profile is deleted anyway.</param>
    public static Result Clean(string account, SessionCleaning mode, DateTime now, bool force = false, Func<string, string?>? beforeDelete = null)
    {
        if (SessionPolicy.NeverClean.Contains(account))
        {
            return new Result(false, $"konto {account} sa nikdy nečistí");
        }

        if (Profiles.SidOf(account) is not { } sid)
        {
            return new Result(false, $"konto {account} neexistuje");
        }

        if (Profiles.IsAdministrator(account))
        {
            return new Result(false, $"konto {account} je správca – nečistí sa");
        }

        var state = Profiles.State(sid);
        if (!state.Exists)
        {
            return new Result(false, AlreadyClean);
        }

        if (state.Loaded)
        {
            return new Result(false, "na konte je niekto prihlásený – vyčistí sa po odhlásení");
        }

        if (!force && !SessionPolicy.ShouldClean(mode, state.Loaded, state.Exists, state.LastUsed, now))
        {
            return new Result(false, "podľa pravidla ešte nie je čas");
        }

        var note = beforeDelete?.Invoke(account);
        Profiles.Delete(sid);
        return new Result(true, $"profil konta {account} zmazaný, ďalšie prihlásenie začne načisto{(note is { Length: > 0 } ? "; " + note : "")}");
    }
}
