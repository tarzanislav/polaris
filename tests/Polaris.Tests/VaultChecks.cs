using Polaris.Recipes;

namespace Polaris.Tests;

/// <summary>The rules of the trezor of profiles on any system: what is kept, how a profile is compared with it, the
/// description as JSON and its checks. The real files in WindowsVaultChecks.</summary>
public static class VaultChecks
{
    private static VaultManifest Manifest(params VaultEntry[] entries) => new()
    {
        Account = "ziak",
        Sid = "S-1-5-21-1-2-3-1001",
        ProfileCreated = 1,
        Windows = "26200.1",
        SavedUtc = new DateTimeOffset(2026, 11, 17, 18, 0, 0, TimeSpan.Zero),
        ArchiveLength = entries.Sum(e => e.Length),
        Entries = [.. entries],
    };

    private static long _at;

    private static VaultEntry Entry(string path, VaultKind kind = VaultKind.File, long size = 10, long time = 100, int attributes = 0x20)
    {
        var e = new VaultEntry(path, kind, kind == VaultKind.File ? size : 0, time, kind == VaultKind.Directory ? 0x10 : kind == VaultKind.Link ? 0x410 : attributes, _at, 20, new string('a', 64));
        _at += 20;
        return e;
    }

    [Check]
    public static void ThePlanRemovesWhatIsNewAndPutsBackWhatDiffers()
    {
        _at = 0;
        var m = Manifest(
            Entry("Desktop", VaultKind.Directory),
            Entry(@"Desktop\pripravene.txt"),
            Entry("Documents", VaultKind.Directory),
            Entry(@"Documents\a.txt", size: 5),
            Entry("Application Data", VaultKind.Link),
            Entry("NTUSER.DAT", size: 262144, time: 500));
        List<ProfileItem> now =
        [
            new("Desktop", VaultKind.Directory, 0, 999, 0x10),
            new(@"Desktop\ziak.txt", VaultKind.File, 3, 900, 0x20),
            new("Documents", VaultKind.Directory, 0, 999, 0x10),
            new(@"Documents\a.txt", VaultKind.File, 7, 900, 0x20),
            new("Downloads", VaultKind.Directory, 0, 1, 0x10),
            new(@"Downloads\hra", VaultKind.Directory, 0, 1, 0x10),
            new(@"Downloads\hra\setup.exe", VaultKind.File, 1, 1, 0x20),
            new("Application Data", VaultKind.Directory, 0, 1, 0x10),
            new("NTUSER.DAT", VaultKind.File, 262144, 500, 0x22),
        ];

        var plan = VaultRules.Plan(now, m);
        Expect.Sequence(["Application Data", @"Desktop\ziak.txt", "Downloads"], plan.Remove.Select(r => r.Path).Order(StringComparer.Ordinal), "nové veci preč; obsah zmazaného priečinka sa nevypisuje");
        Expect.Sequence([@"Desktop\pripravene.txt", @"Documents\a.txt", "Application Data", "NTUSER.DAT"], plan.Put.Select(p => p.Path), "chýbajúce a zmenené späť, v poradí trezoru");
        Expect.True(plan.Remove.First().Path.Contains('\\', StringComparison.Ordinal), "najhlbšie sa maže najprv");

        var same = m.Entries.Select(e => e.Item).ToList();
        Expect.True(VaultRules.Plan(same, m).Nothing, "rovnaký profil – nič");
        var touched = same.Select(i => i.Kind == VaultKind.Directory ? i with { WriteTime = 12345, Attributes = 0x10 | 0x20 } : i).ToList();
        Expect.True(VaultRules.Plan(touched, m).Nothing, "čas priečinka a značka archívu nehrajú rolu");
    }

    [Check]
    public static void CachesAreNotKeptAndPathsAreSafe()
    {
        Expect.True(VaultRules.SkipContent(@"AppData\Local\Temp"), "dočasné súbory");
        Expect.True(VaultRules.SkipContent(@"AppData\Local\Microsoft\Edge\User Data\Default\Cache"), "vyrovnávacia pamäť prehliadača");
        Expect.True(VaultRules.SkipContent(@"appdata\local\Google\Chrome\User Data\Default\Code Cache"), "aj inak písané");
        Expect.True(!VaultRules.SkipContent(@"AppData\Roaming\Cache"), "mimo AppData\\Local nie");
        Expect.True(!VaultRules.SkipContent(@"Documents\Cache"), "dokumenty nie");

        Expect.True(VaultRules.SafePath(@"AppData\Local\x.txt"), "obyčajná cesta");
        foreach (var bad in new[] { "", @"\Windows", @"C:\Windows", @"..\Admin", @"a\..\..\b", @"a\\b", "a:stream", "a/b", @"a\.\b" })
        {
            Expect.True(!VaultRules.SafePath(bad), $"„{bad}“ nie je cesta v profile");
        }
    }

    [Check]
    public static void TheDescriptionReadsWritesAndIsChecked()
    {
        _at = 0;
        var m = Manifest(Entry("Desktop", VaultKind.Directory), Entry(@"Desktop\a.txt"), Entry("Application Data", VaultKind.Link));
        var again = VaultRules.FromJson(VaultRules.ToJson(m));
        Expect.Equal(VaultRules.ToJson(m), VaultRules.ToJson(again), "zápis a čítanie");
        Expect.Equal(VaultKind.Link, again.Entries[2].Kind, "druh");
        Expect.Equal(0, VaultRules.Problems(again).Count(), "v poriadku");

        _at = 0;
        Expect.True(VaultRules.Problems(Manifest(Entry(@"..\Admin\x"))).Any(), "cesta von z profilu");
        _at = 0;
        Expect.True(VaultRules.Problems(Manifest(Entry(@"Desktop\a.txt"))).Any(), "súbor pred svojím priečinkom");
        _at = 0;
        Expect.True(VaultRules.Problems(Manifest(Entry("a"), Entry("A"))).Any(), "dvakrát");
        _at = 0;
        var outside = Manifest(Entry("a"));
        outside.ArchiveLength = 5;
        Expect.True(VaultRules.Problems(outside).Any(), "mimo archívu");
        _at = 0;
        var foreign = Manifest(Entry("a"));
        foreign.Sid = "S-1-5-18";
        Expect.True(VaultRules.Problems(foreign).Any(), "len konto PC");
    }

    private static byte[] Stream(uint id, string name, byte[] data)
    {
        var nameBytes = System.Text.Encoding.Unicode.GetBytes(name);
        var b = new byte[20 + nameBytes.Length + data.Length];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(b, id);
        System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(b.AsSpan(8), data.Length);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(16), (uint)nameBytes.Length);
        nameBytes.CopyTo(b, 20);
        data.CopyTo(b, 20 + nameBytes.Length);
        return b;
    }

    [Check]
    public static void TheFileItselfIsReadFromItsRecordInTheTrezor()
    {
        byte[] pins = [9, 8, 7, 6, 5];
        var record = Stream(3, "", [1, 1, 1, 1]).Concat(Stream(4, ":Zone.Identifier:$DATA", [2, 2])).Concat(Stream(1, "", pins)).ToArray();
        Expect.Sequence(pins, BackupStreams.FileData(record), "dáta súboru za ochranou a pomenovaným prúdom");
        Expect.Equal(0, BackupStreams.FileData(Stream(3, "", [1])).Length, "súbor bez dát");
        Expect.Throws<InvalidDataException>(() => BackupStreams.FileData(record.AsSpan(0, record.Length - 2)), "skrátený záznam");
        Expect.Throws<InvalidDataException>(() => BackupStreams.FileData(new byte[10]), "kúsok hlavičky");

        var folder = StartLayoutRules.Folder;
        Expect.True(StartLayoutRules.IsLayoutFile(folder + @"\start2.bin") && StartLayoutRules.IsLayoutFile(folder + @"\start.bin") && StartLayoutRules.IsLayoutFile(folder + @"\START3.BIN"), "súbory Štartu");
        Expect.True(!StartLayoutRules.IsLayoutFile(folder + @"\start2.bin.bak") && !StartLayoutRules.IsLayoutFile(folder + @"\startx.bin") && !StartLayoutRules.IsLayoutFile(@"Desktop\start2.bin"), "iné súbory nie");

        var recipe = RecipeText.FromJson("""{ "schema": 1, "name": "START", "elements": [ { "type": "start-layout", "id": "s", "from": "ziak" } ] }""");
        Expect.Equal("ziak", ((StartLayoutElement)recipe.Elements[0]).From, "prvok start-layout");
        Expect.Equal(RecipeText.Fingerprint(recipe), RecipeText.Fingerprint(RecipeText.FromJson(RecipeText.ToJson(recipe))), "zápis a čítanie");
        Expect.Equal(0, RecipeCheck.Problems(recipe).Count, string.Join(" ", RecipeCheck.Problems(recipe)));
        var wrong = RecipeText.FromJson("""{ "schema": 1, "name": "START", "elements": [ { "type": "start-layout", "id": "s", "from": "Administrator" } ] }""");
        Expect.True(RecipeCheck.Problems(wrong).Any(p => p.Contains("vstavané", StringComparison.Ordinal)), "vstavané konto nemá trezor");
    }
}
