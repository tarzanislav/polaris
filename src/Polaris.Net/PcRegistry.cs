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

    public List<PcRecord> All()
    {
        var list = new List<PcRecord>();
        if (!Directory.Exists(Pcs))
        {
            return list;
        }

        foreach (var file in Directory.GetFiles(Pcs, "*.json").Order(StringComparer.Ordinal))
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
        if (RegistrationCode.Normalize(request.Code) is not { } code)
        {
            return (null, "kód má 12 znakov (napr. 7K3Q-9XHD-M2PA)");
        }

        if (!request.Proven(serverPin, now))
        {
            return (null, "registrácia nie je podpísaná kľúčom PC pre toto centrum (alebo sa líšia hodiny o viac ako 10 minút)");
        }

        lock (_gate)
        {
            if (Grant(RegistrationCode.Hash(code)) is not { } grant)
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

            var record = new PcRecord(request.Pc, grant.Group, now.ToUniversalTime(), grant.Id, PcRecord.ProofOf(code, request.Pc));
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
