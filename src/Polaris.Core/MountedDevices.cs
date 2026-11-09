namespace Polaris.Core;

/// <summary>Values of HKLM\SYSTEM\MountedDevices: which partition a drive letter belongs to. For a GPT partition the
/// value is the text "DMIO:ID:" followed by the 16 bytes of the partition identifier.</summary>
public static class MountedDevices
{
    private static ReadOnlySpan<byte> Prefix => "DMIO:ID:"u8;

    public static byte[] GptValue(Guid partitionId)
    {
        var value = new byte[24];
        Prefix.CopyTo(value);
        partitionId.TryWriteBytes(value.AsSpan(8));
        return value;
    }

    public static bool TryReadGpt(ReadOnlySpan<byte> value, out Guid partitionId)
    {
        if (value.Length == 24 && value[..8].SequenceEqual(Prefix))
        {
            partitionId = new Guid(value[8..]);
            return true;
        }

        partitionId = Guid.Empty;
        return false;
    }
}
