using Polaris.Recipes;

namespace Polaris.Tests;

/// <summary>Software of the lab (18. 11. 2026): only stable versions, folders with rights, the sample sets.</summary>
public static class SoftwareChecks
{
    [Check]
    public static void OnlyStableVersions()
    {
        foreach (var id in new[] { "Mozilla.Firefox.Nightly", "M2Team.NanaZip.Preview", "Microsoft.VisualStudioCode.Insiders", "Google.Chrome.Canary", "Mozilla.Firefox.DeveloperEdition", "Brave.Brave.Beta" })
        {
            Expect.True(SoftwareRules.TestChannelOf(id) is not null, id + " je testovací kanál");
        }

        foreach (var id in new[] { "Mozilla.Firefox.ESR.sk", "Google.Chrome", "Microsoft.DevHome", "Brave.Brave", "KDE.Kdenlive", "EclipseAdoptium.Temurin.21.JRE" })
        {
            Expect.True(SoftwareRules.TestChannelOf(id) is null, id + " je stabilný");
        }

        foreach (var v in new[] { "3.0-beta2", "1.2rc1", "5.0.0-preview.3", "2.1a", "26.0.0-alpha", "1.0.0-dev" })
        {
            Expect.True(SoftwareRules.TestVersion(v), v + " je testovacia verzia");
        }

        foreach (var v in new[] { "4.76", "26.8.3.1", "157.1.99.56", "21.0.13.9", "3.0.24" })
        {
            Expect.True(!SoftwareRules.TestVersion(v), v + " je stabilná");
        }

        var beta = new ProgramElement { Id = "b", Winget = "Mozilla.Firefox.Beta", DisplayName = "Mozilla Firefox", Match = NameMatch.Prefix };
        Expect.True(RecipeCheck.ElementProblems(beta).Any(p => p.Contains("stabilné", StringComparison.Ordinal)), "recept s betou neprejde");
    }

    [Check]
    public static void FoldersAreCheckedAndReadBack()
    {
        static List<string> Problems(string path, params FolderAccess[] access) => [.. RecipeCheck.ElementProblems(new FolderElement { Id = "f", Path = path, Access = [.. access] })];
        var users = new FolderAccess { Who = "Users", Rights = FolderRights.Modify };
        Expect.Equal(0, Problems(@"C:\Data\Ziaci", users).Count, "dátový priečinok");
        Expect.Equal(0, Problems(@"C:\Users\Public\Documents\Ucivo", new FolderAccess { Who = "ziak", Rights = FolderRights.Read }).Count, "verejné dokumenty, konto");
        foreach (var bad in new[] { @"C:\", @"C:\Windows\Temp", @"C:\Program Files\Firma", @"C:\Program Files (x86)\X", @"C:\Users\ziak\Desktop", @"C:\ProgramData\POLARIS\agent", @"Data\X", @"C:\Data\..\Windows", @"\\server\share" })
        {
            Expect.True(Problems(bad, users).Count > 0, $"„{bad}“ recept nemení");
        }

        Expect.True(Problems(@"C:\Data\X").Count > 0, "bez práv");
        Expect.True(Problems(@"C:\Data\X", new FolderAccess { Who = "Everyone" }).Count > 0, "len Users, AuthenticatedUsers alebo konto");
        Expect.True(Problems(@"C:\Data\X", new FolderAccess { Who = "SYSTEM" }).Count > 0, "nie vstavané kontá");

        var recipe = RecipeText.FromJson("""
            { "schema": 1, "name": "P", "elements": [
              { "type": "folder", "id": "d", "path": "C:\\Data\\Ziaci", "access": [ { "who": "Users", "rights": "Modify" }, { "who": "ziak", "rights": "Read" } ] } ] }
            """);
        var folder = (FolderElement)recipe.Elements[0];
        Expect.True(folder.Access.Count == 2 && folder.Access[1] is { Who: "ziak", Rights: FolderRights.Read }, "prečítané");
        Expect.Equal(RecipeText.Fingerprint(recipe), RecipeText.Fingerprint(RecipeText.FromJson(RecipeText.ToJson(recipe))), "zápis a čítanie");
    }

    [Check]
    public static void TheSampleSetsAreValidAndStable()
    {
        foreach (var name in new[] { "ukazka-softver-zaklad", "ukazka-softver-multimedia", "ukazka-softver-kancelaria", "ukazka-softver-ucitel" })
        {
            var recipe = RecipeText.Load(Path.Combine(Repo.Root, "recepty", name + ".json"));
            Expect.Equal(0, RecipeCheck.Problems(recipe).Count, name + ": " + string.Join(" ", RecipeCheck.Problems(recipe)));
            var programs = recipe.Elements.OfType<ProgramElement>().ToList();
            Expect.True(programs.Count > 0 && programs.All(p => p.Winget is { Length: > 0 } && SoftwareRules.TestChannelOf(p.Winget) is null), name + ": len stabilné balíky WinGet");
        }

        var zaklad = RecipeText.Load(Path.Combine(Repo.Root, "recepty", "ukazka-softver-zaklad.json"));
        var ids = zaklad.Elements.OfType<ProgramElement>().Select(p => p.Winget).ToList();
        foreach (var wanted in new[] { "Mozilla.Firefox.ESR.sk", "Brave.Brave", "alexx2000.DoubleCommander", "EclipseAdoptium.Temurin.21.JRE", "geeksoftwareGmbH.PDF24Creator" })
        {
            Expect.True(ids.Contains(wanted), "rozhodnutie vlastníka: " + wanted);
        }

        var pdf24 = zaklad.Elements.OfType<RegistryValueElement>().Single(r => r.Name == "!NoOnlinePdfTools");
        Expect.True(pdf24.Data == "1" && pdf24.Path == @"HKLM\SOFTWARE\PDF24", "PDF24 bez online nástrojov, používateľ to nezmení");
        Expect.True(!zaklad.Elements.OfType<ProgramElement>().Any(p => p.Winget == "PDFgear.PDFgear"), "PDFgear nie na žiacke PC");
    }
}
