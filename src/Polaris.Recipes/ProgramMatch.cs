namespace Polaris.Recipes;

/// <summary>How a program element recognises entries in the list of installed programs, and how an uninstall command
/// from that list is split into the program and its arguments. Plain text work – tested on any system.</summary>
public static class ProgramMatch
{
    public static bool Matches(ProgramElement p, string displayName)
    {
        var wanted = p.DisplayName.Trim();
        var name = displayName.Trim();
        return wanted.Length > 0 && (p.Match == NameMatch.Exact
            ? name.Equals(wanted, StringComparison.OrdinalIgnoreCase)
            : name.StartsWith(wanted, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Whether the version in the list of installed programs is the wanted one: the same, or the same with more
    /// parts (WinGet "25.8.3" and Windows "25.8.3.2"). An entry without a version cannot be judged and passes.</summary>
    public static bool VersionMatches(string? wanted, string? installed)
    {
        static string Clean(string v) => v.Trim().TrimStart('v', 'V');
        if (wanted is not { Length: > 0 } || installed is not { Length: > 0 })
        {
            return true;
        }

        var w = Clean(wanted);
        var i = Clean(installed);
        return i.Equals(w, StringComparison.OrdinalIgnoreCase) || i.StartsWith(w + ".", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Splits "C:\…\uninst.exe" /S or C:\…\uninst.exe /S into the program and the rest; null when no program
    /// can be found in the text.</summary>
    public static (string Program, string Arguments)? SplitCommand(string? command)
    {
        var text = command?.Trim() ?? "";
        if (text.Length == 0)
        {
            return null;
        }

        if (text[0] == '"')
        {
            var close = text.IndexOf('"', 1);
            return close > 1 ? (text[1..close], text[(close + 1)..].Trim()) : null;
        }

        var exe = text.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        if (exe < 0)
        {
            return null;
        }

        var end = exe + 4;
        return (text[..end], text[end..].Trim());
    }

    /// <summary>The product code {GUID} of a Windows Installer entry (its key name, or inside an MsiExec command).</summary>
    public static string? MsiProductCode(string keyName, string? uninstallCommand)
    {
        if (Guid.TryParseExact(keyName, "B", out _))
        {
            return keyName;
        }

        var text = uninstallCommand ?? "";
        var open = text.IndexOf('{');
        var close = open >= 0 ? text.IndexOf('}', open) : -1;
        return close > open && Guid.TryParseExact(text[open..(close + 1)], "B", out _) ? text[open..(close + 1)] : null;
    }

    /// <summary>"\"C:\…\app.exe\",0" → C:\…\app.exe; null when it is not an .exe.</summary>
    public static string? IconExe(string? displayIcon)
    {
        if (displayIcon is not { Length: > 0 })
        {
            return null;
        }

        var text = displayIcon.Trim();
        if (text.StartsWith('"') && text.IndexOf('"', 1) is var close and > 1)
        {
            text = text[1..close];
        }
        else if (text.LastIndexOf(',') is var comma and > 0 && int.TryParse(text[(comma + 1)..].Trim(), out _))
        {
            text = text[..comma];
        }

        text = text.Trim();
        return text.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? text : null;
    }
}
