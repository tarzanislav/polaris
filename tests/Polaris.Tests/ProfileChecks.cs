using Polaris.Core;

namespace Polaris.Tests;

public static class ProfileChecks
{
    private static readonly DateTimeOffset T = new(2026, 11, 10, 7, 0, 0, TimeSpan.Zero);
    private static string Sha(char c) => new(c, 64);

    private static Profile Office()
    {
        var p = new Profile { Name = "KANCELARSKY" };
        var v1 = p.NewDraft("prvá verzia", T);
        v1.Apps = [new InstalledApp("LibreOffice", "25.8"), new InstalledApp("Firefox", "140")];
        p.Approve(v1.Number, Sha('a'));
        return p;
    }

    [Check]
    public static void DraftIsNeverActive()
    {
        var p = Office();
        var draft = p.NewDraft("aktualizácie", T.AddDays(7));
        Expect.Equal(1, p.Active, "aktívna ostáva v1, kým v2 nie je schválená");
        Expect.Throws<InvalidOperationException>(() => p.Activate(draft.Number), "rozpracovanú nemožno spustiť");
        Expect.Throws<InvalidOperationException>(() => p.NewDraft("druhá", T), "len jedna rozpracovaná naraz");
    }

    [Check]
    public static void ApproveMakesActiveAndRollBackReturns()
    {
        var p = Office();
        var v2 = p.NewDraft("aktualizácie", T.AddDays(7));
        p.Approve(v2.Number, Sha('b'));
        Expect.Equal(2, p.Active, "v2 aktívna");
        Expect.Equal(1, p.RollBack(), "návrat na v1");
        Expect.Throws<InvalidOperationException>(() => p.RollBack(), "staršia ako v1 nie je");
    }

    [Check]
    public static void OldVersionsRetireButTwoStay()
    {
        var p = Office();
        for (var i = 0; i < 4; i++)
        {
            var d = p.NewDraft("mesiac " + i, T.AddDays(30 * (i + 1)));
            p.Approve(d.Number, Sha((char)('b' + i)));
        }

        var approved = p.Versions.Where(v => v.State == VersionState.Approved).Select(v => v.Number).ToList();
        Expect.Sequence([4, 5], approved, "ostávajú dve posledné");
        Expect.Equal(5, p.Active, "aktívna najnovšia");
        Expect.Equal(4, p.RollBack(), "návrat je stále možný");
    }

    [Check]
    public static void DroppedDraftLeavesNoTrace()
    {
        var p = Office();
        p.NewDraft("skúška", T.AddDays(1));
        p.DropDraft();
        Expect.Equal(1, p.Versions.Count, "rozpracovaná zmizla");
        Expect.Equal(2, p.NewDraft("znova", T.AddDays(2)).Number, "ďalšie číslo pokračuje");
    }

    [Check]
    public static void ApproveNeedsFingerprint()
    {
        var p = Office();
        var d = p.NewDraft("x", T);
        Expect.Throws<ArgumentException>(() => p.Approve(d.Number, "kratky"), "bez odtlačku SHA-256 sa neschváli");
    }

    [Check]
    public static void CompareTwoVersions()
    {
        var p = Office();
        var v2 = p.NewDraft("november", T.AddDays(30));
        v2.Apps = [new InstalledApp("LibreOffice", "26.2"), new InstalledApp("GIMP", "3.2")];
        p.Approve(v2.Number, Sha('b'));
        var diff = Profile.Compare(p.Get(1)!, p.Get(2)!);
        Expect.Sequence(["GIMP"], diff.Added.Select(x => x.Name), "pribudlo");
        Expect.Sequence(["Firefox"], diff.Removed.Select(x => x.Name), "ubudlo");
        Expect.Equal(("LibreOffice", "25.8", "26.2"), diff.Changed.Single(), "zmenená verzia");
    }

    [Check]
    public static void NewDraftStartsFromActiveApps()
    {
        var p = Office();
        var d = p.NewDraft("ďalšia", T);
        Expect.Equal(2, d.Apps.Count, "programy sa prevezmú z aktívnej verzie");
        Expect.Equal(1, d.BasedOn, "vychádza z v1");
    }
}
