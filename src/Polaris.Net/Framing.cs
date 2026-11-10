using System.Buffers.Binary;
using System.Text;

namespace Polaris.Net;

/// <summary>The exact bytes that get signed: a fixed 8-byte tag, then every field as its length (4 bytes) and its UTF-8
/// text. Because each field carries its own length, no text – not even one with a new line inside – can shift into the
/// next field, so two different messages never give the same bytes.</summary>
public static class Framing
{
    public static byte[] Build(ReadOnlySpan<byte> tag, params string[] fields)
    {
        if (tag.Length != 8)
        {
            throw new ArgumentException("Značka má presne 8 bajtov.", nameof(tag));
        }

        var encoded = new byte[fields.Length][];
        var size = 8;
        for (var i = 0; i < fields.Length; i++)
        {
            encoded[i] = Encoding.UTF8.GetBytes(fields[i]);
            size += 4 + encoded[i].Length;
        }

        var bytes = new byte[size];
        tag.CopyTo(bytes);
        var at = 8;
        foreach (var field in encoded)
        {
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(at), field.Length);
            field.CopyTo(bytes, at + 4);
            at += 4 + field.Length;
        }

        return bytes;
    }
}
