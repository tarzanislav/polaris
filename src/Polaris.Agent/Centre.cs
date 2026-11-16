using System.Runtime.Versioning;
using Microsoft.Win32;
using Polaris.Recipes;

namespace Polaris.Agent;

/// <summary>The link of this PC to the centre: settings and state in HKLM\SOFTWARE\POLARIS\Agent\Centrum (administrators
/// and the agent only), the kept plan and recipes in the agent's protected folder.</summary>
[SupportedOSPlatform("windows")]
public static class Centre
{
    public const string Key = @"SOFTWARE\POLARIS\Agent\Centrum";
    public static readonly TimeSpan FetchEvery = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan TickEvery = TimeSpan.FromMinutes(1);

    public static string Cache => Path.Combine(Packages.AgentFolder, "centrum");

    public static CentreSettings? Settings()
    {
        using var k = Open(false);
        return k?.GetValue("Adresa") is string a && Uri.TryCreate(a, UriKind.Absolute, out var address)
            && k.GetValue("Odtlacok") is string pin && k.GetValue("Skupina") is string group
            ? new CentreSettings(address, pin, group)
            : null;
    }

    public static void Configure(CentreSettings? settings)
    {
        using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        if (settings is null)
        {
            root.DeleteSubKeyTree(Key, throwOnMissingSubKey: false);
            return;
        }

        using var k = root.CreateSubKey(Key, writable: true);
        k.SetValue("Adresa", settings.Address.ToString(), RegistryValueKind.String);
        k.SetValue("Odtlacok", settings.Pin.ToLowerInvariant(), RegistryValueKind.String);
        k.SetValue("Skupina", settings.Group, RegistryValueKind.String);
        k.DeleteValue("Poradie", throwOnMissingValue: false); // another centre or group: its plans start their own count
    }

    public static long Sequence() => Open(false) is { } k ? Using(k, x => x.GetValue("Poradie") is long n ? n : 0) : 0;

    public static void SetSequence(long n) => Using(Open(true)!, k => k.SetValue("Poradie", n, RegistryValueKind.QWord));

    public static (string? Profile, string? Fingerprint, DateTimeOffset? At) Applied() => Open(false) is { } k
        ? Using(k, x => (x.GetValue("Profil") as string, x.GetValue("Recept") as string,
            x.GetValue("Pouzite") is string t && DateTimeOffset.TryParse(t, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out var at) ? at : (DateTimeOffset?)null))
        : (null, null, null);

    public static void SetApplied(string profile, string fingerprint, DateTimeOffset at) => Using(Open(true)!, k =>
    {
        k.SetValue("Profil", profile, RegistryValueKind.String);
        k.SetValue("Recept", fingerprint, RegistryValueKind.String);
        k.SetValue("Pouzite", at.ToString("O", System.Globalization.CultureInfo.InvariantCulture), RegistryValueKind.String);
    });

    public static CentreSync Sync(CentreSettings settings, AgentHost host)
    {
        Directory.CreateDirectory(Cache);
        AgentService.ProtectFolder(Packages.AgentFolder);
        return new CentreSync(settings, Cache, host.Trusted);
    }

    /// <summary>Applies the profile the timetable wants now, when it should; a line for the journal or null.</summary>
    public static RunRecord? Tick(CentreSync sync, AgentHost host, DateTime local, bool force = false)
    {
        if (sync.Plan() is not { } plan || CentrePolicy.Wanted(plan, local) is not { } wanted)
        {
            return null;
        }

        var (profile, fingerprint, at) = Applied();
        var now = host.Clock();
        if (!force && !CentrePolicy.ShouldApply(wanted, profile, fingerprint, at, now))
        {
            return null;
        }

        var recipe = sync.Recipe(wanted.Fingerprint);
        if (recipe is null)
        {
            return new RunRecord
            {
                StartedUtc = now,
                FinishedUtc = now,
                Recipe = wanted.Profile,
                Fingerprint = wanted.Fingerprint,
                Skipped = [$"recept profilu {wanted.Profile} chýba alebo neprešiel kontrolou podpisu – čaká sa na centrum"],
            };
        }

        using var engine = new Engine(host.Handlers(sync.RecipeFolder));
        var run = engine.Apply(engine.Plan(recipe.Elements), recipe, host.Clock);
        SetApplied(wanted.Profile, wanted.Fingerprint, now);
        return run;
    }

    private static RegistryKey? Open(bool writable)
    {
        using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        return writable ? root.CreateSubKey(Key, writable: true) : root.OpenSubKey(Key);
    }

    private static T Using<T>(RegistryKey key, Func<RegistryKey, T> read)
    {
        using (key)
        {
            return read(key);
        }
    }

    private static void Using(RegistryKey key, Action<RegistryKey> write)
    {
        using (key)
        {
            write(key);
        }
    }
}
