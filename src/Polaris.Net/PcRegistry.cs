using System.Globalization;
using System.Text;

namespace Polaris.Net;

/// <summary>The PCs a centre knows, in its own folder: the codes consoles issued (kody), the registered PCs (zoznam) and
/// their reports (hlasenia – the last one and one file a day, kept 30 days). Every file is written beside and moved
/// over, so a power loss never leaves half a record.</summary>
public sealed class PcRegistry(string root)
{
    public static readonly TimeSpan KeepReports = TimeSpan.FromDays(30);

    private static readonly UTF8Encoding Utf8 = new(false);
    private readonly Lock _gate = new();

    public string Root { get; } = root;

    private string Codes => Path.Combine(Root, "kody");

    private string Pcs => Path.Combine(Root, "zoznam");

    private string Reports => Path.Combine(Root, "hlasenia");

    private string Waiting => Path.Combine(Root, "prihlasky");

    /// <summary>At most this many PCs wait at once; older applications go after a week.</summary>
    public const int MostWaiting = 300;

    public static readonly TimeSpan KeepWaiting = TimeSpan.FromDays(7);

    /// <summary>A PC applies without a code: its approval when a console approved it, otherwise it waits (kept, newest
    /// first) – or the reason it is refused.</summary>
    public (PcApproval? Approval, bool Waiting, string Problem) Apply(PcApplication application, string serverPin, DateTimeOffset now)
    {
        if (!application.Proven(serverPin, now))
        {
            return (null, false, "prihláška nie je podpísaná kľúčom PC pre toto centrum (alebo sa líšia hodiny o viac ako 10 minút)");
        }

        var id = application.Pc.Id;
        lock (_gate)
        {
            if (Find(id) is not null)
            {
                return Approval(id) is { } approval ? (approval, false, "") : (null, false, "PC je už zaregistrované kódom");
            }

            Directory.CreateDirectory(Waiting);
            var file = Path.Combine(Waiting, id + ".json");
            if (!File.Exists(file) && WaitingNow(now).Count >= MostWaiting)
            {
                return (null, false, "na schválenie čaká príliš veľa PC – najprv ich v konzole prijmi alebo zamietni");
            }

            Write(file, application.ToJson());
            return (null, true, "");
        }
    }

    /// <summary>The PCs waiting for a console, newest first, with when they last asked.</summary>
    public List<(PcApplication Application, DateTimeOffset Asked)> WaitingNow(DateTimeOffset now)
    {
        var list = new List<(PcApplication, DateTimeOffset)>();
        if (!Directory.Exists(Waiting))
        {
            return list;
        }

        foreach (var file in Directory.GetFiles(Waiting, "*.json"))
        {
            var asked = new DateTimeOffset(File.GetLastWriteTimeUtc(file), TimeSpan.Zero);
            try
            {
                var application = PcApplication.FromJson(File.ReadAllText(file, Utf8));
                if (DateTimeOffset.TryParse(application.Time, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var at))
                {
                    asked = at;
                }

                if (now - asked > KeepWaiting || Path.GetFileNameWithoutExtension(file) != application.Pc.Id)
                {
                    File.Delete(file);
                    continue;
                }

                list.Add((application, asked));
            }
            catch (InvalidDataException)
            {
                File.Delete(file);
            }
        }

        return list.OrderByDescending(x => x.Item2).ToList();
    }

    /// <summary>A console admits a waiting PC: the approval is checked against the consoles the centre trusts, the PC
    /// becomes registered and its application and approval stay beside its record (proof for another centre).</summary>
    public (PcRecord? Record, string Problem) Approve(PcApproval approval, string serverPin, IReadOnlyList<TrustedConsole> consoles, DateTimeOffset now)
    {
        lock (_gate)
        {
            var file = Path.Combine(Waiting, approval.Pc + ".json");
            if (!PcRules.Id(approval.Pc) || !File.Exists(file))
            {
                return (null, "také PC na schválenie nečaká");
            }

            var application = PcApplication.FromJson(File.ReadAllText(file, Utf8));
            var (trust, problem) = approval.Check(application.Pc.Id, serverPin, consoles.Select(c => c.Fingerprint));
            if (trust is null)
            {
                return (null, problem);
            }

            var record = new PcRecord(application.Pc, approval.Group, now.ToUniversalTime(), "schvalenie:" + approval.ConsoleFingerprint(), "");
            Write(Path.Combine(Pcs, record.Id + ".prihlaska.json"), application.ToJson());
            Write(Path.Combine(Pcs, record.Id + ".schvalenie.json"), approval.ToJson());
            Write(Path.Combine(Pcs, record.Id + ".json"), record.ToJson());
            File.Delete(file);
            return (record, "");
        }
    }

    public bool Reject(string id)
    {
        lock (_gate)
        {
            var file = Path.Combine(Waiting, id + ".json");
            if (!PcRules.Id(id) || !File.Exists(file))
            {
                return false;
            }

            File.Delete(file);
            return true;
        }
    }

    /// <summary>The approval of a PC admitted by a console, or null.</summary>
    public PcApproval? Approval(string id)
    {
        var file = Path.Combine(Pcs, id + ".schvalenie.json");
        return PcRules.Id(id) && File.Exists(file) ? PcApproval.FromJson(File.ReadAllText(file, Utf8)) : null;
    }

    public void AddGrant(RegistrationGrant grant) => Write(Path.Combine(Codes, grant.CodeHash + ".json"), grant.ToJson());

    public RegistrationGrant? Grant(string codeHash)
    {
        var file = Path.Combine(Codes, codeHash + ".json");
        return codeHash.Length == 64 && codeHash.All(char.IsAsciiHexDigitLower) && File.Exists(file) ? RegistrationGrant.FromJson(File.ReadAllText(file, Utf8)) : null;
    }

    public PcRecord? Find(string id)
    {
        var file = Path.Combine(Pcs, id + ".json");
        return PcRules.Id(id) && File.Exists(file) ? PcRecord.FromJson(File.ReadAllText(file, Utf8)) : null;
    }

    /// <summary>What proves the PC's registration to another centre: its signed request and the code it used, or null.</summary>
    public (RegistrationRequest Request, RegistrationGrant Grant)? Proof(string id)
    {
        var request = Path.Combine(Pcs, id + ".registracia.json");
        var grant = Path.Combine(Pcs, id + ".kod.json");
        return PcRules.Id(id) && File.Exists(request) && File.Exists(grant)
            ? (RegistrationRequest.FromJson(File.ReadAllText(request, Utf8)), RegistrationGrant.FromJson(File.ReadAllText(grant, Utf8)))
            : null;
    }

    public List<PcRecord> All()
    {
        var list = new List<PcRecord>();
        if (!Directory.Exists(Pcs))
        {
            return list;
        }

        foreach (var file in Directory.GetFiles(Pcs, "*.json").Where(f => PcRules.Id(Path.GetFileNameWithoutExtension(f))).Order(StringComparer.Ordinal))
        {
            try
            {
                list.Add(PcRecord.FromJson(File.ReadAllText(file, Utf8)));
            }
            catch (InvalidDataException)
            {
                // a damaged record is skipped; the PC registers again
            }
        }

        return list;
    }

    /// <summary>Registers the PC with the code: the result and the record, or why not. A PC that is already registered
    /// (the same key) gets its record again – the agent may ask twice after a lost answer.</summary>
    public (PcRecord? Record, string Problem) Register(RegistrationRequest request, string serverPin, IReadOnlyList<TrustedConsole> consoles, DateTimeOffset now)
    {
        if (request.Ticket.Length != 64 || !request.Ticket.All(char.IsAsciiHexDigitLower) || request.CodeProof.Length != 64)
        {
            return (null, "registrácia nemá kód v správnom tvare");
        }

        if (!request.Proven(serverPin, now))
        {
            return (null, "registrácia nie je podpísaná kľúčom PC pre toto centrum (alebo sa líšia hodiny o viac ako 10 minút)");
        }

        lock (_gate)
        {
            if (Grant(RegistrationCode.GrantHash(request.Ticket)) is not { } grant)
            {
                return (null, "taký kód centrum nevydalo");
            }

            var (console, problem) = grant.Check(consoles, now);
            if (console is null)
            {
                return (null, problem);
            }

            if (Find(request.Pc.Id) is { } known)
            {
                return known.Group == grant.Group ? (known, "") : (null, $"PC je už zaregistrované v skupine {known.Group} – najprv ho odober v konzole");
            }

            var used = All().Count(r => r.Grant == grant.Id);
            if (used >= grant.MaxUses)
            {
                return (null, $"kód už použilo {used} PC – vydaj nový");
            }

            var record = new PcRecord(request.Pc, grant.Group, now.ToUniversalTime(), grant.Id, request.CodeProof);
            // the PC's own signed registration and the console's signed code beside the record: a second centre added later
            // (design 4.16) checks both itself – it does not have to believe this one
            Write(Path.Combine(Pcs, record.Id + ".registracia.json"), request.ToJson());
            Write(Path.Combine(Pcs, record.Id + ".kod.json"), grant.ToJson());
            Write(Path.Combine(Pcs, record.Id + ".json"), record.ToJson());
            return (record, "");
        }
    }

    public bool Remove(string id)
    {
        lock (_gate)
        {
            var file = Path.Combine(Pcs, id + ".json");
            if (!PcRules.Id(id) || !File.Exists(file))
            {
                return false;
            }

            File.Delete(file);
            File.Delete(Path.Combine(Pcs, id + ".registracia.json"));
            File.Delete(Path.Combine(Pcs, id + ".kod.json"));
            File.Delete(Path.Combine(Pcs, id + ".prihlaska.json"));
            File.Delete(Path.Combine(Pcs, id + ".schvalenie.json"));
            return true;
        }
    }

    /// <summary>Keeps the report as the PC's last one and adds it to the file of the day; days older than 30 go.</summary>
    public void SaveReport(string id, PcReport report, DateTimeOffset now)
    {
        var folder = Path.Combine(Reports, id);
        var json = report.ToJson();
        lock (_gate)
        {
            Write(Path.Combine(folder, "posledne.json"), json);
            Write(Path.Combine(folder, "posledne.cas"), now.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
            File.AppendAllText(Path.Combine(folder, now.ToUniversalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".jsonl"), json + "\n", Utf8);
            foreach (var day in Directory.GetFiles(folder, "????-??-??.jsonl"))
            {
                if (DateTime.TryParseExact(Path.GetFileNameWithoutExtension(day), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date)
                    && now.UtcDateTime.Date - date.ToUniversalTime().Date > KeepReports)
                {
                    File.Delete(day);
                }
            }
        }
    }

    /// <summary>The last report of the PC and when it came, or null.</summary>
    public (PcReport Report, DateTimeOffset Received)? Last(string id)
    {
        var file = Path.Combine(Reports, id, "posledne.json");
        if (!PcRules.Id(id) || !File.Exists(file))
        {
            return null;
        }

        try
        {
            var time = Path.Combine(Reports, id, "posledne.cas");
            var received = File.Exists(time) && DateTimeOffset.TryParse(File.ReadAllText(time, Utf8), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var at)
                ? at
                : new DateTimeOffset(File.GetLastWriteTimeUtc(file), TimeSpan.Zero);
            return (PcReport.FromJson(File.ReadAllText(file, Utf8)), received);
        }
        catch (InvalidDataException)
        {
            return null;
        }
    }

    private static void Write(string file, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file + ".novy", text, Utf8);
        File.Move(file + ".novy", file, overwrite: true);
    }
}
