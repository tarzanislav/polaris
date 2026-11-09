using System.Buffers.Binary;
using System.Text;
using Polaris.Core;

namespace Polaris.Tests;

public static class FormatChecks
{
    [Check]
    public static void BitmapRunsAcrossPieces()
    {
        // clusters 0-3 used, 4-9 free, 10-25 used (crosses the piece border at 16), 26-31 free, 32 used
        var bits = new bool[40];
        for (var i = 0; i < 4; i++)
        {
            bits[i] = true;
        }

        for (var i = 10; i < 26; i++)
        {
            bits[i] = true;
        }

        bits[32] = true;
        var bytes = new byte[5];
        for (var i = 0; i < bits.Length; i++)
        {
            if (bits[i])
            {
                bytes[i / 8] |= (byte)(1 << (i % 8));
            }
        }

        var builder = new ClusterRunBuilder();
        builder.Add(bytes.AsSpan(0, 2), 0, 16);
        builder.Add(bytes.AsSpan(2, 3), 16, 24);
        var runs = builder.Finish();
        Expect.Sequence([new ClusterRun(0, 4), new ClusterRun(10, 16), new ClusterRun(32, 1)], runs, "behy");
        Expect.Equal(21L, builder.UsedClusters, "použité klastre");
    }

    [Check]
    public static void BitmapFastPathsAgreeWithSlowPath()
    {
        var random = new Random(7);
        var bytes = new byte[4096];
        for (var i = 0; i < bytes.Length; i++)
        {
            bytes[i] = random.Next(4) switch { 0 => 0, 1 => 0xFF, _ => (byte)random.Next(256) };
        }

        var fast = new ClusterRunBuilder();
        fast.Add(bytes, 0, bytes.Length * 8L - 3);
        var slow = new List<ClusterRun>();
        long start = -1;
        for (long i = 0; i < bytes.Length * 8L - 3; i++)
        {
            var used = (bytes[i / 8] >> (int)(i % 8) & 1) != 0;
            if (used && start < 0)
            {
                start = i;
            }
            else if (!used && start >= 0)
            {
                slow.Add(new ClusterRun(start, i - start));
                start = -1;
            }
        }

        if (start >= 0)
        {
            slow.Add(new ClusterRun(start, bytes.Length * 8L - 3 - start));
        }

        Expect.Sequence(slow, fast.Finish(), "rovnaké behy");
    }

    [Check]
    public static void BitmapPiecesMustFollow()
    {
        var builder = new ClusterRunBuilder();
        builder.Add(new byte[1], 0, 8);
        Expect.Throws<ArgumentException>(() => builder.Add(new byte[1], 16, 8), "medzera v bitmape");
    }

    [Check]
    public static void ChunksSplitLongRuns()
    {
        var chunks = ClusterRuns.Chunks([new ClusterRun(5, 10), new ClusterRun(100, 3)], 4).ToList();
        Expect.Sequence(
            [new ClusterRun(5, 4), new ClusterRun(9, 4), new ClusterRun(13, 2), new ClusterRun(100, 3)],
            chunks,
            "kúsky");
    }

    [Check]
    public static void NtfsBootSectorFields()
    {
        var sector = new byte[512];
        Encoding.ASCII.GetBytes(NtfsBootSector.NtfsOem).CopyTo(sector, 3);
        BinaryPrimitives.WriteUInt16LittleEndian(sector.AsSpan(0x0B), 512);
        sector[0x0D] = 8;
        BinaryPrimitives.WriteUInt32LittleEndian(sector.AsSpan(0x1C), 2048);
        BinaryPrimitives.WriteInt64LittleEndian(sector.AsSpan(0x28), 1_000_000);
        sector[510] = 0x55;
        sector[511] = 0xAA;
        var parsed = NtfsBootSector.Parse(sector);
        Expect.True(parsed.IsNtfs && !parsed.IsBitLocker, "NTFS");
        Expect.Equal(4096L, parsed.BytesPerCluster, "veľkosť klastra");
        Expect.Equal(2048u, parsed.HiddenSectors, "skryté sektory");
        Expect.Equal(1_000_000L, parsed.TotalSectors, "sektory");
        Expect.True(NtfsBootSector.HasSignature(sector), "podpis 55AA");
        NtfsBootSector.SetHiddenSectors(sector, 4096);
        Expect.Equal(4096u, NtfsBootSector.Parse(sector).HiddenSectors, "nové skryté sektory");

        sector[0x0D] = 0xF4; // 2^12 sectors per cluster
        Expect.Equal(4096L, NtfsBootSector.Parse(sector).SectorsPerCluster, "záporná mocnina dvoch");

        Encoding.ASCII.GetBytes(NtfsBootSector.BitLockerOem).CopyTo(sector, 3);
        Expect.True(NtfsBootSector.Parse(sector).IsBitLocker, "BitLocker");
    }

    [Check]
    public static void MountedDevicesValue()
    {
        var id = new Guid("a1b2c3d4-0102-0304-0506-0708090a0b0c");
        var value = MountedDevices.GptValue(id);
        Expect.Equal(24, value.Length, "dĺžka");
        Expect.Equal("DMIO:ID:", Encoding.ASCII.GetString(value, 0, 8), "predpona");
        Expect.True(MountedDevices.TryReadGpt(value, out var back) && back == id, "spätne");
        Expect.True(!MountedDevices.TryReadGpt(new byte[12], out _), "iná hodnota (MBR)");
    }

    [Check]
    public static void BcdHelpers()
    {
        Expect.Equal("23000003", BcdModel.ElementKey(BcdModel.DefaultObject), "kľúč prvku");
        var id = Guid.NewGuid();
        Expect.Equal(id, BcdModel.SingleGuidIn($"Položka bola úspešne skopírovaná do {id:B}."), "jeden identifikátor (slovensky)");
        Expect.Equal(id, BcdModel.SingleGuidIn($"The entry was successfully copied to {id:B}."), "jeden identifikátor (anglicky)");
        Expect.Throws<InvalidDataException>(() => BcdModel.SingleGuidIn("bez identifikátora"), "žiadny identifikátor");
        Expect.Throws<InvalidDataException>(() => BcdModel.SingleGuidIn($"{id:B} {Guid.NewGuid():B}"), "dva identifikátory");
        var device = new byte[200];
        Encoding.Unicode.GetBytes(@"\POLARIS\Sklad\relacia-B.vhdx").CopyTo(device, 72);
        Expect.True(BcdModel.DeviceContainsPath(device, @"\polaris\sklad\relacia-b.vhdx"), "cesta v zariadení");
        Expect.True(!BcdModel.DeviceContainsPath(device, @"\POLARIS\Sklad\relacia-A.vhdx"), "iná cesta");
        Expect.Equal(30L, BcdModel.ParseInteger([30, 0, 0, 0, 0, 0, 0, 0]), "číslo");
        Expect.Sequence([id], BcdModel.ParseList([id.ToString("B"), "nezmysel"]), "zoznam");
    }

    [Check]
    public static void SlovakNumbers()
    {
        Expect.Equal("47,9 GB", Format.Gb(47_888_400_025), "GB");
        Expect.Equal("13 min 16 s", Format.Duration(TimeSpan.FromSeconds(796)), "trvanie");
        Expect.Equal("3 štarty", Format.Count(3, "štart", "štarty", "štartov"), "3");
        Expect.Equal("5 štartov", Format.Count(5, "štart", "štarty", "štartov"), "5");
    }

    [Check]
    public static void EditionsAndSpace()
    {
        Expect.True(CheckRules.EditionSupportsNativeBoot("Professional"), "Pro");
        Expect.True(CheckRules.EditionSupportsNativeBoot("Education"), "Education");
        Expect.True(CheckRules.EditionSupportsNativeBoot("ProfessionalEducation"), "Pro Education");
        Expect.True(CheckRules.EditionSupportsNativeBoot("Enterprise"), "Enterprise");
        Expect.True(!CheckRules.EditionSupportsNativeBoot("Core"), "Home");
        Expect.True(!CheckRules.EditionSupportsNativeBoot("CoreSingleLanguage"), "Home jeden jazyk");
        Expect.True(!CheckRules.EditionSupportsNativeBoot(null), "neznáma");
        Expect.True(CheckRules.RequiredFreeBytes(30_000_000_000, 8_000_000_000) > 50_000_000_000, "miesto");
    }
}
