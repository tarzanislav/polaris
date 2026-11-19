using System.Buffers.Binary;
using System.Text;

namespace Polaris.Recipes;

/// <summary>One setting in a file of the local group policy (Registry.pol): key under the machine or the user part,
/// value, type of Windows and its bytes.</summary>
public sealed record PolicyEntry(string Key, string Value, uint Type, byte[] Data);

/// <summary>The file Registry.pol as Windows keeps it (format "PReg", version 1: for every setting
/// [key;value;type;size;data] in UTF-16). The editor of group policies writes it; the service of group policy of
/// Windows sets the registry from it – also values that protections of Windows let only Windows itself write.</summary>
public static class PolicyFile
{
    public const uint Signature = 0x67655250; // "PReg"
    public const uint Version = 1;

    public const uint RegSz = 1;
    public const uint RegExpandSz = 2;
    public const uint RegBinary = 3;
    public const uint RegDword = 4;
    public const uint RegMultiSz = 7;
    public const uint RegQword = 11;

    public static List<PolicyEntry> Read(byte[] file)
    {
        if (file.Length == 0)
        {
            return [];
        }

        if (file.Length < 8 || BinaryPrimitives.ReadUInt32LittleEndian(file) != Signature || BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(4)) != Version)
        {
            throw new InvalidDataException("Súbor politiky nie je Registry.pol (PReg 1).");
        }

        var entries = new List<PolicyEntry>();
        var at = 8;
        while (at < file.Length)
        {
            Expect(file, ref at, '[');
            var key = Text(file, ref at);
            Expect(file, ref at, ';');
            var value = Text(file, ref at);
            Expect(file, ref at, ';');
            var type = Number(file, ref at);
            Expect(file, ref at, ';');
            var size = Number(file, ref at);
            Expect(file, ref at, ';');
            if (size > file.Length - at)
            {
                throw new InvalidDataException("Súbor politiky je skrátený.");
            }

            var data = file.AsSpan(at, (int)size).ToArray();
            at += (int)size;
            Expect(file, ref at, ']');
            entries.Add(new PolicyEntry(key, value, type, data));
        }

        return entries;
    }

    public static byte[] Write(IEnumerable<PolicyEntry> entries)
    {
        using var stream = new MemoryStream();
        Span<byte> four = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(four, Signature);
        stream.Write(four);
        BinaryPrimitives.WriteUInt32LittleEndian(four, Version);
        stream.Write(four);
        foreach (var e in entries)
        {
            Char(stream, '[');
            Chars(stream, e.Key);
            Char(stream, ';');
            Chars(stream, e.Value);
            Char(stream, ';');
            BinaryPrimitives.WriteUInt32LittleEndian(four, e.Type);
            stream.Write(four);
            Char(stream, ';');
            BinaryPrimitives.WriteUInt32LittleEndian(four, (uint)e.Data.Length);
            stream.Write(four);
            Char(stream, ';');
            stream.Write(e.Data);
            Char(stream, ']');
        }

        return stream.ToArray();
    }

    /// <summary>The entries with one value set (replaced when there, else added at the end).</summary>
    public static List<PolicyEntry> Set(IEnumerable<PolicyEntry> entries, PolicyEntry entry)
    {
        var list = Without(entries, entry.Key, entry.Value);
        list.Add(entry);
        return list;
    }

    public static List<PolicyEntry> Without(IEnumerable<PolicyEntry> entries, string key, string value) =>
        [.. entries.Where(e => !Same(e, key, value))];

    public static PolicyEntry? Find(IEnumerable<PolicyEntry> entries, string key, string value) =>
        entries.LastOrDefault(e => Same(e, key, value));

    /// <summary>A value of a recipe as an entry: its type and bytes as the registry keeps them.</summary>
    public static PolicyEntry Entry(string key, string value, RegistryKind kind, string data)
    {
        var parsed = RegistryData.Parse(kind, data) ?? throw new InvalidDataException($"Neplatné údaje „{data}“ pre {kind}.");
        return kind switch
        {
            RegistryKind.DWord => new PolicyEntry(key, value, RegDword, Le32((uint)parsed)),
            RegistryKind.QWord => new PolicyEntry(key, value, RegQword, Le64((ulong)parsed)),
            RegistryKind.Binary => new PolicyEntry(key, value, RegBinary, (byte[])parsed),
            RegistryKind.MultiString => new PolicyEntry(key, value, RegMultiSz, Utf16(string.Concat(((string[])parsed).Select(s => s + "\0")) + "\0")),
            RegistryKind.ExpandString => new PolicyEntry(key, value, RegExpandSz, Utf16((string)parsed + "\0")),
            _ => new PolicyEntry(key, value, RegSz, Utf16((string)parsed + "\0")),
        };
    }

    private static bool Same(PolicyEntry e, string key, string value) =>
        string.Equals(e.Key.Trim('\\'), key.Trim('\\'), StringComparison.OrdinalIgnoreCase) && string.Equals(e.Value, value, StringComparison.OrdinalIgnoreCase);

    private static byte[] Le32(uint v)
    {
        var b = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(b, v);
        return b;
    }

    private static byte[] Le64(ulong v)
    {
        var b = new byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(b, v);
        return b;
    }

    private static byte[] Utf16(string s) => Encoding.Unicode.GetBytes(s);

    private static void Char(Stream s, char c)
    {
        s.WriteByte((byte)c);
        s.WriteByte((byte)(c >> 8));
    }

    private static void Chars(Stream s, string text)
    {
        s.Write(Encoding.Unicode.GetBytes(text));
        Char(s, '\0');
    }

    private static void Expect(byte[] f, ref int at, char c)
    {
        if (at + 2 > f.Length || BinaryPrimitives.ReadUInt16LittleEndian(f.AsSpan(at)) != c)
        {
            throw new InvalidDataException($"Súbor politiky je poškodený (pri bajte {at} čakám „{c}“).");
        }

        at += 2;
    }

    private static string Text(byte[] f, ref int at)
    {
        var start = at;
        while (at + 2 <= f.Length && BinaryPrimitives.ReadUInt16LittleEndian(f.AsSpan(at)) != 0)
        {
            at += 2;
        }

        if (at + 2 > f.Length)
        {
            throw new InvalidDataException("Súbor politiky je skrátený.");
        }

        var text = Encoding.Unicode.GetString(f, start, at - start);
        at += 2;
        return text;
    }

    private static uint Number(byte[] f, ref int at)
    {
        if (at + 4 > f.Length)
        {
            throw new InvalidDataException("Súbor politiky je skrátený.");
        }

        var n = BinaryPrimitives.ReadUInt32LittleEndian(f.AsSpan(at));
        at += 4;
        return n;
    }
}

/// <summary>gpt.ini of the local group policy: its version (machine in the low half, user in the high half – it must
/// grow for Windows to read Registry.pol again) and the lists of extensions that tell Windows a part is in use.</summary>
public static class PolicyIni
{
    /// <summary>The extension of the registry part and the tool that wrote it (machine and user).</summary>
    public const string MachineRegistry = "[{35378EAC-683F-11D2-A89A-00C04FBBCFA2}{D02B1F72-3407-48AE-BA88-E8213C6761F1}]";
    public const string UserRegistry = "[{35378EAC-683F-11D2-A89A-00C04FBBCFA2}{D02B1F73-3407-48AE-BA88-E8213C6761F1}]";

    /// <summary>The text of gpt.ini after a change of the machine (or the user) part.</summary>
    public static string Bump(string ini, bool user)
    {
        var lines = ini.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').Where(l => l.Trim().Length > 0).ToList();
        if (!lines.Any(l => l.Trim().Equals("[General]", StringComparison.OrdinalIgnoreCase)))
        {
            lines.Insert(0, "[General]");
        }

        var versionAt = lines.FindIndex(l => l.StartsWith("Version=", StringComparison.OrdinalIgnoreCase));
        var version = versionAt >= 0 && uint.TryParse(lines[versionAt][8..].Trim(), out var v) ? v : 0;
        var machine = version & 0xFFFF;
        var userPart = version >> 16;
        if (user)
        {
            userPart = (userPart + 1) & 0xFFFF;
        }
        else
        {
            machine = (machine + 1) & 0xFFFF;
        }

        var line = "Version=" + ((userPart << 16) | machine).ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (versionAt >= 0)
        {
            lines[versionAt] = line;
        }
        else
        {
            lines.Add(line);
        }

        var name = user ? "gPCUserExtensionNames" : "gPCMachineExtensionNames";
        var wanted = user ? UserRegistry : MachineRegistry;
        var namesAt = lines.FindIndex(l => l.StartsWith(name + "=", StringComparison.OrdinalIgnoreCase));
        if (namesAt < 0)
        {
            lines.Insert(1, name + "=" + wanted);
        }
        else if (!lines[namesAt].Contains(wanted, StringComparison.OrdinalIgnoreCase))
        {
            // the groups [{extension}{tool}] stay sorted by the extension, as Windows writes them
            var groups = lines[namesAt][(name.Length + 1)..].Split(']', StringSplitOptions.RemoveEmptyEntries).Select(g => g.Trim() + "]").Where(g => g.StartsWith('[')).ToList();
            groups.Add(wanted);
            lines[namesAt] = name + "=" + string.Concat(groups.OrderBy(g => g, StringComparer.OrdinalIgnoreCase));
        }

        return string.Join("\r\n", lines) + "\r\n";
    }
}

/// <summary>A policy of users (under Software\Policies) in the user part of the local group policy: Windows sets it at
/// every sign-in of every user – also in profiles that already exist (the trezor of a pupil), where the template of new
/// profiles no longer reaches. Part of a user-registry element with a policy path.</summary>
public sealed class UserPolicyElement : Element
{
    public string Path { get; set; } = "";

    public string Name { get; set; } = "";

    public RegistryKind Kind { get; set; } = RegistryKind.DWord;

    public string Data { get; set; } = "";

    public override string Key => "user-policy:" + Path.Trim('\\').ToLowerInvariant() + "|" + Name.ToLowerInvariant();

    public override string Title => $"politika používateľov {Path.Trim('\\')}\\{Name}";

    public static bool IsPolicyPath(string path) => path.Trim('\\').StartsWith(@"Software\Policies\", StringComparison.OrdinalIgnoreCase);
}
