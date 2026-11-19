using System.Buffers.Binary;

namespace Polaris.Recipes;

/// <summary>The pinned programs of Start for new profiles taken from the trezor of a shared account: the pins the
/// administrator arranged when preparing the pupil's profile go into the template of profiles, so a new account (a
/// teacher's, say) starts with them instead of the pins and offers Windows brings. Windows 11 Pro has no policy for this
/// outside a domain or Intune; the file of Start is copied as it is (Windows does not describe it – when it keeps it
/// otherwise one day, POLARIS says so instead of guessing). present false = the template is without it again.</summary>
public sealed class StartLayoutElement : Element
{
    /// <summary>The account whose trezor holds the layout (e.g. ziak).</summary>
    public string From { get; set; } = "";

    public override string Key => "start-layout";

    public override string Title => $"pripnuté v Štarte pre nové profily z trezoru konta {From}";
}

public static class StartLayoutRules
{
    /// <summary>Where Start keeps the pins of a profile (relative to the profile folder).</summary>
    public const string Folder = @"AppData\Local\Packages\Microsoft.Windows.StartMenuExperienceHost_cw5n1h2txyewy\LocalState";

    /// <summary>The files of the pins (Windows 11: start2.bin; older builds start.bin; a later one may add a number).</summary>
    public static bool IsLayoutFile(string relativePath)
    {
        // the trezor writes paths with \ on every system
        var path = relativePath.Replace('/', '\\');
        var cut = path.LastIndexOf('\\');
        var parent = cut < 0 ? "" : path[..cut];
        var name = path[(cut + 1)..];
        return string.Equals(parent, Folder, StringComparison.OrdinalIgnoreCase)
            && name.StartsWith("start", StringComparison.OrdinalIgnoreCase)
            && name.EndsWith(".bin", StringComparison.OrdinalIgnoreCase)
            && name.Length <= 12
            && name[5..^4].All(char.IsAsciiDigit);
    }

    public const long Largest = 16L * 1024 * 1024;

    public static IEnumerable<string> Problems(StartLayoutElement s)
    {
        var from = s.From.Trim();
        if (!AccountRules.NamePattern().IsMatch(from))
        {
            yield return "from: meno lokálneho konta s trezorom profilu (napr. ziak).";
        }
        else if (SessionPolicy.NeverClean.Contains(from))
        {
            yield return $"konto „{from}“ je vstavané konto Windows – trezor nemá.";
        }
    }
}

/// <summary>The streams one file has in the output of BackupRead (WIN32_STREAM_ID: id, attributes, size, length of the
/// name, the name, then the bytes) – the trezor keeps each file so.</summary>
public static class BackupStreams
{
    public const uint Data = 1;
    private const int Header = 20;

    /// <summary>The bytes of the file itself (its unnamed data stream); empty for a file without data.</summary>
    public static byte[] FileData(ReadOnlySpan<byte> record)
    {
        var at = 0;
        while (at < record.Length)
        {
            if (record.Length - at < Header)
            {
                throw new InvalidDataException("Záznam súboru v trezore je skrátený.");
            }

            var id = BinaryPrimitives.ReadUInt32LittleEndian(record[at..]);
            var size = BinaryPrimitives.ReadInt64LittleEndian(record[(at + 8)..]);
            var nameSize = BinaryPrimitives.ReadUInt32LittleEndian(record[(at + 16)..]);
            at += Header;
            if (nameSize > record.Length - at || size < 0 || size > record.Length - at - nameSize)
            {
                throw new InvalidDataException("Záznam súboru v trezore je poškodený.");
            }

            at += (int)nameSize;
            if (id == Data && nameSize == 0)
            {
                return record.Slice(at, (int)size).ToArray();
            }

            at += (int)size;
        }

        return [];
    }
}
