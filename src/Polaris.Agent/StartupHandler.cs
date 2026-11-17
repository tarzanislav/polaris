using System.Runtime.Versioning;
using Microsoft.Win32;
using Polaris.Recipes;

namespace Polaris.Agent;

/// <summary>One program that starts by itself for all users: where it is written, its name and command, and whether
/// Task Manager's mark lets it start.</summary>
public sealed record StartupEntry(string Place, string Name, string Command, bool On)
{
    /// <summary>The name under which POLARIS remembers it: place\name.</summary>
    public string Id => Place + "\\" + Name;
}

/// <summary>The programs that start by themselves for all users, read and switched as Task Manager does: the Run of the
/// PC (Run), its 32-bit Run (Run32) and the common Startup folder (StartupFolder). The mark lives in
/// HKLM\…\Explorer\StartupApproved\{place}; the entry itself is never deleted.</summary>
[SupportedOSPlatform("windows")]
public static class StartupEntries
{
    public const string Run = "Run";
    public const string Run32 = "Run32";
    public const string Folder = "StartupFolder";

    private const string Approved = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\";

    private static readonly Dictionary<string, string> RunKeys = new(StringComparer.Ordinal)
    {
        [Run] = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run",
        [Run32] = @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Run",
    };

    public static string CommonFolder => Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup);

    public static List<StartupEntry> Read()
    {
        using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        var entries = new List<StartupEntry>();
        foreach (var (place, path) in RunKeys)
        {
            using var key = root.OpenSubKey(path);
            foreach (var name in key?.GetValueNames() ?? [])
            {
                if (name.Length > 0 && key!.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) is string command)
                {
                    entries.Add(new StartupEntry(place, name, command, StartupRules.IsOn(Approval(place, name))));
                }
            }
        }

        if (Directory.Exists(CommonFolder))
        {
            foreach (var file in Directory.EnumerateFiles(CommonFolder))
            {
                var name = Path.GetFileName(file);
                if (!name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase))
                {
                    entries.Add(new StartupEntry(Folder, name, Command(file), StartupRules.IsOn(Approval(Folder, name))));
                }
            }
        }

        return entries;
    }

    /// <summary>The mark of an entry as Task Manager keeps it (null = none, the entry starts).</summary>
    public static byte[]? Approval(string place, string name)
    {
        using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var key = root.OpenSubKey(Approved + place);
        return key?.GetValue(name) as byte[];
    }

    /// <summary>Writes the mark (null removes it, and the entry starts again).</summary>
    public static void SetApproval(string place, string name, byte[]? value)
    {
        using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        if (value is null)
        {
            using var existing = root.OpenSubKey(Approved + place, writable: true);
            existing?.DeleteValue(name, throwOnMissingValue: false);
            return;
        }

        using var key = root.CreateSubKey(Approved + place);
        key.SetValue(name, value, RegistryValueKind.Binary);
    }

    /// <summary>The program a file in the Startup folder starts: the target of a shortcut, otherwise the file itself.</summary>
    private static string Command(string file)
    {
        if (!file.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
        {
            return file;
        }

        try
        {
            return Polaris.Windows.Shortcuts.Target(file);
        }
        catch (Exception e) when (e is Polaris.Windows.SystemCallException or IOException or UnauthorizedAccessException)
        {
            return "";
        }
    }
}

/// <summary>The mark each entry had before POLARIS first switched it off (HKLM\SOFTWARE\POLARIS\Agent\Spustanie), so that
/// it can be put back: hex of the value, empty for none. The first mark is kept: later changes never overwrite it.</summary>
[SupportedOSPlatform("windows")]
public sealed class StartupMemory(string key = StartupMemory.DefaultKey)
{
    public const string DefaultKey = @"SOFTWARE\POLARIS\Agent\Spustanie";

    public void Remember(StartupEntry entry)
    {
        using var memory = Registry.LocalMachine.CreateSubKey(key);
        if (memory.GetValue(entry.Id) is null)
        {
            var before = StartupEntries.Approval(entry.Place, entry.Name);
            memory.SetValue(entry.Id, before is null ? "" : Convert.ToHexString(before), RegistryValueKind.String);
        }
    }

    /// <summary>place\name → its mark before POLARIS (hex, empty = none).</summary>
    public SortedDictionary<string, string> All()
    {
        using var memory = Registry.LocalMachine.OpenSubKey(key);
        var all = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var id in memory?.GetValueNames() ?? [])
        {
            if (memory!.GetValue(id) is string before)
            {
                all[id] = before;
            }
        }

        return all;
    }

    /// <summary>Puts the original mark back and forgets it; false when POLARIS did not switch the entry off.</summary>
    public bool Restore(string id)
    {
        var all = All();
        var known = all.Keys.FirstOrDefault(k => string.Equals(k, id, StringComparison.OrdinalIgnoreCase))
            ?? all.Keys.FirstOrDefault(k => string.Equals(k[(k.IndexOf('\\') + 1)..], id, StringComparison.OrdinalIgnoreCase));
        if (known is null)
        {
            return false;
        }

        var slash = known.IndexOf('\\');
        var before = all[known];
        StartupEntries.SetApproval(known[..slash], known[(slash + 1)..], before.Length == 0 ? null : Convert.FromHexString(before));
        using var memory = Registry.LocalMachine.CreateSubKey(key);
        memory.DeleteValue(known, throwOnMissingValue: false);
        return true;
    }
}

/// <summary>The list of allowed programs the agent keeps after a recipe (HKLM\SOFTWARE\POLARIS\Agent, value
/// SpustaniePovolene): the service checks it every few minutes, so a program added later does not start either.</summary>
[SupportedOSPlatform("windows")]
public static class StartupRule
{
    public const string Key = @"SOFTWARE\POLARIS\Agent";
    public const string Value = "SpustaniePovolene";

    /// <summary>The kept list; null = no rule (the recipe has no startup element).</summary>
    public static List<string>? Read(string key = Key)
    {
        using var k = Registry.LocalMachine.OpenSubKey(key);
        return k?.GetValue(Value) is string[] allow ? [.. allow] : null;
    }

    public static void Write(List<string>? allow, string key = Key)
    {
        using var k = Registry.LocalMachine.CreateSubKey(key);
        if (allow is null)
        {
            k.DeleteValue(Value, throwOnMissingValue: false);
        }
        else
        {
            k.SetValue(Value, allow.ToArray(), RegistryValueKind.MultiString);
        }
    }
}

/// <summary>Switches off every program that starts by itself and is not allowed, and switches on again those POLARIS
/// switched off that are allowed now. Returns what it did, for the journal.</summary>
[SupportedOSPlatform("windows")]
public static class StartupWork
{
    public static List<string> Enforce(List<string> allow, StartupMemory memory, DateTimeOffset now)
    {
        var done = new List<string>();
        foreach (var e in Unwanted(allow))
        {
            memory.Remember(e);
            StartupEntries.SetApproval(e.Place, e.Name, StartupRules.OffValue(now));
            done.Add("vypnuté " + e.Name);
        }

        foreach (var id in Back(allow, memory))
        {
            memory.Restore(id);
            done.Add("znova zapnuté " + id[(id.IndexOf('\\') + 1)..]);
        }

        return done;
    }

    /// <summary>Entries that start and are not allowed.</summary>
    public static List<StartupEntry> Unwanted(List<string> allow) =>
        [.. StartupEntries.Read().Where(e => e.On && !StartupRules.Allowed(allow, e.Name, e.Command))];

    /// <summary>Entries POLARIS switched off that the list allows now (or that are gone – their mark is cleaned).</summary>
    public static List<string> Back(List<string> allow, StartupMemory memory)
    {
        var entries = StartupEntries.Read().ToDictionary(e => e.Id, StringComparer.OrdinalIgnoreCase);
        return [.. memory.All().Keys.Where(id => !entries.TryGetValue(id, out var e) || StartupRules.Allowed(allow, e.Name, e.Command))];
    }

    public static string Describe(StartupEntry e) =>
        $"{(e.On ? "✓" : "✗")} {e.Name}  ({e.Place}{(e.Command.Length > 0 ? ": " + StartupRules.ProgramName(e.Command) : "")})";
}

/// <summary>Executor of the startup element: switches off what is not allowed and keeps the list for the service.</summary>
[SupportedOSPlatform("windows")]
public sealed class StartupHandler(StartupMemory? memory = null, string ruleKey = StartupRule.Key, Func<DateTimeOffset>? clock = null) : IElementHandler
{
    private readonly StartupMemory _memory = memory ?? new StartupMemory();
    private readonly Func<DateTimeOffset> _clock = clock ?? (() => DateTimeOffset.UtcNow);

    public bool CanHandle(Element element) => element is StartupElement;

    public Observation Observe(Element element)
    {
        var s = (StartupElement)element;
        if (!s.Present)
        {
            var held = _memory.All().Count;
            return held > 0 || StartupRule.Read(ruleKey) is not null
                ? new Observation(Observed.Present, $"POLARIS vypol {held} programov")
                : new Observation(Observed.Absent);
        }

        var unwanted = StartupWork.Unwanted(s.Allow);
        var back = StartupWork.Back(s.Allow, _memory);
        var rule = StartupRule.Read(ruleKey);
        var ruleSame = rule is not null && rule.SequenceEqual(s.Allow, StringComparer.Ordinal);
        if (unwanted.Count == 0 && back.Count == 0 && ruleSame)
        {
            return new Observation(Observed.Present, $"spúšťa sa len povolené ({StartupEntries.Read().Count(e => e.On)} programov)");
        }

        var parts = new List<string>();
        if (unwanted.Count > 0)
        {
            parts.Add("spúšťa sa aj " + string.Join(", ", unwanted.Select(e => e.Name)));
        }

        if (back.Count > 0)
        {
            parts.Add("znova zapnúť " + string.Join(", ", back.Select(id => id[(id.IndexOf('\\') + 1)..])));
        }

        if (!ruleSame)
        {
            parts.Add(rule is null ? "služba zoznam ešte nestráži" : "služba stráži iný zoznam");
        }

        return new Observation(rule is null && unwanted.Count == 0 && back.Count == 0 ? Observed.Absent : Observed.Different, string.Join("; ", parts));
    }

    public void Apply(Element element)
    {
        var s = (StartupElement)element;
        if (!s.Present)
        {
            StartupRule.Write(null, ruleKey);
            foreach (var id in _memory.All().Keys)
            {
                _memory.Restore(id);
            }

            return;
        }

        StartupWork.Enforce(s.Allow, _memory, _clock());
        StartupRule.Write(s.Allow, ruleKey);
    }
}
