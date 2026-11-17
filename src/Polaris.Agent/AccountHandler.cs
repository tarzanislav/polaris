using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32;
using Polaris.Recipes;

namespace Polaris.Agent;

/// <summary>Executor of the account element: makes the local account when it is missing (a standard user), keeps its
/// password from expiring and from being changed by its user, and – without a password – sets the empty password once
/// and remembers when, so a password set later by somebody is noticed (never by trying to sign in: wrong attempts would
/// count towards locking the account). An administrator is refused.</summary>
[SupportedOSPlatform("windows")]
public sealed class AccountHandler(string stateKey = AccountHandler.DefaultStateKey) : IElementHandler
{
    public const string DefaultStateKey = @"SOFTWARE\POLARIS\Agent\Konta";

    /// <summary>How far the moment of the last password change may move between reads (the age is in seconds).</summary>
    private static readonly TimeSpan Tolerance = TimeSpan.FromMinutes(5);

    public bool CanHandle(Element element) => element is AccountElement;

    public Observation Observe(Element element)
    {
        var a = (AccountElement)element;
        var name = a.Name.Trim();
        if (Profiles.SidOf(name) is null)
        {
            return new Observation(Observed.Absent, "konto neexistuje");
        }

        if (Profiles.IsAdministrator(name))
        {
            return new Observation(Observed.Different, "konto je správca – recept ho nespravuje (žiacke kontá sú bez práv správcu)");
        }

        var differences = new List<string>();
        var flags = Accounts.Flags(name);
        var wanted = Wanted(a);
        if ((flags & wanted) != wanted)
        {
            differences.Add("heslo môže meniť používateľ alebo mu vyprší");
        }

        if ((flags & Accounts.Disabled) != 0 && !LockedByVault(name))
        {
            differences.Add("konto je vypnuté");
        }

        if (a.NoPassword && !BlankStill(name))
        {
            differences.Add("konto môže mať heslo");
        }

        return differences.Count == 0
            ? new Observation(Observed.Present, a.NoPassword ? "bez hesla, heslo sa nedá zmeniť" : "heslo sa nedá zmeniť ani nevyprší")
            : new Observation(Observed.Different, string.Join("; ", differences));
    }

    public void Apply(Element element)
    {
        var a = (AccountElement)element;
        var name = a.Name.Trim();
        if (Profiles.SidOf(name) is null)
        {
            if (!a.NoPassword)
            {
                throw new InvalidOperationException($"Konto {name} neexistuje – recept heslá nenastavuje: vytvor ho s heslom, alebo ho daj do receptu bez hesla.");
            }

            Accounts.Add(name, Wanted(a));
        }
        else if (Profiles.IsAdministrator(name))
        {
            throw new InvalidOperationException($"Konto {name} je správca – recept nespravuje kontá s právami správcu.");
        }

        var flags = Accounts.Flags(name);
        var off = LockedByVault(name) ? 0 : Accounts.Disabled; // an account locked for a restore stays locked until it ends
        Accounts.SetFlags(name, (flags & ~off) | Wanted(a));
        if (a.NoPassword && !BlankStill(name))
        {
            Accounts.SetPassword(name, "");
            using var key = Registry.LocalMachine.CreateSubKey(stateKey);
            key.SetValue(name, DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture), RegistryValueKind.String);
        }
    }

    private static uint Wanted(AccountElement a) =>
        Accounts.PasswordCannotChange | Accounts.PasswordNeverExpires | (a.NoPassword ? Accounts.PasswordNotRequired : 0);

    /// <summary>POLARIS set the empty password and nobody changed the password since.</summary>
    private bool BlankStill(string name)
    {
        using var key = Registry.LocalMachine.OpenSubKey(stateKey);
        if (key?.GetValue(name) is not string text || !DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var set))
        {
            return false;
        }

        var changed = DateTimeOffset.UtcNow - TimeSpan.FromSeconds(Accounts.PasswordAgeSeconds(name));
        return (changed - set).Duration() <= Tolerance;
    }

    private static bool LockedByVault(string name)
    {
        using var key = Registry.LocalMachine.OpenSubKey(ProfileVault.DefaultStateKey + "\\" + name);
        return key?.GetValue("Zablokovane") is not null;
    }
}

/// <summary>Executor of the automatic sign-in: keeps the rule (which account) for the service; the service sets Windows.</summary>
[SupportedOSPlatform("windows")]
public sealed class AutoLogonHandler(string ruleKey = AutoLogon.RuleKey) : IElementHandler
{
    public bool CanHandle(Element element) => element is AutoLogonElement;

    public Observation Observe(Element element)
    {
        var a = (AutoLogonElement)element;
        var rule = AutoLogon.Rule(ruleKey);
        var mine = string.Equals(rule, a.Account, StringComparison.OrdinalIgnoreCase);
        return mine ? new Observation(Observed.Present, "PC sa po štarte prihlási samo") : new Observation(Observed.Absent, rule is null ? "bez automatického prihlásenia" : $"automaticky sa prihlasuje konto {rule}");
    }

    public void Apply(Element element)
    {
        var a = (AutoLogonElement)element;
        var rule = AutoLogon.Rule(ruleKey);
        if (a.Present)
        {
            AutoLogon.SetRule(a.Account, ruleKey);
        }
        else if (string.Equals(rule, a.Account, StringComparison.OrdinalIgnoreCase))
        {
            AutoLogon.SetRule(null, ruleKey);
        }

        if (ruleKey == AutoLogon.RuleKey)
        {
            AutoLogon.Update(SessionCleaner.Pending);
        }
    }
}

/// <summary>The automatic sign-in of Windows (Winlogon) for an account without a password – no password is ever written
/// anywhere. It is on only when the profile of the account is ready: while somebody is signed in to it, or its profile
/// still waits to be cleaned or put back from the trezor, it is off – the PC never signs in to a profile half put back.</summary>
[SupportedOSPlatform("windows")]
public static class AutoLogon
{
    public const string RuleKey = @"SOFTWARE\POLARIS\Agent";
    public const string RuleValue = "AutoPrihlasenie";
    public const string WinlogonKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon";

    public static string? Rule(string key = RuleKey)
    {
        using var k = Registry.LocalMachine.OpenSubKey(key);
        return k?.GetValue(RuleValue) as string is { Length: > 0 } name ? name : null;
    }

    public static void SetRule(string? account, string key = RuleKey)
    {
        using var k = Registry.LocalMachine.CreateSubKey(key);
        if (account is null)
        {
            k.DeleteValue(RuleValue, throwOnMissingValue: false);
        }
        else
        {
            k.SetValue(RuleValue, account, RegistryValueKind.String);
        }
    }

    /// <summary>One look: Windows signs in by itself to the account of the rule only when its profile is ready.
    /// <paramref name="pending"/> says the profile of the account still waits for cleaning. Returns the state set (for
    /// the journal: null when nothing changed).</summary>
    public static string? Update(Func<string, bool> pending)
    {
        using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var winlogon = root.OpenSubKey(WinlogonKey, writable: true) ?? throw new InvalidOperationException("Chýba nastavenie prihlasovania Windows.");
        var account = Rule();
        if (account is null)
        {
            // only an automatic sign-in POLARIS set is taken away (an admin's own setting with a password stays)
            if (winlogon.GetValue("AutoAdminLogon") as string == "1" && winlogon.GetValue("DefaultPassword") as string == ""
                && Equals(winlogon.GetValue(PolarisMark), winlogon.GetValue("DefaultUserName")))
            {
                winlogon.SetValue("AutoAdminLogon", "0", RegistryValueKind.String);
                winlogon.DeleteValue("DefaultPassword", throwOnMissingValue: false);
                winlogon.DeleteValue(PolarisMark, throwOnMissingValue: false);
                return "automatické prihlásenie vypnuté";
            }

            return null;
        }

        var ready = Profiles.SidOf(account) is { } sid && !Profiles.IsAdministrator(account) && !Profiles.State(sid).Loaded && !pending(account);
        var changed = Set(winlogon, "DefaultUserName", account)
            | Set(winlogon, "DefaultDomainName", Environment.MachineName)
            | Set(winlogon, "DefaultPassword", "")
            | Set(winlogon, PolarisMark, account)
            | Set(winlogon, "AutoAdminLogon", ready ? "1" : "0");
        return changed ? (ready ? $"po štarte sa PC prihlási samo do konta {account}" : $"automatické prihlásenie do konta {account} počká, kým bude profil pripravený") : null;
    }

    /// <summary>Which account POLARIS set for the automatic sign-in (so it never takes away one it did not set).</summary>
    private const string PolarisMark = "PolarisAutoPrihlasenie";

    private static bool Set(RegistryKey key, string name, string value)
    {
        if (key.GetValue(name) as string == value)
        {
            return false;
        }

        key.SetValue(name, value, RegistryValueKind.String);
        return true;
    }
}
