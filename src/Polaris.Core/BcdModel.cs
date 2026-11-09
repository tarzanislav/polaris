using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace Polaris.Core;

/// <summary>Identifiers of the boot configuration (BCD) as Windows stores them in its registry form. POLARIS reads the
/// boot configuration only from this structured form – never from the translated text of bcdedit.</summary>
public static class BcdModel
{
    public static Guid BootManager { get; } = new("9dea862c-5cdd-4e70-acc1-f32b344d4795");

    public const uint ApplicationDevice = 0x11000001;
    public const uint ApplicationPath = 0x12000002;
    public const uint Description = 0x12000004;
    public const uint OsDevice = 0x21000001;
    public const uint SystemRoot = 0x22000002;
    public const uint DefaultObject = 0x23000003;
    public const uint DisplayOrder = 0x24000001;
    public const uint BootSequence = 0x24000002;
    public const uint Timeout = 0x25000004;

    public static string ElementKey(uint type) => type.ToString("X8", CultureInfo.InvariantCulture);

    public static string ObjectKey(Guid id) => id.ToString("B");

    /// <summary>Text form for bcdedit arguments.</summary>
    public static string Arg(Guid id) => id.ToString("B");

    public static List<Guid> ParseList(IEnumerable<string>? values)
    {
        var list = new List<Guid>();
        if (values is null)
        {
            return list;
        }

        foreach (var value in values)
        {
            if (Guid.TryParse(value, out var id))
            {
                list.Add(id);
            }
        }

        return list;
    }

    public static long? ParseInteger(byte[]? value) =>
        value is { Length: >= 8 } ? BinaryPrimitives.ReadInt64LittleEndian(value) : null;

    /// <summary>Does the device element point to this file (e.g. \POLARIS\Sklad\relacia-A.vhdx)? The device is a binary
    /// structure; the path inside it is stored in UTF-16.</summary>
    public static bool DeviceContainsPath(byte[]? device, string path)
    {
        if (device is null || path.Length == 0)
        {
            return false;
        }

        var text = Encoding.Unicode.GetString(device, 0, device.Length & ~1);
        return text.Contains(path, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Finds the only identifier in a text (the answer of "bcdedit /copy"). The rest of the text may be in any
    /// language; an identifier looks the same in all of them.</summary>
    public static Guid SingleGuidIn(string text)
    {
        var found = new List<Guid>();
        for (var i = 0; i + 38 <= text.Length; i++)
        {
            if (text[i] == '{' && text[i + 37] == '}' && Guid.TryParseExact(text.AsSpan(i, 38), "B", out var id))
            {
                found.Add(id);
                i += 37;
            }
        }

        return found.Count == 1
            ? found[0]
            : throw new InvalidDataException($"Očakával som jeden identifikátor, našiel som {found.Count}: {text.Trim()}");
    }
}
