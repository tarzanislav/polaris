using Polaris.Recipes;

namespace Polaris.Tests;

/// <summary>The account element (K6b): a shared account without a password that the PC signs in to by itself.</summary>
public static class AccountChecks
{
    private const string Recipe = """
        {
          "schema": 1,
          "name": "Konto žiaka",
          "elements": [
            { "type": "account", "id": "ziak", "name": "ziak", "noPassword": true, "autoLogon": true },
            { "type": "session", "id": "relacia", "account": "ziak", "clean": "Restore" }
          ]
        }
        """;

    [Check]
    public static void AnAccountWithoutPasswordReadsWritesAndExpands()
    {
        var recipe = RecipeText.FromJson(Recipe);
        var a = (AccountElement)recipe.Elements[0];
        Expect.True(a.NoPassword && a.AutoLogon && a.Name == "ziak", "prečítané");
        Expect.Equal(SessionCleaning.Restore, ((SessionElement)recipe.Elements[1]).Clean, "relácia z trezoru");
        Expect.Equal(RecipeText.Fingerprint(recipe), RecipeText.Fingerprint(RecipeText.FromJson(RecipeText.ToJson(recipe))), "zápis a čítanie");
        Expect.Equal(0, RecipeCheck.Problems(recipe).Count, string.Join(" ", RecipeCheck.Problems(recipe)));

        var parts = Expansion.Expand(a).ToList();
        Expect.True(parts[0] is PasswordPolicyElement && parts[1] == a, "najprv politika hesiel, potom konto (prázdne heslo až keď ho politika dovolí)");
        var lsa = parts.OfType<RegistryValueElement>().Single();
        Expect.True(lsa.Path == AccountRules.LsaKey && lsa.Name == "LimitBlankPasswordUse" && lsa.Data == "1" && lsa.Present, "konto bez hesla len pri PC, nikdy cez sieť");
        Expect.True(parts.OfType<AutoLogonElement>().Single() is { Present: true, Account: "ziak" }, "automatické prihlásenie");

        a.AutoLogon = false;
        Expect.True(Expansion.Expand(a).OfType<AutoLogonElement>().Single().Present == false, "bez poľa sa automatické prihlásenie vypne");
        a.NoPassword = false;
        Expect.True(!Expansion.Expand(a).OfType<RegistryValueElement>().Any() && !Expansion.Expand(a).OfType<PasswordPolicyElement>().Any(), "s heslom sa pravidlo Windows ani politika nemenia");
    }

    [Check]
    public static void AccountsAreChecked()
    {
        static List<string> Problems(AccountElement a) => [.. RecipeCheck.ElementProblems(a)];
        Expect.True(Problems(new AccountElement { Id = "a", Name = "ziak", AutoLogon = true }).Any(p => p.Contains("bez hesla", StringComparison.Ordinal)), "automaticky len bez hesla");
        Expect.True(Problems(new AccountElement { Id = "a", Name = "ziak", Present = false }).Any(), "kontá recept nemaže");
        Expect.True(Problems(new AccountElement { Id = "a", Name = "Administrator" }).Any(), "vstavané konto nie");
        Expect.True(Problems(new AccountElement { Id = "a", Name = "zi/ak" }).Any(), "neplatné meno");
        Expect.True(Problems(new AccountElement { Id = "a", Name = new string('z', 21) }).Any(), "najviac 20 znakov");

        var two = RecipeText.FromJson(Recipe.Replace("""{ "type": "session", "id": "relacia", "account": "ziak", "clean": "Restore" }""",
            """{ "type": "account", "id": "ziak2", "name": "ziak2", "noPassword": true, "autoLogon": true }""", StringComparison.Ordinal));
        Expect.True(RecipeCheck.Problems(two).Any(p => p.Contains("jedného konta", StringComparison.Ordinal)), "automaticky len do jedného konta");
        var both = RecipeText.FromJson(Recipe.Replace("""{ "type": "session", "id": "relacia", "account": "ziak", "clean": "Restore" }""",
            """{ "type": "account", "id": "ziak2", "name": "ziak2", "noPassword": true }""", StringComparison.Ordinal));
        Expect.Equal(0, RecipeCheck.Problems(both).Count, "dve kontá bez hesla – politika hesiel je jedna pre PC: " + string.Join(" ", RecipeCheck.Problems(both)));
    }
}
