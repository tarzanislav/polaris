using Polaris.Json;
using Polaris.Recipes;

namespace Polaris.Tests;

/// <summary>The own JSON: what it reads, what it refuses and where it says the mistake is, and the canonical form that
/// fingerprints and signatures rest on.</summary>
public static class JsonChecks
{
    private static JsonError Fails(string text, JsonSyntax? syntax = null)
    {
        try
        {
            JsonReader.Parse(text, syntax);
        }
        catch (JsonError e)
        {
            return e;
        }

        throw new CheckFailed("text sa mal odmietnuť: " + text);
    }

    [Check]
    public static void ReadsValuesAndKeepsPositions()
    {
        var root = (JsonObject)JsonReader.Parse("{\n  \"a\": [1, -2.5e3, true, false, null],\n  \"b\": \"Š\\u0161\\n\\\"\\\\\\/\\uD83D\\uDE00😀\"\n}");
        var a = (JsonArray)root["a"]!;
        Expect.Equal(5, a.Count, "päť hodnôt");
        Expect.Equal("-2.5e3", ((JsonNumber)a.Items[1]).Text, "číslo presne ako je");
        Expect.Equal(-2500d, ((JsonNumber)a.Items[1]).ToDouble(), "hodnota čísla");
        Expect.Equal(JsonKind.Null, a.Items[4].Kind, "null");
        Expect.Equal("Šš\n\"\\/😀😀", ((JsonString)root["b"]!).Value, "skratky a dvojice UTF-16");
        Expect.Equal(3, root.Member("b")!.Line, "riadok mena");
        Expect.Equal(3, root.Member("b")!.Column, "znak mena");
        Expect.Equal(2, a.Line, "riadok zoznamu");
        Expect.Equal(8, a.Column, "znak zoznamu");
        Expect.Equal(JsonKind.String, JsonReader.Parse("\uFEFF\"x\"").Kind, "značka BOM na začiatku");
    }

    [Check]
    public static void RefusesWithThePlace()
    {
        var e = Fails("{\n  \"a\": 1,\n  \"a\": 2\n}");
        Expect.True(e.Line == 3 && e.Column == 3 && e.Problem.Contains("dvakrát", StringComparison.Ordinal), "meno dvakrát: " + e.Message);
        e = Fails("{ \"a\": 1 \"b\": 2 }");
        Expect.True(e.Column == 10 && e.Problem.Contains("čiarka", StringComparison.Ordinal), "chýba čiarka: " + e.Message);
        Expect.True(Fails("{ \"a\": 1, }").Problem.Contains("nesmie byť čiarka", StringComparison.Ordinal), "čiarka na konci v prísnom");
        Expect.True(Fails("[1,]").Problem.Contains("nesmie byť čiarka", StringComparison.Ordinal), "čiarka na konci zoznamu");
        Expect.True(Fails("// x\n1").Problem.Contains("komentáre", StringComparison.Ordinal), "komentár v prísnom");
        Expect.True(Fails("{ \"a\": 01 }").Problem.Contains("nulou", StringComparison.Ordinal), "nula na začiatku");
        Expect.True(Fails("{ \"a\": 1x }").Problem.Contains("písmená", StringComparison.Ordinal), "písmená za číslom");
        Expect.True(Fails("{ \"a\": 'x' }").Problem.Contains("apostrofoch", StringComparison.Ordinal), "apostrofy");
        Expect.True(Fails("\"C:\\Users\"").Problem.Contains("dvojitým", StringComparison.Ordinal), "cesta Windows s jedným \\");
        Expect.True(Fails("\"a\nb\"").Problem.Contains("nový riadok", StringComparison.Ordinal), "nový riadok v texte");
        Expect.True(Fails("\"\\uD83D\"").Problem.Contains("druhá", StringComparison.Ordinal), "osamotená prvá polovica");
        Expect.True(Fails("\"\\uDE00\"").Problem.Contains("osamotená", StringComparison.Ordinal), "osamotená druhá polovica");
        Expect.True(Fails("\"\uD83D\"").Problem.Contains("osamotená", StringComparison.Ordinal), "surový lonely surrogate");
        Expect.True(Fails("tru").Problem.Contains("true", StringComparison.Ordinal), "neúplné slovo");
        Expect.True(Fails("nullx").Problem.Contains("null", StringComparison.Ordinal), "slovo s príveskom");
        Expect.True(Fails("1 2").Problem.Contains("ďalší text", StringComparison.Ordinal), "dve hodnoty");
        Expect.True(Fails("").Problem.Contains("chýba hodnota", StringComparison.Ordinal), "prázdny text");
        Expect.True(Fails("{\"a\": [1, 2").Problem.Contains("nie je uzavretý", StringComparison.Ordinal), "neuzavretý zoznam");
        Expect.True(Fails(new string('[', 40) + new string(']', 40)).Problem.Contains("vnorených", StringComparison.Ordinal), "príliš hlboko");
        Expect.True(Fails("/* a", JsonSyntax.Human).Problem.Contains("komentár", StringComparison.Ordinal), "neuzavretý komentár");
        Expect.True(Fails(new string(' ', 20) + "1", JsonSyntax.Strict with { MaxLength = 10 }).Problem.Contains("dlhší", StringComparison.Ordinal), "príliš dlhý text");
        Expect.True(Fails("[\"x\" ]x").Line == 1, "pozícia na prvom riadku");

        try
        {
            JsonReader.Parse([0x22, 0xC3, 0x28, 0x22]);
            throw new CheckFailed("zlé UTF-8 sa malo odmietnuť");
        }
        catch (JsonError e2)
        {
            Expect.True(e2.Problem.Contains("UTF-8", StringComparison.Ordinal), "zlé bajty UTF-8 sa nenahradia potichu");
        }
    }

    [Check]
    public static void PeopleMayCommentAndLeaveCommas()
    {
        var o = (JsonObject)JsonReader.Parse("{\n // komentár\n \"a\": [1, 2,], /* aj\n takýto */ \"b\": 3,\n}", JsonSyntax.Human);
        Expect.Equal(2, o.Count, "dve polia");
        Expect.Equal(4, o.Member("b")!.Line, "riadok za viacriadkovým komentárom");
    }

    [Check]
    public static void TheCanonicalFormIsFixed()
    {
        var node = new JsonObject()
            .Add("b", "Škola – \"úvod\"\\\n\t\u0001\u001f😀 </script>")
            .Add("a", new JsonArray().Add(1).Add(-0L).Add(true).Add(new JsonNull()))
            .Add("c", new JsonObject())
            .Add("d", new JsonNumber(0.1 + 0.2));
        var canonical = JsonText.Canonical(node);
        Expect.Equal("{\"b\":\"Škola – \\\"úvod\\\"\\\\\\n\\t\\u0001\\u001f😀 </script>\",\"a\":[1,0,true,null],\"c\":{},\"d\":0.30000000000000004}", canonical, "kanonický tvar");
        Expect.Equal(canonical, JsonText.Canonical(JsonReader.Parse(canonical)), "prečítaný a zapísaný znova je rovnaký");
        Expect.Equal(canonical, JsonText.Canonical(JsonReader.Parse(JsonText.Readable(node))), "aj z čitateľného tvaru");

        try
        {
            JsonText.Canonical(new JsonString("a\uD800b"));
            throw new CheckFailed("pokazený text sa nemá dať zapísať");
        }
        catch (ArgumentException)
        {
        }
    }

    /// <summary>The fingerprint of a fixed recipe, pinned. If this check ever fails, the canonical form changed – and every
    /// signature in schools would stop being valid. Then the change is wrong, not this number.</summary>
    [Check]
    public static void TheFingerprintOfARecipeIsPinned()
    {
        var recipe = new Recipe
        {
            Name = "SKUSKA",
            Version = 7,
            Note = "Škola – záložky",
            Elements =
            [
                new ProgramElement { Id = "vlc", Winget = "VideoLAN.VLC", DisplayName = "VLC media player" },
                new RegistryValueElement { Id = "r", Path = @"HKLM\SOFTWARE\Policies\X", Name = "A", Kind = RegistryKind.DWord, Data = "1", Present = false },
                new SandboxElement { Id = "s", Name = "Pokusy", MemoryMb = 6144, Folders = [new MappedFolder { HostFolder = @"C:\Ucivo" }] },
            ],
        };
        Expect.Equal(
            """{"schema":1,"name":"SKUSKA","version":7,"note":"Škola – záložky","elements":[{"type":"program","id":"vlc","winget":"VideoLAN.VLC","displayName":"VLC media player"},{"type":"registry","id":"r","path":"HKLM\\SOFTWARE\\Policies\\X","name":"A","kind":"DWord","data":"1","present":false},{"type":"sandbox","id":"s","name":"Pokusy","memoryMb":6144,"folders":[{"hostFolder":"C:\\Ucivo"}]}]}""",
            RecipeText.Canonical(recipe),
            "kanonický tvar receptu");
        Expect.Equal(JsonText.Fingerprint(JsonReader.Parse(RecipeText.Canonical(recipe))), RecipeText.Fingerprint(recipe), "odtlačok = SHA-256 kanonického textu");
        // the same number as `printf '%s' '<the text above>' | sha256sum` gives – checked outside .NET
        Expect.Equal("09f9f4671e4f6fb4dd63043c6746fb6768f4239df94c3e8df5d6009b7910b884", RecipeText.Fingerprint(recipe), "odtlačok pripnutý");
    }

    [Check]
    public static void TyposInARecipeAreFoundWithAHint()
    {
        static InvalidDataException Bad(string json)
        {
            try
            {
                RecipeText.FromJson(json);
            }
            catch (InvalidDataException e)
            {
                return e;
            }

            throw new CheckFailed("recept sa mal odmietnuť");
        }

        var e = Bad("{ \"schema\": 1, \"elements\": [\n  { \"type\": \"sandbox\", \"id\": \"s\", \"name\": \"A\",\n    \"netwroking\": true } ] }");
        Expect.True(e.Message.Contains("riadok 3", StringComparison.Ordinal) && e.Message.Contains("„networking“", StringComparison.Ordinal), "preklep s návrhom: " + e.Message);
        e = Bad("{ \"schema\": 1, \"elemnts\": [] }");
        Expect.True(e.Message.Contains("„elements“", StringComparison.Ordinal), "preklep v recepte: " + e.Message);
        e = Bad("{ \"schema\": 1, \"elements\": [ { \"type\": \"feature\", \"id\": \"f\", \"name\": \"X\", \"winget\": \"A.B\" } ] }");
        Expect.True(e.Message.Contains("„winget“", StringComparison.Ordinal) && e.Message.Contains("prvok f", StringComparison.Ordinal), "pole iného typu: " + e.Message);
        e = Bad("{ \"schema\": 1, \"elements\": [ { \"type\": \"sandbox\", \"name\": \"A\", \"memoryMb\": \"4096\" } ] }");
        Expect.True(e.Message.Contains("celé číslo", StringComparison.Ordinal), "číslo v úvodzovkách: " + e.Message);
        e = Bad("{ \"schema\": 1, \"elements\": [ { \"type\": \"service\", \"name\": \"X\", \"start\": \"Auto\" } ] }");
        Expect.True(e.Message.Contains("Automatic, AutomaticDelayed, Manual, Disabled", StringComparison.Ordinal), "zoznam možností: " + e.Message);
        e = Bad("{ \"schema\": 1, \"elements\": [ { \"type\": \"pieskovisko\" } ] }");
        Expect.True(e.Message.Contains("neznámy typ", StringComparison.Ordinal), "neznámy typ: " + e.Message);
        e = Bad("{ \"schema\": 9, \"elements\": [], \"novinka\": 1 }");
        Expect.True(e.Message.Contains("novšieho", StringComparison.Ordinal), "novšia schéma má prednosť pred neznámym poľom: " + e.Message);
        Expect.Equal(2, JsonFields.Distance("netwroking", "networking") + 1, "výmena susedov = 1");
    }

    [Check]
    public static void DefaultsAreLeftOutAndComeBack()
    {
        var s = new SandboxElement { Id = "s", Name = "A" };
        var text = JsonText.Canonical(RecipeFormat.WriteElement(s));
        Expect.Equal("{\"type\":\"sandbox\",\"id\":\"s\",\"name\":\"A\"}", text, "predvolené hodnoty sa nepíšu");
        var back = (SandboxElement)RecipeFormat.ReadElement(JsonReader.Parse(text), 0);
        Expect.True(back.ProtectedClient && back.MemoryMb == 4096 && !back.Networking && back.Present, "a vrátia sa pri čítaní");

        foreach (var file in Directory.GetFiles(Path.Combine(Repo.Root, "recepty"), "*.json"))
        {
            var recipe = RecipeText.Load(file);
            var again = RecipeText.FromJson(RecipeText.ToJson(recipe));
            Expect.Equal(RecipeText.Fingerprint(recipe), RecipeText.Fingerprint(again), "zápis a čítanie nemení odtlačok: " + Path.GetFileName(file));
            Expect.Equal(RecipeText.ToJson(recipe), RecipeText.ToJson(again), "čitateľný tvar je stály: " + Path.GetFileName(file));
        }
    }
}
