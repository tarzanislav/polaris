using System.Globalization;
using System.Text;
using Polaris.Json;
using Polaris.Net;
using Polaris.Recipes;
using Polaris.Web;

namespace Polaris.Centrum;

/// <summary>The PCs of the centre (K7): codes for registering PCs into a group, the registration itself, the reports of
/// PCs and the console's view of them. A console signs what it sends (codes, questions); a PC signs with its own key.</summary>
public static partial class Commands
{
    public const string CodesPath = CentrePaths.Codes;
    public const string RegisterPath = CentrePaths.Register;
    public const string ReportPath = CentrePaths.Report;
    public const string PcsPath = CentrePaths.Pcs;

    /// <summary>A PC that has not reported for this long counts as off (it reports every 15 minutes).</summary>
    public static readonly TimeSpan Silent = TimeSpan.FromMinutes(40);

    public static readonly IReadOnlySet<string> BodyPaths = new HashSet<string>(StringComparer.Ordinal) { UploadPath, CodesPath, RegisterPath, ReportPath, PcsPath };

    /// <summary>The routes of the PCs: codes from consoles, registrations and reports of PCs, the list for consoles.</summary>
    public static Func<ServerRequest, ServerResponse> PcRoutes(PcRegistry registry, string serverPin, Func<IReadOnlyList<TrustedConsole>> trusted, Func<DateTimeOffset> clock) => request =>
    {
        var body = request.Body ?? [];
        var now = clock();
        switch (request.Path)
        {
            case CodesPath:
                {
                    var consoles = trusted();
                    var (console, problem) = UploadSigning.Verify(request.Header, body, request.Path, serverPin, consoles, now);
                    if (console is null)
                    {
                        return Reply(403, false, problem);
                    }

                    var grant = RegistrationGrant.FromJson(Encoding.UTF8.GetString(body));
                    var (by, why) = grant.Check(consoles, now);
                    if (by is null || grant.NotAfterUtc - now > RegistrationGrant.Longest)
                    {
                        return Reply(400, false, by is null ? why : "kód môže platiť najviac 7 dní");
                    }

                    registry.AddGrant(grant);
                    return Reply(200, true, $"kód pre skupinu {grant.Group} platí do {grant.NotAfterUtc:O} (najviac {grant.MaxUses} PC)");
                }

            case RegisterPath:
                {
                    var registration = RegistrationRequest.FromJson(Encoding.UTF8.GetString(body));
                    var (record, problem) = registry.Register(registration, serverPin, trusted(), now);
                    if (record is null || registry.Grant(RegistrationCode.GrantHash(registration.Ticket)) is not { } grant)
                    {
                        return Reply(403, false, record is null ? problem : "kód medzitým zmizol");
                    }

                    // the consoles the PC is to trust, sealed by the code: the centre hands them over unchanged
                    return Json(200, new JsonObject().Add("ok", true).Add("message", "zaregistrované").Add("pc", record.Id).Add("group", record.Group)
                        .Add("trust", grant.Trust).Add("trustSeal", grant.TrustSeal));
                }

            case ReportPath:
                {
                    var (record, problem) = PcSigning.Verify(request.Header, body, request.Path, serverPin, registry.Find, now);
                    if (record is null)
                    {
                        return Reply(403, false, problem);
                    }

                    if (body.Length > PcReport.Largest)
                    {
                        return Reply(413, false, "hlásenie je príliš veľké");
                    }

                    registry.SaveReport(record.Id, PcReport.FromJson(Encoding.UTF8.GetString(body)), now);
                    return Json(200, new JsonObject().Add("ok", true).Add("message", "prijaté").Add("group", record.Group));
                }

            case PcsPath:
                {
                    var (console, problem) = UploadSigning.Verify(request.Header, body, request.Path, serverPin, trusted(), now);
                    if (console is null)
                    {
                        return Reply(403, false, problem);
                    }

                    var f = new JsonFields(JsonReader.Parse(Encoding.UTF8.GetString(body), JsonSyntax.Strict), "otázka konzoly");
                    var command = f.String("command");
                    var id = f.String("pc");
                    f.End();
                    if (command == "remove")
                    {
                        return registry.Remove(id) ? Reply(200, true, $"PC {id} odobraté") : Reply(404, false, $"PC {id} centrum nepozná");
                    }

                    var list = new JsonArray();
                    foreach (var record in registry.All())
                    {
                        var item = new JsonObject().Add("record", JsonReader.Parse(record.ToJson()));
                        if (registry.Last(record.Id) is { } last)
                        {
                            item.Add("report", JsonReader.Parse(last.Report.ToJson())).Add("received", last.Received.ToString("O", CultureInfo.InvariantCulture));
                        }

                        list.Add(item);
                    }

                    return Json(200, new JsonObject().Add("ok", true).Add("now", now.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)).Add("pcs", list));
                }

            default:
                return Reply(405, false, "Sem sa nič neodovzdáva.");
        }
    };

    private static ServerResponse Reply(int status, bool ok, string message) =>
        Json(status, new JsonObject().Add("ok", ok).Add("message", message));

    private static ServerResponse Json(int status, JsonObject o) => new() { Status = status, Body = Utf8NoBom.GetBytes(JsonText.Canonical(o)) };

    /// <summary>kod SKUPINA KLUC --server URL ODTLAČOK [--dni N] [--pocet N]: a code for registering PCs into the group.</summary>
    private static int Code(List<string> rest, TextWriter o, DateTimeOffset utcNow, Func<string, string?> secret)
    {
        var server = ServerOption(rest, "kod SKUPINA KLUC --server https://SERVER:8443 ODTLAČOK [--dni 1] [--pocet 30]");
        var days = IntOption(rest, "--dni", 1, 1, 7);
        var uses = IntOption(rest, "--pocet", 30, 1, RegistrationGrant.MostUses);
        Need(rest, 2, "kod SKUPINA KLUC --server https://SERVER:8443 ODTLAČOK [--dni 1] [--pocet 30]");
        var group = rest[0];
        if (!PcRules.GroupName(group))
        {
            throw new UsageException("Skupina: písmená, číslice, - a _ (do 40 znakov).");
        }

        var keyFile = RecipeSigning.KeyFromJson(File.ReadAllText(rest[1]));
        using var key = RecipeSigning.OpenPrivate(keyFile, secret($"Heslo ku kľúču {keyFile.Name}: ") ?? "");
        // every console of the administrator the new PCs are to trust: this one and the public keys lying beside its key
        var trust = new List<TrustedConsole> { RecipeSigning.Trust(keyFile) };
        foreach (var file in Directory.GetFiles(Path.GetDirectoryName(Path.GetFullPath(rest[1]))!, "*.verejny.json").Order(StringComparer.Ordinal))
        {
            try
            {
                trust.Add(RecipeSigning.Trust(RecipeSigning.KeyFromJson(File.ReadAllText(file))));
            }
            catch (InvalidDataException e)
            {
                o.WriteLine($"Pozor: {Path.GetFileName(file)} vynechaný – {e.Message}");
            }
        }

        var code = RegistrationCode.Generate();
        var grant = RegistrationGrant.Sign(group, utcNow.AddDays(days), uses, RegistrationCode.Normalize(code)!, key, trust);
        var answer = Ask(server, CodesPath, Utf8NoBom.GetBytes(grant.ToJson()), key, utcNow);
        if (!answer.Ok)
        {
            o.WriteLine("Centrum kód neprijalo: " + answer.Message);
            return 1;
        }

        o.WriteLine($"Kód na registráciu PC do skupiny {group}: {code}");
        o.WriteLine($"Platí {days} {(days == 1 ? "deň" : "dni")} (do {grant.NotAfterUtc.ToLocalTime():d. M. yyyy H:mm}), najviac {uses} PC.");
        o.WriteLine("PC budú dôverovať konzolám: " + string.Join(", ", grant.Consoles().Select(c => c.Name)) + ". Na PC (správca):");
        o.WriteLine($"  polaris-agent registrovat {server.Address} {server.Pin} {code}");
        o.WriteLine("Kód nikam neukladaj – centrum pozná len jeho odtlačok.");
        return 0;
    }

    /// <summary>stav KLUC --server URL ODTLAČOK [PC [odobrat]]: the PCs of the centre and what they last said.</summary>
    private static int Status(List<string> rest, TextWriter o, DateTimeOffset utcNow, Func<string, string?> secret)
    {
        const string Usage = "stav KLUC --server https://SERVER:8443 ODTLAČOK [PC [odobrat]]";
        var server = ServerOption(rest, Usage);
        if (rest.Count is < 1 or > 3 || (rest.Count == 3 && rest[2] != "odobrat"))
        {
            throw new UsageException("Použitie: " + Usage);
        }

        var keyFile = RecipeSigning.KeyFromJson(File.ReadAllText(rest[0]));
        using var key = RecipeSigning.OpenPrivate(keyFile, secret($"Heslo ku kľúču {keyFile.Name}: ") ?? "");
        var wanted = rest.Count > 1 ? rest[1] : null;
        var answer = Ask(server, PcsPath, Utf8NoBom.GetBytes(JsonText.Canonical(new JsonObject().Add("command", "list").Add("pc", ""))), key, utcNow);
        if (!answer.Ok || answer.Fields is not { } f)
        {
            o.WriteLine("Centrum neodpovedalo: " + answer.Message);
            return 1;
        }

        var now = f.Time("now", utcNow);
        var pcs = f.Array("pcs").Select(n => PcView.From(new JsonFields(n, "PC"))).ToList();
        var matching = wanted is null ? [] : pcs.Where(p => p.Record.Id.StartsWith(wanted, StringComparison.OrdinalIgnoreCase) || string.Equals(p.Record.Pc.Name, wanted, StringComparison.OrdinalIgnoreCase)).ToList();
        if (wanted is not null && matching.Count != 1)
        {
            o.WriteLine(matching.Count == 0 ? $"PC „{wanted}“ centrum nepozná." : $"„{wanted}“ sedí na {matching.Count} PC – zadaj dlhší začiatok identifikátora.");
            return 1;
        }

        if (rest.Count == 3)
        {
            var removed = Ask(server, PcsPath, Utf8NoBom.GetBytes(JsonText.Canonical(new JsonObject().Add("command", "remove").Add("pc", matching[0].Record.Id))), key, utcNow);
            o.WriteLine(removed.Message);
            return removed.Ok ? 0 : 1;
        }

        if (wanted is not null)
        {
            Detail(matching[0], now, o);
            return 0;
        }

        if (pcs.Count == 0)
        {
            o.WriteLine("Centrum zatiaľ nepozná žiadne PC (kód: polaris-centrum kod SKUPINA KLUC --server …).");
            return 0;
        }

        o.WriteLine($"PC v centre {server.Address}: {pcs.Count}");
        foreach (var group in pcs.GroupBy(p => p.Record.Group).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            o.WriteLine($"Skupina {group.Key}:");
            foreach (var pc in group.OrderBy(p => p.Record.Pc.Name, StringComparer.OrdinalIgnoreCase))
            {
                o.WriteLine("  " + pc.Line(now));
            }
        }

        return 0;
    }

    private static void Detail(PcView pc, DateTimeOffset now, TextWriter o)
    {
        var r = pc.Record;
        o.WriteLine($"{r.Pc.Name} ({r.Id[..8]}) – skupina {r.Group}, zaregistrované {r.RegisteredUtc.ToLocalTime():d. M. yyyy H:mm}");
        o.WriteLine($"  {r.Pc.Hardware} · {r.Pc.Windows} · agent {r.Pc.Agent}");
        if (pc.Report is not { } report)
        {
            o.WriteLine("  Hlásenie zatiaľ neprišlo.");
            return;
        }

        o.WriteLine("  " + pc.Line(now));
        o.WriteLine($"  Disk: voľné {PcSize(report.FreeDiskBytes)} z {PcSize(report.TotalDiskBytes)} · zapnuté {Uptime(report.UptimeSeconds)} · trezor: {(report.Trezor.Length > 0 ? report.Trezor : "–")}");
        var runs = RunReport.ReadJournal(string.Join("\n", report.Measurements));
        foreach (var m in runs)
        {
            o.WriteLine($"  Meranie {m.StartedUtc.ToLocalTime():d. M. H:mm}: " + string.Join(" · ", m.Results.Take(3).Select(x => x.Message)));
        }

        o.WriteLine("  Denník (posledné):");
        foreach (var run in RunReport.ReadJournal(string.Join("\n", report.Journal)).TakeLast(15))
        {
            o.WriteLine($"    {run.StartedUtc.ToLocalTime():d. M. H:mm}  {run.Recipe}  {(run.Failed == 0 ? "v poriadku" : $"chyby: {run.Failed}")}");
            foreach (var bad in run.Results.Where(x => !x.Ok).Take(5))
            {
                o.WriteLine($"      ✗ {bad.Title}: {bad.Message}");
            }

            foreach (var skipped in run.Skipped.Take(3))
            {
                o.WriteLine($"      ✗ {skipped}");
            }
        }
    }

    /// <summary>One PC as the console sees it.</summary>
    private sealed record PcView(PcRecord Record, PcReport? Report, DateTimeOffset? Received)
    {
        public static PcView From(JsonFields f)
        {
            var record = PcRecord.FromJson(JsonText.Canonical(f.Node("record") ?? throw new InvalidDataException("Centrum poslalo PC bez záznamu.")));
            var report = f.Node("report") is { } node ? PcReport.FromJson(JsonText.Canonical(node)) : null;
            DateTimeOffset? received = f.Has("received") ? f.Time("received") : null;
            f.End();
            return new PcView(record, report, received);
        }

        public string Line(DateTimeOffset now)
        {
            if (Report is not { } r || Received is not { } at)
            {
                return $"{Record.Pc.Name,-16} ? zatiaľ bez hlásenia";
            }

            var failed = RunReport.ReadJournal(string.Join("\n", r.Journal)).Where(x => x.Recipe is not "(meranie)" and not "(prihlásenie)").TakeLast(10).Sum(x => x.Failed);
            var silent = now - at > Silent;
            var mark = silent ? "✗" : failed > 0 || r.DevicesWithoutDriver > 0 ? "!" : "✓";
            var parts = new List<string>
            {
                silent ? $"neozvalo sa {Ago(now - at)}" : $"ozvalo sa pred {Ago(now - at)}",
                r.Profile.Length > 0 ? "profil " + r.Profile : "bez profilu z centra",
            };
            if (failed > 0)
            {
                parts.Add($"chyby receptov: {failed}");
            }

            if (r.DevicesWithoutDriver > 0)
            {
                parts.Add($"bez ovládača: {r.DevicesWithoutDriver}");
            }

            if (r.TotalDiskBytes > 0 && r.FreeDiskBytes * 10 < r.TotalDiskBytes)
            {
                parts.Add($"málo miesta na disku ({PcSize(r.FreeDiskBytes)})");
            }

            return $"{Record.Pc.Name,-16} {mark} {string.Join(" · ", parts)}";
        }
    }

    private static string Ago(TimeSpan t) =>
        t.TotalMinutes < 1 ? "chvíľou" : t.TotalHours < 1 ? $"{t.TotalMinutes:0} min" : t.TotalDays < 1 ? $"{t.TotalHours:0} h" : $"{t.TotalDays:0} d";

    private static string Uptime(long seconds) => Ago(TimeSpan.FromSeconds(seconds));

    private static string PcSize(long bytes) =>
        bytes >= 1L << 30 ? Polaris.Core.Format.Decimal(bytes / (double)(1L << 30), 1) + " GB" : Polaris.Core.Format.Decimal(bytes / (double)(1L << 20), 0) + " MB";

    private sealed record Answered(bool Ok, string Message, JsonFields? Fields);

    /// <summary>A question or upload of this console to the centre, signed for it.</summary>
    private static Answered Ask((Uri Address, string Pin) server, string path, byte[] body, SigningKey key, DateTimeOffset now)
    {
        var headers = UploadSigning.Headers(body, path, server.Pin, key, now);
        var answer = PinnedClient.Post(new Uri(server.Address, path.TrimStart('/')), server.Pin, body, headers, 8 * 1024 * 1024, TimeSpan.FromMinutes(1));
        if (answer.Status == 0)
        {
            return new Answered(false, answer.Error ?? "centrum nedostupné", null);
        }

        try
        {
            var f = new JsonFields(JsonReader.Parse(Encoding.UTF8.GetString(answer.Body), new JsonSyntax(false, false, 32, 64 * 1024 * 1024)), "odpoveď centra");
            return new Answered(answer.Ok && f.Bool("ok", false), f.String("message"), f);
        }
        catch (Exception e) when (e is JsonError or InvalidDataException)
        {
            return new Answered(false, $"centrum odpovedalo {answer.Status}: {Encoding.UTF8.GetString(answer.Body).Trim()}", null);
        }
    }

    private static (Uri Address, string Pin) ServerOption(List<string> rest, string usage)
    {
        var at = rest.IndexOf("--server");
        if (at < 0 || at + 2 >= rest.Count || !Uri.TryCreate(rest[at + 1], UriKind.Absolute, out var address) || address.Scheme != Uri.UriSchemeHttps
            || rest[at + 2].Length != 64 || !rest[at + 2].All(char.IsAsciiHexDigit))
        {
            throw new UsageException("Použitie: " + usage);
        }

        var pin = rest[at + 2].ToLowerInvariant();
        rest.RemoveRange(at, 3);
        return (address, pin);
    }

    private static int IntOption(List<string> rest, string name, int fallback, int min, int max)
    {
        var at = rest.IndexOf(name);
        if (at < 0)
        {
            return fallback;
        }

        if (at + 1 >= rest.Count || !int.TryParse(rest[at + 1], NumberStyles.None, CultureInfo.InvariantCulture, out var n) || n < min || n > max)
        {
            throw new UsageException($"{name}: číslo od {min} do {max}.");
        }

        rest.RemoveRange(at, 2);
        return n;
    }
}
