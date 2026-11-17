using Polaris.Recipes;

namespace Polaris.Tests;

public static class SessionChecks
{
    private static List<string> Problems(Element e) => RecipeCheck.Problems(new Recipe { Name = "S", Elements = [e] });

    [Check]
    public static void KeyboardsBecomeValuesOfTheTemplate()
    {
        var k = new KeyboardsElement { Id = "k", Languages = ["sk-SK", "en-US", "de-DE"] };
        var values = KeyboardLayouts.Expand(k).Cast<UserRegistryElement>().ToList();
        UserRegistryElement V(string id) => values.Single(v => v.Id == id);
        Expect.Equal("0000041b", V("k.preload1").Data, "prvá klávesnica slovenská");
        Expect.Equal(KeyboardLayouts.PreloadKey, V("k.preload2").Path, "kľúč Preload");
        Expect.True(!V("k.nahrada1").Present, "bežné rozloženie bez náhrady");
        Expect.Equal(@"Control Panel\International\User Profile\de-DE", V("k.jazyk3").Path, "jazyk nemčina");
        Expect.Equal("0407:00000407", V("k.jazyk3").Name, "vstup nemčiny");
        Expect.Equal("sk-SK\nen-US\nde-DE", V("k.jazyky").Data, "zoznam jazykov");
        Expect.True(Enumerable.Range(4, 6).All(i => !V($"k.preload{i}").Present), "staršie miesta 4 až 9 sa odstránia");

        k.Layouts = new Dictionary<string, string> { ["sk-SK"] = "0001041B" };
        values = KeyboardLayouts.Expand(k).Cast<UserRegistryElement>().ToList();
        Expect.True(V("k.nahrada1").Present && V("k.nahrada1").Name == "0000041b" && V("k.nahrada1").Data == "0001041b", "slovenská QWERTY ako náhrada");
        Expect.Equal("041B:0001041B", V("k.jazyk1").Name, "vstup s náhradou");
    }

    [Check]
    public static void ChecksOfTheSessionParts()
    {
        Expect.Sequence(Array.Empty<string>(), Problems(new KeyboardsElement { Id = "k", Languages = ["sk-SK", "cs-CZ", "en-US", "ru-RU", "fr-FR", "de-DE"] }), "šesť jazykov");
        Expect.True(Problems(new KeyboardsElement { Id = "k", Languages = ["uk-UA"] }).Any(p => p.Contains("nepoznám", StringComparison.Ordinal)), "neznámy jazyk");
        Expect.Sequence(Array.Empty<string>(), Problems(new KeyboardsElement { Id = "k", Languages = ["uk-UA"], Layouts = new() { ["uk-UA"] = "0422:00020422" } }), "neznámy jazyk s rozložením");
        Expect.True(Problems(new KeyboardsElement { Id = "k", Languages = ["en-US", "en-US"] }).Any(p => p.Contains("dvakrát", StringComparison.Ordinal)), "dvakrát rovnaký");
        Expect.True(Problems(new KeyboardsElement { Id = "k", Languages = ["en-US"], Layouts = new() { ["sk-SK"] = "0001041B" } }).Any(p => p.Contains("nie je v languages", StringComparison.Ordinal)), "rozloženie bez jazyka");
        Expect.True(Problems(new KeyboardsElement { Id = "k", Languages = [] }).Any(p => p.Contains("1 až", StringComparison.Ordinal)), "bez klávesníc");

        Expect.True(Problems(new UserRegistryElement { Id = "u", Path = @"HKCU\Control Panel\Desktop", Name = "X", Data = "1" }).Any(p => p.Contains("bez HKCU", StringComparison.Ordinal)), "bez HKCU");
        Expect.True(Problems(new UserRegistryElement { Id = "u", Path = "Control Panel", Name = "X", Kind = RegistryKind.DWord, Data = "x" }).Any(p => p.Contains("DWord", StringComparison.Ordinal)), "zlé číslo");

        Expect.Sequence(Array.Empty<string>(), Problems(new SessionElement { Id = "s", Account = "ziak" }), "konto žiaka");
        Expect.True(Problems(new SessionElement { Id = "s", Account = "Administrator" }).Any(p => p.Contains("nikdy nemaže", StringComparison.Ordinal)), "správca sa nikdy nečistí");
        Expect.True(Problems(new SessionElement { Id = "s", Account = "konto/ziaka" }).Any(p => p.Contains("meno konta", StringComparison.Ordinal)), "zlé meno konta");

        var sample = RecipeText.Load(Path.Combine(Repo.Root, "recepty", "ukazka-relacia.json"));
        Expect.Sequence(Array.Empty<string>(), RecipeCheck.Problems(sample), "ukážka relácie");
        Expect.Equal(SessionCleaning.Restore, sample.Elements.OfType<SessionElement>().Single().Clean, "po odhlásení z trezoru");
        Expect.True(sample.Elements.OfType<AccountElement>().Single() is { NoPassword: true, AutoLogon: true }, "konto žiaka bez hesla, prihlási sa samo");
    }

    [Check]
    public static void WhenTheProfileIsCleaned()
    {
        var monday = new DateTime(2026, 11, 16, 7, 30, 0);
        Expect.True(SessionPolicy.ShouldClean(SessionCleaning.AtSignOut, false, true, monday, monday), "po odhlásení hneď");
        Expect.True(!SessionPolicy.ShouldClean(SessionCleaning.AtSignOut, true, true, monday, monday), "nikdy počas prihlásenia");
        Expect.True(!SessionPolicy.ShouldClean(SessionCleaning.AtSignOut, false, false, null, monday), "bez profilu nič");
        Expect.True(!SessionPolicy.ShouldClean(SessionCleaning.EndOfDay, false, true, monday.AddHours(-1), monday), "dnes použitý ostáva");
        Expect.True(SessionPolicy.ShouldClean(SessionCleaning.EndOfDay, false, true, monday.AddDays(-1), monday), "včerajší sa vyčistí");
        Expect.True(SessionPolicy.ShouldClean(SessionCleaning.EndOfDay, false, true, null, monday), "neznámy čas = vyčistiť");
        Expect.True(SessionPolicy.ShouldClean(SessionCleaning.EndOfWeek, false, true, monday.AddDays(-3), monday), "piatkový v pondelok");
        Expect.True(!SessionPolicy.ShouldClean(SessionCleaning.EndOfWeek, false, true, monday.AddHours(1), monday.AddDays(4)), "v piatok ešte ostáva");
        Expect.True(!SessionPolicy.ShouldClean(SessionCleaning.Never, false, true, null, monday), "nečistiť");
    }
}
