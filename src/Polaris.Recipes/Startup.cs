using System.Buffers.Binary;

namespace Polaris.Recipes;

/// <summary>Programs that start by themselves after every sign-in, for all users of the PC: the Run of the PC, its
/// 32-bit Run and the common Startup folder. Every one is switched off the way Task Manager does it (the entry stays,
/// only its "StartupApproved" says off), except those on the list. The agent keeps the list and checks it again every
/// few minutes, so a program installed later does not start by itself either. A name on the list is the name of the
/// entry (as Task Manager shows it in the column of the registry) or of its program file, without .exe; ending with "*"
/// it is the beginning of names. The Windows Security icon is always allowed. Absent (present false) = the entries
/// POLARIS switched off are switched on again.</summary>
public sealed class StartupElement : Element
{
    public List<string> Allow { get; set; } = [];

    public override string Key => "startup";

    public override string Title => "automatické spúšťanie programov";
}

/// <summary>The rules of starting programs by themselves – the same for the agent and the tests.</summary>
public static class StartupRules
{
    /// <summary>Entries never switched off, whatever the recipe says: the icon of Windows Security.</summary>
    public static readonly IReadOnlyList<string> AlwaysAllowed = ["SecurityHealth"];

    public const int MaxAllow = 64;
    public const int MinPrefix = 3;

    /// <summary>Task Manager's mark of a switched-off entry: 3 and the time of switching off.</summary>
    public const byte Off = 3;
    public const byte On = 2;

    /// <summary>The entry may start: its name or its program matches a name on the list.</summary>
    public static bool Allowed(IEnumerable<string> allow, string entry, string command)
    {
        var program = ProgramName(command);
        return AlwaysAllowed.Concat(allow).Any(a => Matches(a, entry) || (program.Length > 0 && Matches(a, program)));
    }

    public static bool Matches(string pattern, string name)
    {
        var p = pattern.Trim();
        return p.EndsWith('*')
            ? name.StartsWith(p[..^1], StringComparison.OrdinalIgnoreCase)
            : string.Equals(p, name, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The program of a command line without its folder and .exe: "C:\Program Files\X\x.exe" /a → x.</summary>
    public static string ProgramName(string command)
    {
        var c = command.Trim();
        string file;
        if (c.StartsWith('"'))
        {
            var end = c.IndexOf('"', 1);
            file = end > 0 ? c[1..end] : c[1..];
        }
        else
        {
            var exe = c.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            file = exe > 0 ? c[..(exe + 4)] : c.Split(' ', 2)[0];
        }

        var name = file.Replace('/', '\\');
        name = name[(name.LastIndexOf('\\') + 1)..];
        return name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
    }

    /// <summary>The state Task Manager keeps: no value or an even first byte = on, an odd one = off.</summary>
    public static bool IsOn(byte[]? approval) => approval is not { Length: > 0 } || (approval[0] & 1) == 0;

    /// <summary>The value of a switched-off entry, as Task Manager writes it.</summary>
    public static byte[] OffValue(DateTimeOffset when)
    {
        var value = new byte[12];
        value[0] = Off;
        BinaryPrimitives.WriteInt64LittleEndian(value.AsSpan(4), when.ToFileTime());
        return value;
    }

    public static IEnumerable<string> Problems(StartupElement s)
    {
        if (s.Allow.Count > MaxAllow)
        {
            yield return $"zoznam povolených má najviac {MaxAllow} mien.";
        }

        foreach (var a in s.Allow)
        {
            var name = a.Trim();
            if (name.Length == 0)
            {
                yield return "prázdne meno v zozname povolených.";
            }
            else if (name.IndexOfAny(['\\', '/', '"', '\0']) >= 0)
            {
                yield return $"„{a}“: meno položky alebo programu, bez priečinka a úvodzoviek.";
            }
            else if (name.EndsWith('*') && name.Length - 1 < MinPrefix)
            {
                yield return $"„{a}“: začiatok mien má aspoň {MinPrefix} znaky – „*“ samo by povolilo všetko (to je bez prvku).";
            }
            else if (name.IndexOf('*') is var star && star >= 0 && star < name.Length - 1)
            {
                yield return $"„{a}“: hviezdička môže byť len na konci.";
            }
        }
    }
}
