using System.Globalization;

namespace Polaris.Recipes;

/// <summary>Basic settings of the PC as a whole: its workgroup and its time zone. Only what is filled is managed. The
/// element cannot be removed – a PC always has some workgroup and some time zone.</summary>
public sealed class ComputerElement : Element
{
    /// <summary>Workgroup (e.g. SKOLA – the same name as Samba on the server has). A PC in a domain is never taken out.</summary>
    public string? Workgroup { get; set; }

    /// <summary>Time zone by its name in Windows (e.g. "Central Europe Standard Time").</summary>
    public string? TimeZone { get; set; }

    public override string Key => "computer";

    public override string Title => "počítač (" + string.Join(", ", Parts()) + ")";

    private IEnumerable<string> Parts()
    {
        if (Workgroup is { Length: > 0 } w)
        {
            yield return "pracovná skupina " + w;
        }

        if (TimeZone is { Length: > 0 } t)
        {
            yield return "časové pásmo " + t;
        }
    }
}

/// <summary>Windows Update by the policies of Windows (the same values the editor of group policies writes). What is not
/// filled is not set – the policy value is removed and Windows decides itself. Absent = all these policies removed.</summary>
public sealed class UpdatesElement : Element
{
    /// <summary>Active hours: the hour (0–23) from which Windows does not restart the PC for updates …</summary>
    public int? ActiveHoursStart { get; set; }

    /// <summary>… and the hour it may again. At most 18 hours apart (a limit of Windows).</summary>
    public int? ActiveHoursEnd { get; set; }

    /// <summary>Feature updates (the yearly versions) come this many days later (0–365).</summary>
    public int? DeferFeatureDays { get; set; }

    /// <summary>Quality updates (the monthly ones) come this many days later (0–30).</summary>
    public int? DeferQualityDays { get; set; }

    /// <summary>The PC stays on this version of Windows 11 (e.g. 25H2) until the recipe says another.</summary>
    public string? TargetRelease { get; set; }

    /// <summary>Drivers do not come through Windows Update.</summary>
    public bool ExcludeDrivers { get; set; }

    public override string Key => "updates";

    public override string Title => "aktualizácie Windows";
}

public enum PowerAction
{
    Nothing,
    Sleep,
    Hibernate,
    ShutDown,
}

/// <summary>Power settings by the policies of Windows (Policies\Microsoft\Power), so a user cannot change them. They hold
/// in the socket and on battery alike. Times in minutes, 0 = never. What is not filled is not set.</summary>
public sealed class PowerElement : Element
{
    public int? SleepMinutes { get; set; }

    public int? DisplayMinutes { get; set; }

    public int? HibernateMinutes { get; set; }

    public PowerAction? PowerButton { get; set; }

    /// <summary>Closing the lid of a notebook.</summary>
    public PowerAction? LidClose { get; set; }

    /// <summary>Ask for the password after waking up.</summary>
    public bool? WakePassword { get; set; }

    /// <summary>Fast startup of Windows (a half hibernation at shutting down). Off in a classroom: a shut-down PC then
    /// really starts anew, and Wake on LAN works more reliably. Not filled = not touched.</summary>
    public bool? FastStartup { get; set; }

    public override string Key => "power";

    public override string Title => "napájanie";
}

/// <summary>The settings above as policy values in the registry: tested on any system, applied by the executor of the
/// registry that already exists.</summary>
public static class SettingsExpansion
{
    public const string UpdatePolicies = @"HKLM\SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate";
    public const string PowerPolicies = @"HKLM\SOFTWARE\Policies\Microsoft\Power\PowerSettings";
    public const string FastStartupKey = @"HKLM\SYSTEM\CurrentControlSet\Control\Session Manager\Power";

    public const int MaxActiveHours = 18;
    public const int MaxDeferFeatureDays = 365;
    public const int MaxDeferQualityDays = 30;

    /// <summary>Longest time of the power settings a recipe may set: one week.</summary>
    public const int MaxMinutes = 7 * 24 * 60;

    // the settings of power by their GUID (the same in every Windows)
    public const string SleepAfter = "29F6C1DB-86DA-48C5-9FDB-F2B67B1F44DA";
    public const string DisplayAfter = "3C0BC021-C8A8-4E07-A973-6B14CBCB2B7E";
    public const string HibernateAfter = "9D7815A6-7EE4-497E-8888-515A05F02364";
    public const string PowerButtonAction = "7648EFA3-DD9C-4E3E-B566-50F929386280";
    public const string LidCloseAction = "5CA83367-6E45-459F-A27B-476B1D01C936";
    public const string PasswordOnWake = "0E796BDB-100D-47D6-A2D5-F7D2DAA51F51";

    public static IEnumerable<Element> Updates(UpdatesElement u)
    {
        var activeHours = u.ActiveHoursStart is not null && u.ActiveHoursEnd is not null;
        yield return Value(u, "aktivne-hodiny", UpdatePolicies, "SetActiveHours", activeHours ? 1u : null);
        yield return Value(u, "aktivne-od", UpdatePolicies, "ActiveHoursStart", activeHours ? (uint?)u.ActiveHoursStart : null);
        yield return Value(u, "aktivne-do", UpdatePolicies, "ActiveHoursEnd", activeHours ? (uint?)u.ActiveHoursEnd : null);
        yield return Value(u, "odklad-verzie", UpdatePolicies, "DeferFeatureUpdates", u.DeferFeatureDays is not null ? 1u : null);
        yield return Value(u, "odklad-verzie-dni", UpdatePolicies, "DeferFeatureUpdatesPeriodInDays", (uint?)u.DeferFeatureDays);
        yield return Value(u, "odklad-mesacne", UpdatePolicies, "DeferQualityUpdates", u.DeferQualityDays is not null ? 1u : null);
        yield return Value(u, "odklad-mesacne-dni", UpdatePolicies, "DeferQualityUpdatesPeriodInDays", (uint?)u.DeferQualityDays);
        var target = u.TargetRelease is { Length: > 0 };
        yield return Value(u, "verzia", UpdatePolicies, "TargetReleaseVersion", target ? 1u : null);
        yield return Text(u, "verzia-produkt", UpdatePolicies, "ProductVersion", target ? "Windows 11" : null);
        yield return Text(u, "verzia-cislo", UpdatePolicies, "TargetReleaseVersionInfo", target ? u.TargetRelease : null);
        yield return Value(u, "bez-ovladacov", UpdatePolicies, "ExcludeWUDriversInQualityUpdate", u.ExcludeDrivers ? 1u : null);
    }

    public static IEnumerable<Element> Power(PowerElement p)
    {
        foreach (var e in Both(p, "spanok", SleepAfter, Seconds(p.SleepMinutes)))
        {
            yield return e;
        }

        foreach (var e in Both(p, "obrazovka", DisplayAfter, Seconds(p.DisplayMinutes)))
        {
            yield return e;
        }

        foreach (var e in Both(p, "hibernacia", HibernateAfter, Seconds(p.HibernateMinutes)))
        {
            yield return e;
        }

        foreach (var e in Both(p, "tlacidlo", PowerButtonAction, (uint?)p.PowerButton))
        {
            yield return e;
        }

        foreach (var e in Both(p, "veko", LidCloseAction, (uint?)p.LidClose))
        {
            yield return e;
        }

        foreach (var e in Both(p, "heslo", PasswordOnWake, p.WakePassword is { } w ? (w ? 1u : 0u) : null))
        {
            yield return e;
        }

        // a value of Windows itself, not a policy: written only when the recipe says so, never removed
        if (p.Present && p.FastStartup is { } fast)
        {
            yield return new RegistryValueElement
            {
                Id = p.Id + ".rychly-start",
                Note = p.Title,
                Path = FastStartupKey,
                Name = "HiberbootEnabled",
                Kind = RegistryKind.DWord,
                Data = fast ? "1" : "0",
            };
        }
    }

    private static uint? Seconds(int? minutes) => minutes is { } m ? (uint)m * 60 : null;

    /// <summary>One setting of power for the socket (AC) and the battery (DC).</summary>
    private static IEnumerable<Element> Both(PowerElement p, string id, string guid, uint? value)
    {
        yield return Value(p, id + "-siet", PowerPolicies + "\\" + guid, "ACSettingIndex", value);
        yield return Value(p, id + "-bateria", PowerPolicies + "\\" + guid, "DCSettingIndex", value);
    }

    private static RegistryValueElement Value(Element owner, string id, string path, string name, uint? value) => new()
    {
        Id = owner.Id + "." + id,
        Present = owner.Present && value is not null,
        Note = owner.Title,
        Path = path,
        Name = name,
        Kind = RegistryKind.DWord,
        Data = value is { } v ? v.ToString(CultureInfo.InvariantCulture) : "",
    };

    private static RegistryValueElement Text(Element owner, string id, string path, string name, string? value) => new()
    {
        Id = owner.Id + "." + id,
        Present = owner.Present && value is not null,
        Note = owner.Title,
        Path = path,
        Name = name,
        Kind = RegistryKind.String,
        Data = value ?? "",
    };
}
