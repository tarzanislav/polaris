using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using Microsoft.Win32;
using Polaris.Recipes;
using Polaris.Windows;

namespace Polaris.Agent;

/// <summary>Values written into the template of profiles (the profile "Default"): every new profile gets them – the pupil's
/// after every cleaning too. The registry file of the template is loaded once for a run and unloaded at its end.</summary>
[SupportedOSPlatform("windows")]
public sealed class ProfileTemplateHandler(string? hiveFile = null) : IElementHandler, IDisposable
{
    public const string HiveName = "POLARIS_PREDLOHA";
    private LoadedHive? _hive;

    public bool CanHandle(Element element) => element is UserRegistryElement;

    public Observation Observe(Element element)
    {
        var u = (UserRegistryElement)element;
        using var root = Root();
        return RegistryValues.Observe(root, UserRegistryPath.Normalize(u.Path), u.Name, u.Present, u.Kind, u.Data);
    }

    public void Apply(Element element)
    {
        var u = (UserRegistryElement)element;
        using var root = Root();
        RegistryValues.Apply(root, UserRegistryPath.Normalize(u.Path), u.Name, u.Present, u.Kind, u.Data);
    }

    public void Dispose()
    {
        _hive?.Dispose();
        _hive = null;
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
        _hive ??= LoadedHive.Load(hiveFile ?? DefaultHiveFile(), HiveName);
        return Registry.LocalMachine.OpenSubKey(HiveName, writable: true)
            ?? throw new InvalidOperationException("Predloha profilov sa nenačítala.");
    }
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
    public sealed record Result(bool Cleaned, string Message);

    public static Result Clean(string account, SessionCleaning mode, DateTime now, bool force = false)
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
            return new Result(false, "profil je už čistý");
        }

        if (state.Loaded)
        {
            return new Result(false, "na konte je niekto prihlásený – vyčistí sa po odhlásení");
        }

        if (!force && !SessionPolicy.ShouldClean(mode, state.Loaded, state.Exists, state.LastUsed, now))
        {
            return new Result(false, "podľa pravidla ešte nie je čas");
        }

        Profiles.Delete(sid);
        return new Result(true, $"profil konta {account} zmazaný, ďalšie prihlásenie začne načisto");
    }
}
