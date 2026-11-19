using System.Text;
using Polaris.Recipes;

namespace Polaris.Tests;

/// <summary>The local group policy as files (Registry.pol, gpt.ini) and the elements that use it (pilot 19. 11. 2026:
/// UCPD lets only Windows write the policy of widgets).</summary>
public static class PolicyChecks
{
    [Check]
    public static void RegistryPolIsTheFormatOfWindows()
    {
        var entry = PolicyFile.Entry(@"Software\Policies\Microsoft\Dsh", "AllowNewsAndInterests", RegistryKind.DWord, "0");
        var bytes = PolicyFile.Write([entry]);
        Expect.Equal("50526567", Convert.ToHexString(bytes, 0, 4), "PReg");
        Expect.Equal(1u, BitConverter.ToUInt32(bytes, 4), "verzia 1");
        var expected = new List<byte>(bytes[..8]);
        void Text(string t) => expected.AddRange(Encoding.Unicode.GetBytes(t));
        Text("[" + @"Software\Policies\Microsoft\Dsh" + "\0;AllowNewsAndInterests\0;");
        expected.AddRange(BitConverter.GetBytes(4u));
        Text(";");
        expected.AddRange(BitConverter.GetBytes(4u));
        Text(";");
        expected.AddRange(BitConverter.GetBytes(0u));
        Text("]");
        Expect.True(bytes.SequenceEqual(expected), "[kľúč;hodnota;typ;veľkosť;údaje] v UTF-16");

        var many = PolicyFile.Set([entry], PolicyFile.Entry(@"Software\Policies\X", "Text", RegistryKind.String, "ahoj"));
        many = PolicyFile.Set(many, PolicyFile.Entry(@"Software\Policies\X", "Viac", RegistryKind.MultiString, "a\nb"));
        many = PolicyFile.Set(many, PolicyFile.Entry(@"SOFTWARE\Policies\Microsoft\Dsh", "allownewsandinterests", RegistryKind.DWord, "1"));
        var back = PolicyFile.Read(PolicyFile.Write(many));
        Expect.Equal(3, back.Count, "tá istá hodnota sa nahradí, nie pridá");
        Expect.Equal(1u, BitConverter.ToUInt32(PolicyFile.Find(back, @"Software\Policies\Microsoft\Dsh", "AllowNewsAndInterests")!.Data), "nová hodnota");
        Expect.Equal("ahoj\0", Encoding.Unicode.GetString(PolicyFile.Find(back, @"Software\Policies\X", "Text")!.Data), "text s nulou na konci");
        Expect.Equal("a\0b\0\0", Encoding.Unicode.GetString(PolicyFile.Find(back, @"Software\Policies\X", "Viac")!.Data), "viac textov");
        Expect.Equal(2, PolicyFile.Without(back, @"Software\Policies\X", "TEXT").Count, "odstránenie");
        Expect.Equal(0, PolicyFile.Read([]).Count, "prázdny súbor");
        Expect.Throws<InvalidDataException>(() => PolicyFile.Read(bytes[..^3]), "skrátený súbor");
        Expect.Throws<InvalidDataException>(() => PolicyFile.Read(Encoding.ASCII.GetBytes("REGEDIT4")), "iný súbor");
    }

    [Check]
    public static void GptIniGrowsAndKnowsTheRegistry()
    {
        var first = PolicyIni.Bump("", user: false);
        Expect.True(first.StartsWith("[General]\r\n", StringComparison.Ordinal) && first.Contains("Version=1\r\n", StringComparison.Ordinal), "nový gpt.ini: " + first);
        Expect.True(first.Contains("gPCMachineExtensionNames=" + PolicyIni.MachineRegistry, StringComparison.Ordinal), "rozšírenie registra pre PC");
        var user = PolicyIni.Bump(first, user: true);
        Expect.True(user.Contains("Version=65537\r\n", StringComparison.Ordinal), "používateľ v hornej polovici: " + user);
        Expect.True(user.Contains("gPCUserExtensionNames=" + PolicyIni.UserRegistry, StringComparison.Ordinal), "rozšírenie registra pre používateľov");
        Expect.Equal(1, PolicyIni.Bump(user, user: false).Split("gPCMachineExtensionNames").Length - 1, "rozšírenie raz");

        var other = "[General]\r\ngPCMachineExtensionNames=[{827D319E-6EAC-11D2-A4EA-00C04F79F83A}{803E14A0-B4FB-11D0-A0D0-00A0C90F574B}]\r\nVersion=7\r\n";
        var merged = PolicyIni.Bump(other, user: false);
        Expect.True(merged.Contains("Version=8", StringComparison.Ordinal), "verzia rastie");
        Expect.True(merged.Contains("=" + PolicyIni.MachineRegistry + "[{827D319E", StringComparison.Ordinal), "zoradené podľa rozšírenia, iné ostali: " + merged);
    }

    [Check]
    public static void PoliciesOfUsersGoAlsoToTheLocalPolicy()
    {
        var u = new UserRegistryElement { Id = "s", Path = @"Software\Policies\Microsoft\Windows\CloudContent", Name = "DisableWindowsSpotlightFeatures", Kind = RegistryKind.DWord, Data = "1" };
        var parts = Expansion.Expand(u).ToList();
        Expect.True(parts.Count == 2 && parts[1] is UserPolicyElement { Name: "DisableWindowsSpotlightFeatures", Data: "1" }, "predloha aj miestna politika používateľov");
        var plain = new UserRegistryElement { Id = "p", Path = @"Control Panel\Desktop", Name = "X", Data = "1" };
        Expect.Equal(1, Expansion.Expand(plain).Count(), "obyčajné nastavenie len do predlohy");
    }

    [Check]
    public static void TheSystemDiskHasALabel()
    {
        var c = new ComputerElement { Id = "c", SystemDriveLabel = "WIN11" };
        Expect.Equal(0, RecipeCheck.ElementProblems(c).Count(), "menovka stačí");
        Expect.True(RecipeCheck.ElementProblems(new ComputerElement { Id = "c", SystemDriveLabel = new string('x', 33) }).Any(), "najviac 32 znakov");
        Expect.True(RecipeCheck.ElementProblems(new ComputerElement { Id = "c", SystemDriveLabel = "a:b" }).Any(), "bez dvojbodky");
        var sample = RecipeText.Load(Path.Combine(Repo.Root, "recepty", "ukazka-zaklad.json"));
        Expect.Equal("WIN11", sample.Elements.OfType<ComputerElement>().Single().SystemDriveLabel, "ukážka: WIN11");
        Expect.True(Expansion.Expand(sample.Elements.OfType<UserRegistryElement>().Single(e => e.Name == "DisableWindowsSpotlightFeatures")).OfType<UserPolicyElement>().Any(), "ukážka: bez Spotlightu pre všetky kontá");
    }
}
