using Polaris.Net;

namespace Polaris.Tests;

public static class NetChecks
{
    private static readonly Guid Pc1 = new("aaaa0000-0000-0000-0000-000000000001");
    private static readonly Guid Pc2 = new("aaaa0000-0000-0000-0000-000000000002");
    private static readonly DateTimeOffset Now = new(2026, 11, 10, 8, 0, 0, TimeSpan.Zero);

    private static (Fleet, SigningKey, ConsoleAccess) FleetWithConsole(Role role)
    {
        var key = SigningKey.Create();
        using var pub = key.Public();
        var console = new ConsoleAccess { Fingerprint = pub.Fingerprint(), Name = "Správca", Role = role, PublicKey = pub.Export() };
        var fleet = new Fleet
        {
            Groups = [new Group { Name = "stare", DefaultProfile = "KANCELARSKY" }],
            Consoles = [console],
            Pcs =
            [
                new Pc { Id = Pc1, Name = "PC-01", Group = "stare" },
                new Pc { Id = Pc2, Name = "PC-02", Group = "stare", Profile = "GRAFICKY" },
            ],
        };
        return (fleet, key, console);
    }

    [Check]
    public static void OnlyFixedTasksExist()
    {
        // every value of the enum has a description – there is no "other / arbitrary command"
        foreach (var kind in Enum.GetValues<TaskKind>())
        {
            Expect.True(Tasks.Describe(kind).Length > 0, $"popis úlohy {kind}");
        }
    }

    [Check]
    public static void UnknownTaskNumberIsRefusedEvenWhenSigned()
    {
        var (_, key, console) = FleetWithConsole(Role.Admin);
        var issuer = new TaskIssuer(console.Fingerprint, key);
        var signed = issuer.Make(Pc1, (TaskKind)99, "", Now, TimeSpan.FromMinutes(5));
        var checker = new TaskChecker(Pc1, _ => VerifyingKey.FromSubjectPublicKeyInfo(console.PublicKey), (_, _) => true);
        Expect.Equal(TaskVerdict.UnknownTask, checker.Check(signed, Now), "číslo mimo pevného zoznamu");
    }

    [Check]
    public static void SignedTaskRoundTrip()
    {
        var (fleet, key, console) = FleetWithConsole(Role.Admin);
        var issuer = new TaskIssuer(console.Fingerprint, key);
        var signed = issuer.Make(Pc1, TaskKind.Freeze, "", Now, TimeSpan.FromMinutes(5));
        var checker = new TaskChecker(Pc1,
            c => c == console.Fingerprint ? VerifyingKey.FromSubjectPublicKeyInfo(console.PublicKey) : null,
            (_, kind) => console.MaySend(kind));
        Expect.Equal(TaskVerdict.Ok, checker.Check(signed, Now), "dobrá úloha prejde");
        Expect.Equal(TaskVerdict.AlreadySeen, checker.Check(signed, Now), "tá istá úloha druhýkrát neprejde (opakovanie)");
        _ = fleet;
    }

    [Check]
    public static void TamperedTaskIsRefused()
    {
        var (_, key, console) = FleetWithConsole(Role.Admin);
        var issuer = new TaskIssuer(console.Fingerprint, key);
        var signed = issuer.Make(Pc1, TaskKind.Freeze, "", Now, TimeSpan.FromMinutes(5));
        signed.Task.Kind = TaskKind.Pause; // change the task after it was signed
        var checker = new TaskChecker(Pc1, _ => VerifyingKey.FromSubjectPublicKeyInfo(console.PublicKey), (_, _) => true);
        Expect.Equal(TaskVerdict.BadSignature, checker.Check(signed, Now), "zmenená úloha má zlý podpis");
    }

    [Check]
    public static void ExpiredAndWrongPcAndUnknownConsole()
    {
        var (_, key, console) = FleetWithConsole(Role.Admin);
        var issuer = new TaskIssuer(console.Fingerprint, key);
        var checker = new TaskChecker(Pc1, c => c == console.Fingerprint ? VerifyingKey.FromSubjectPublicKeyInfo(console.PublicKey) : null, (_, _) => true);
        var old = issuer.Make(Pc1, TaskKind.Restart, "", Now, TimeSpan.FromMinutes(5));
        Expect.Equal(TaskVerdict.Expired, checker.Check(old, Now.AddMinutes(6)), "po platnosti");
        var other = issuer.Make(Pc2, TaskKind.Restart, "", Now, TimeSpan.FromMinutes(5));
        Expect.Equal(TaskVerdict.NotForThisPc, checker.Check(other, Now), "pre iný PC");
        var stranger = new TaskChecker(Pc1, _ => null, (_, _) => true);
        Expect.Equal(TaskVerdict.UnknownConsole, stranger.Check(issuer.Make(Pc1, TaskKind.Restart, "", Now, TimeSpan.FromMinutes(5)), Now), "neznáma konzola");
    }

    [Check]
    public static void TeacherMayNotFreezeOrPause()
    {
        Expect.True(Tasks.AllowedForTeacher(TaskKind.Restart), "učiteľ smie reštartovať");
        Expect.True(Tasks.AllowedForTeacher(TaskKind.SwitchProfile), "učiteľ smie prepnúť profil");
        Expect.True(!Tasks.AllowedForTeacher(TaskKind.Pause), "učiteľ nesmie pozastaviť ochranu");
        Expect.True(!Tasks.AllowedForTeacher(TaskKind.ThawForRestarts), "učiteľ nesmie rozmraziť");
        var (_, _, console) = FleetWithConsole(Role.Teacher);
        Expect.True(!console.MaySend(TaskKind.Pause), "učiteľská konzola nepošle pauzu");
        Expect.True(console.MaySend(TaskKind.MessageToPupil), "učiteľská konzola pošle správu žiakovi");
    }

    [Check]
    public static void CentreRefusesByRole()
    {
        var (fleet, key, console) = FleetWithConsole(Role.Teacher);
        var centre = new Centre("linux", SigningKey.Create(), fleet, new EventLog());
        var issuer = new TaskIssuer(console.Fingerprint, key);
        Expect.Equal(Acceptance.NotAllowed, centre.Receive(issuer.Make(Pc1, TaskKind.Pause, "", Now, TimeSpan.FromMinutes(5)), Now), "učiteľ a pauza");
        Expect.Equal(Acceptance.Taken, centre.Receive(issuer.Make(Pc1, TaskKind.Restart, "", Now, TimeSpan.FromMinutes(5)), Now), "učiteľ a reštart");
        Expect.Equal(1, centre.Log.Entries.Count, "do denníka sa zapísala len prijatá úloha");
    }

    [Check]
    public static void UnknownPcIsRefused()
    {
        var (fleet, key, console) = FleetWithConsole(Role.Admin);
        var centre = new Centre("linux", SigningKey.Create(), fleet, new EventLog());
        var issuer = new TaskIssuer(console.Fingerprint, key);
        var strange = issuer.Make(Guid.NewGuid(), TaskKind.Restart, "", Now, TimeSpan.FromMinutes(5));
        Expect.Equal(Acceptance.UnknownPc, centre.Receive(strange, Now), "neznámy PC");
    }

    [Check]
    public static void ProfileResolvesNarrowestFirst()
    {
        var (fleet, _, _) = FleetWithConsole(Role.Admin);
        Expect.Equal("KANCELARSKY", fleet.ProfileFor(fleet.Find(Pc1)!), "PC bez profilu dostane profil skupiny");
        Expect.Equal("GRAFICKY", fleet.ProfileFor(fleet.Find(Pc2)!), "vlastný profil PC má prednosť");
    }

    [Check]
    public static void OnlineByLastSeen()
    {
        var pc = new Pc { Id = Pc1, LastSeenUtc = Now };
        Expect.True(pc.OnlineAt(Now.AddSeconds(30), TimeSpan.FromMinutes(1)), "nedávno ohlásený je online");
        Expect.True(!pc.OnlineAt(Now.AddMinutes(2), TimeSpan.FromMinutes(1)), "dlho ticho = offline");
    }

    [Check]
    public static void ArgumentsRoundTrip()
    {
        Expect.True(TaskArgument.TryRestarts(TaskArgument.Restarts(3), out var n) && n == 3, "počet reštartov");
        Expect.True(!TaskArgument.TryRestarts("0", out _), "nula nie je platný počet");
        var moment = DateTimeOffset.FromUnixTimeSeconds(Now.ToUnixTimeSeconds());
        Expect.True(TaskArgument.TryUntil(TaskArgument.Until(moment), out var back) && back == moment, "moment pauzy");
    }

    // ---- the two centres keep each other whole ----

    [Check]
    public static void TwoCentresBecomeEqual()
    {
        var kA = SigningKey.Create();
        var kB = SigningKey.Create();
        VerifyingKey? ByOrigin(string o) => o == "linux" ? kA.Public() : o == "windows" ? kB.Public() : null;
        var a = new EventLog();
        var b = new EventLog();
        a.Write("linux", kA, "uloha", "A1", Pc1, Now);
        a.Write("linux", kA, "uloha", "A2", Pc1, Now);
        b.Write("windows", kB, "uloha", "B1", Pc2, Now);

        var (toA, toB) = Sync.Exchange(a, b, ByOrigin);
        Expect.Equal(1, toA, "A prevzalo B1");
        Expect.Equal(2, toB, "B prevzalo A1, A2");
        Expect.Equal(3, a.Entries.Count, "A má všetky");
        Expect.Equal(3, b.Entries.Count, "B má všetky");

        // another round does nothing (idempotent)
        var (toA2, toB2) = Sync.Exchange(a, b, ByOrigin);
        Expect.Equal(0, toA2 + toB2, "druhé kolo už nič neprenesie");
    }

    [Check]
    public static void CentreCatchesUpAfterOutage()
    {
        var kA = SigningKey.Create();
        var kB = SigningKey.Create();
        VerifyingKey? ByOrigin(string o) => o == "linux" ? kA.Public() : o == "windows" ? kB.Public() : null;
        var a = new EventLog();
        var b = new EventLog();
        // B is off: A keeps working
        for (var i = 0; i < 20; i++)
        {
            a.Write("linux", kA, "uloha", "A" + i, Pc1, Now.AddMinutes(i));
        }

        var (_, toB) = Sync.Exchange(a, b, ByOrigin);
        Expect.Equal(20, toB, "B po návrate dobehlo všetkých 20");
        Expect.Equal(20, b.Entries.Count, "B má celý denník");
    }

    [Check]
    public static void ForgedLogEntryIsRefused()
    {
        var real = SigningKey.Create();
        var fake = SigningKey.Create();
        var a = new EventLog();
        var entry = new LogEntry { Origin = "linux", Sequence = 1, Utc = Now, Kind = "uloha", Text = "podvrh", Pc = Pc1 };
        entry.Signature = fake.Sign(entry.BytesToSign()); // signed by the wrong key
        Expect.True(!a.Accept(entry, _ => real.Public()), "podpis cudzím kľúčom sa odmietne");
        Expect.Equal(0, a.Entries.Count, "nič sa nepridalo");
    }

    [Check]
    public static void LogEntriesSurviveJson()
    {
        var key = SigningKey.Create();
        var log = new EventLog();
        var entry = log.Write("linux", key, "uloha", "čosi s diakritikou ľščťž", Pc1, Now);
        var json = System.Text.Json.JsonSerializer.Serialize(entry, NetJson.Default.LogEntry);
        var back = System.Text.Json.JsonSerializer.Deserialize(json, NetJson.Default.LogEntry)!;
        using var pub = key.Public();
        Expect.True(pub.Verify(back.BytesToSign(), back.Signature), "podpis platí aj po JSON");
        Expect.Equal(entry.Text, back.Text, "text prežil");
    }
}
