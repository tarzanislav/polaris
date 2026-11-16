using Polaris.Recipes;

namespace Polaris.Tests;

/// <summary>The School supply: descriptions of packages, their fingerprints, the folder and the centre's commands.</summary>
public static class PackageChecks
{
    private static readonly DateTimeOffset T = new(2026, 11, 16, 22, 0, 0, TimeSpan.Zero);

    private static Package Exe(string id = "skuska-1.0") => new()
    {
        Id = id,
        Title = "Skúška",
        Version = "1.0",
        Kind = PackageKind.Exe,
        File = "skuska-setup.exe",
        Sha256 = new string('a', 64),
        Size = 10,
        Arguments = "/S",
        DisplayName = "Skúška",
        AddedUtc = T,
    };

    private static (int Code, string Text) Centrum(params string[] args)
    {
        var w = new StringWriter();
        return (Polaris.Centrum.Commands.Run(args, w, T.LocalDateTime, T), w.ToString());
    }

    [Check]
    public static void DescriptionsAreStrictAndPinned()
    {
        var p = Exe();
        var json = PackageFormat.ToJson(p);
        var back = PackageFormat.FromJson(json);
        Expect.Equal(PackageFormat.Fingerprint(p), PackageFormat.Fingerprint(back), "zápis a čítanie nemenia odtlačok");
        Expect.Sequence(Array.Empty<string>(), PackageFormat.Problems(p), "dobrý popis");

        back.Arguments = "/S /D=C:\\Iny";
        Expect.True(PackageFormat.Fingerprint(back) != PackageFormat.Fingerprint(p), "iné argumenty = iný odtlačok");
        try
        {
            PackageFormat.FromJson(json.Replace("\"arguments\"", "\"argumnets\"", StringComparison.Ordinal));
            throw new CheckFailed("preklep sa mal odmietnuť");
        }
        catch (InvalidDataException e)
        {
            Expect.True(e.Message.Contains("„arguments“", StringComparison.Ordinal), "návrh správneho mena: " + e.Message);
        }

        var silent = Exe();
        silent.Arguments = "";
        Expect.True(PackageFormat.Problems(silent).Any(x => x.Contains("tichej", StringComparison.Ordinal)), "exe bez tichých argumentov");
        var nameless = Exe();
        nameless.DisplayName = "";
        Expect.True(PackageFormat.Problems(nameless).Any(x => x.Contains("displayName", StringComparison.Ordinal)), "exe bez mena");
        var msi = Exe();
        msi.Kind = PackageKind.Msi;
        msi.File = "x.msi";
        Expect.True(PackageFormat.Problems(msi).Any(x => x.Contains("productCode", StringComparison.Ordinal)), "msi bez ProductCode");
        var path = Exe();
        path.File = "..\\x.exe";
        Expect.True(PackageFormat.Problems(path).Any(x => x.Contains("bez cesty", StringComparison.Ordinal)), "cesta v mene súboru");

        Expect.Equal("audacity-3.7.4", PackageFormat.SuggestId("Audacity", "3.7.4"), "id z názvu a verzie");
        Expect.Equal("skolsky-program-2.0", PackageFormat.SuggestId("Školský Program", "2.0"), "bez diakritiky");
        Expect.Equal("vlc-media-player-3.0.21", PackageFormat.SuggestId("VLC media player 3.0.21", "3.0.21"), "verzia už v názve");
    }

    [Check]
    public static void RecipesPinPackages()
    {
        var fingerprint = PackageFormat.Fingerprint(Exe());
        var program = new ProgramElement { Id = "s", Package = new PackageRef { Id = "skuska-1.0", Fingerprint = fingerprint } };
        var recipe = new Recipe { Name = "B", Elements = [program] };
        Expect.Sequence(Array.Empty<string>(), RecipeCheck.Problems(recipe), "program z balíka bez displayName");
        var again = RecipeText.FromJson(RecipeText.ToJson(recipe));
        Expect.Equal(fingerprint, ((ProgramElement)again.Elements[0]).Package!.Fingerprint, "odtlačok balíka v recepte");
        Expect.Equal("package:skuska-1.0", program.Key, "kľúč prvku");

        program.Package.Fingerprint = "zly";
        Expect.True(RecipeCheck.Problems(recipe).Any(x => x.Contains("package.fingerprint", StringComparison.Ordinal)), "zlý odtlačok");
        program.Package.Fingerprint = fingerprint;
        program.Winget = "Vydavatel.Program";
        Expect.True(RecipeCheck.Problems(recipe).Any(x => x.Contains("viac zdrojov", StringComparison.Ordinal)), "dva zdroje");

        var store = new StoreElement { Id = "o", Accounts = ["ziak"], Items = [new StoreItem { Id = "s", Title = "Skúška", Program = new ProgramElement { Id = "s", Package = new PackageRef { Id = "skuska-1.0", Fingerprint = fingerprint } } }] };
        Expect.Sequence(Array.Empty<string>(), RecipeCheck.Problems(new Recipe { Name = "O", Elements = [store] }), "balík v Školskom obchode");
    }

    [Check]
    public static void TheSupplyFolderAndTheCentre()
    {
        var dir = Directory.CreateTempSubdirectory("polaris-zasoba-").FullName;
        try
        {
            var supply = Path.Combine(dir, "zasoba");
            var installer = Path.Combine(dir, "skuska-setup.exe");
            File.WriteAllBytes(installer, [0x4D, 0x5A, 1, 2, 3, 4, 5, 6, 7, 8]);

            var (code, text) = Centrum("zasoba", "pridat", supply, installer, "--nazov", "Skúška", "--verzia", "1.0", "--argumenty", "/S", "--meno", "Skúška");
            Expect.Equal(0, code, text);
            Expect.True(text.Contains("\"package\": {\"id\":\"skuska-1.0\",\"fingerprint\":\"", StringComparison.Ordinal), "úryvok do receptu: " + text);

            var store = new PackageStore(supply);
            var p = store.Load("skuska-1.0");
            Expect.True(p.Size == 10 && p.Sha256 == RecipeText.FileFingerprint(installer) && p.AddedUtc == T, "popis po pridaní");
            Expect.Equal(p.Id, store.Load(p.Id, PackageFormat.Fingerprint(p)).Id, "načítanie s odtlačkom");
            try
            {
                store.Load(p.Id, new string('0', 64));
                throw new CheckFailed("iný odtlačok sa mal odmietnuť");
            }
            catch (InvalidDataException e)
            {
                Expect.True(e.Message.Contains("zmenil", StringComparison.Ordinal), e.Message);
            }

            Expect.Equal(2, Centrum("zasoba", "pridat", supply, installer, "--nazov", "Skúška", "--verzia", "1.0", "--argumenty", "/S", "--meno", "Skúška").Code, "rovnaké id druhýkrát");
            Expect.Equal(2, Centrum("zasoba", "pridat", supply, installer, "--id", "bez-mena", "--argumenty", "/S").Code, "exe bez --meno");
            Expect.True(Centrum("zasoba", "zoznam", supply).Text.Contains("skuska-1.0", StringComparison.Ordinal), "zoznam");
            Expect.Equal(0, Centrum("zasoba", "overit", supply).Code, "neporušená zásoba");

            File.AppendAllText(store.InstallerOf(p), "x");
            var (bad, report) = Centrum("zasoba", "overit", supply);
            Expect.True(bad == 1 && report.Contains("iný odtlačok", StringComparison.Ordinal), "zmenený inštalátor: " + report);
            Expect.True(!Directory.EnumerateDirectories(Path.Combine(supply, "baliky")).Any(d => d.Contains(".novy-", StringComparison.Ordinal)), "žiadne polovičné balíky");

            if (!OperatingSystem.IsWindows())
            {
                var msi = Path.Combine(dir, "x.msi");
                File.WriteAllText(msi, "x");
                var refused = Centrum("zasoba", "pridat", supply, msi);
                Expect.True(refused.Code == 2 && refused.Text.Contains("vo Windows", StringComparison.Ordinal), "MSI mimo Windows: " + refused.Text);
            }
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
