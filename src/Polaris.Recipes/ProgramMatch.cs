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
}
