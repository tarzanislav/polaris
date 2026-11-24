using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Polaris.Json;
using Polaris.Net;
using Polaris.Recipes;
using Polaris.Web;

namespace Polaris.Centrum;

/// <summary>The console in the browser (K7c, v1): a small web server on 127.0.0.1 of the administrator's own PC. The key
/// of the console is unlocked once with its password and stays in this process – the browser never sees it; every
/// question to the centre is signed here. Only this PC can open it, and only with the random key of this run (in the
/// address after #, sent as a header – a page of another site can neither read nor send it).</summary>
public static partial class Commands
{
    public const int ConsolePort = 8444;
    public const string ConsoleTokenHeader = "X-Polaris-Konzola";

    private const string ConsoleUsage = "konzola KLUC --server https://SERVER:8443 ODTLAČOK [--port 8444] [--neotvarat]";

    /// <summary>konzola KLUC --server URL ODTLAČOK: the console in the browser until Ctrl+C.</summary>
    private static int ConsoleCommand(List<string> rest, TextWriter o, Func<string, string?> secret, CancellationToken stop)
    {
        var server = ServerOption(rest, ConsoleUsage);
        var port = IntOption(rest, "--port", ConsolePort, 1024, 65535);
        var open = !rest.Remove("--neotvarat");
        Need(rest, 1, ConsoleUsage);
        var keyFile = RecipeSigning.KeyFromJson(File.ReadAllText(rest[0]));
        using var key = RecipeSigning.OpenPrivate(keyFile, secret($"Heslo ku kľúču {keyFile.Name}: ") ?? "");
        var token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(24));
        var web = new WebServer(new IPEndPoint(IPAddress.Loopback, port), null, ConsoleRoutes(server, rest[0], keyFile, key, token, port, () => DateTimeOffset.UtcNow), line => o.WriteLine(line), ConsoleBody.Contains);
        var address = $"http://127.0.0.1:{port}/#{token}";
        o.WriteLine($"POLARIS konzola {keyFile.Name} – centrum {server.Address}");
        o.WriteLine($"Otvor v prehliadači na tomto PC: {address}");
        o.WriteLine("(adresa platí len pre tento beh; koniec: Ctrl+C)");
        var running = web.RunAsync(stop);
        if (open)
        {
            OpenBrowser(address);
        }

        running.GetAwaiter().GetResult();
        o.WriteLine("Konzola zatvorená.");
        return 0;
    }

    private static readonly IReadOnlySet<string> ConsoleBody = new HashSet<string>(StringComparer.Ordinal) { "/api/kod", "/api/odobrat" };

    /// <summary>The routes of the console: the page and its script and look (no key needed), and the API (the key of this
    /// run in a header, the address must be this PC's own).</summary>
    public static Func<ServerRequest, ServerResponse> ConsoleRoutes((Uri Address, string Pin) server, string keyPath, ConsoleKeyFile keyFile, SigningKey key, string token, int port, Func<DateTimeOffset> clock) => request =>
    {
        // a page of another name pointing at this PC (DNS rebinding) is not this console
        var host = request.Header("host");
        if (host != $"127.0.0.1:{port}" && host != $"localhost:{port}")
        {
            return ServerResponse.Text(403, "Konzola odpovedá len na adrese 127.0.0.1.");
        }

        switch (request.Path)
        {
            case "/":
                return ServerResponse.Page("text/html; charset=utf-8", ConsoleUi.Html);
            case "/konzola.js":
                return ServerResponse.Page("text/javascript; charset=utf-8", ConsoleUi.Script);
            case "/konzola.css":
                return ServerResponse.Page("text/css; charset=utf-8", ConsoleUi.Style);
        }

        if (!request.Path.StartsWith("/api/", StringComparison.Ordinal))
        {
            return ServerResponse.Text(404, "Taká stránka v konzole nie je.");
        }

        if (request.Header(ConsoleTokenHeader.ToLowerInvariant()) is not { } given
            || !CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(given), Encoding.ASCII.GetBytes(token)))
        {
            return Answer(403, new JsonObject().Add("ok", false).Add("message", "Konzola sa otvára len adresou, ktorú vypísal príkaz konzola (spusti ho znova)."));
        }

        var now = clock();
        try
        {
            return request.Path switch
            {
                "/api/stav" => Answer(200, StateOf(server, keyFile, key, now)),
                "/api/kod" => Answer(200, NewCode(server, keyPath, keyFile, key, request.Body ?? [], now)),
                "/api/odobrat" => Answer(200, Remove(server, key, request.Body ?? [], now)),
                _ => Answer(404, new JsonObject().Add("ok", false).Add("message", "Neznáma otázka konzoly.")),
            };
        }
        catch (Exception e) when (e is InvalidDataException or JsonError or IOException)
        {
            return Answer(200, new JsonObject().Add("ok", false).Add("message", e.Message));
        }
    };

    private static ServerResponse Answer(int status, JsonObject o) => new() { Status = status, Body = Utf8NoBom.GetBytes(JsonText.Canonical(o)) };

    /// <summary>The classroom as the page shows it: every PC with its mark, what makes it so and its last report.</summary>
    private static JsonObject StateOf((Uri Address, string Pin) server, ConsoleKeyFile keyFile, SigningKey key, DateTimeOffset now)
    {
        var (ok, message, centreNow, pcs) = FetchPcs(server, key, now);
        var result = new JsonObject()
            .Add("ok", ok)
            .Add("message", message)
            .Add("console", keyFile.Name)
            .Add("centre", server.Address.ToString())
            .Add("now", centreNow.ToString("O", CultureInfo.InvariantCulture));
        var list = new JsonArray();
        foreach (var pc in pcs.OrderBy(p => p.Record.Group, StringComparer.Ordinal).ThenBy(p => p.Record.Pc.Name, StringComparer.OrdinalIgnoreCase))
        {
            var (mark, parts) = pc.State(centreNow);
            var r = pc.Record;
            var item = new JsonObject()
                .Add("id", r.Id)
                .Add("name", r.Pc.Name)
                .Add("group", r.Group)
                .Add("mark", mark.ToString())
                .Add("parts", JsonArray.Of(parts, p => p))
                .Add("hardware", r.Pc.Hardware)
                .Add("windows", r.Pc.Windows)
                .Add("agent", r.Pc.Agent)
                .Add("registered", r.RegisteredUtc.ToString("O", CultureInfo.InvariantCulture));
            if (pc.Report is { } report && pc.Received is { } received)
            {
                var journal = new JsonArray();
                foreach (var run in RunReport.ReadJournal(string.Join("\n", report.Journal)).Where(x => x.Recipe != "(meranie)").Reverse())
                {
                    journal.Add(new JsonObject()
                        .Add("time", run.StartedUtc.ToString("O", CultureInfo.InvariantCulture))
                        .Add("recipe", run.Recipe)
                        .Add("failed", run.Failed)
                        .Add("summary", run.Failed == 0 ? $"zmien {run.Results.Count(x => x.Kind != ChangeKind.None)}, v poriadku" : $"chyby: {run.Failed}")
                        .Add("lines", JsonArray.Of(run.Results.Where(x => !x.Ok).Select(x => $"{x.Title}: {x.Message}").Concat(run.Skipped).Take(12), l => l))
                        .Add("note", run.Results.Count == 1 && run.Results[0].Ok ? run.Results[0].Message : ""));
                }

                var measurements = new JsonArray();
                foreach (var m in Enumerable.Reverse(RunReport.ReadJournal(string.Join("\n", report.Measurements))))
                {
                    measurements.Add(new JsonObject()
                        .Add("time", m.StartedUtc.ToString("O", CultureInfo.InvariantCulture))
                        .Add("lines", JsonArray.Of(m.Results.Take(8).Select(x => $"{x.Title}: {x.Message}"), l => l)));
                }

                item.Add("received", received.ToString("O", CultureInfo.InvariantCulture))
                    .Add("ago", Ago(centreNow - received))
                    .Add("profile", report.Profile)
                    .Add("recipe", report.Recipe)
                    .Add("uptime", Ago(TimeSpan.FromSeconds(report.UptimeSeconds)))
                    .Add("freeDisk", PcSize(report.FreeDiskBytes))
                    .Add("totalDisk", PcSize(report.TotalDiskBytes))
                    .Add("diskUsed", report.TotalDiskBytes > 0 ? (int)(100 - report.FreeDiskBytes * 100 / report.TotalDiskBytes) : 0)
                    .Add("devicesWithoutDriver", report.DevicesWithoutDriver)
                    .Add("trezor", report.Trezor)
                    .Add("journal", journal)
                    .Add("measurements", measurements);
            }

            list.Add(item);
        }

        return result.Add("pcs", list);
    }

    private static JsonObject NewCode((Uri Address, string Pin) server, string keyPath, ConsoleKeyFile keyFile, SigningKey key, byte[] body, DateTimeOffset now)
    {
        var f = new JsonFields(JsonReader.Parse(Encoding.UTF8.GetString(body), JsonSyntax.Strict), "kód");
        var group = f.String("group").Trim();
        var days = f.Int("days", 1);
        var count = f.Int("count", 30);
        f.End();
        if (days is < 1 or > 7 || count is < 1 or > RegistrationGrant.MostUses)
        {
            return new JsonObject().Add("ok", false).Add("message", $"Kód platí 1 až 7 dní a pre 1 až {RegistrationGrant.MostUses} PC.");
        }

        var warnings = new List<string>();
        var (ok, message, code, grant) = IssueCode(server, keyPath, keyFile, key, group, days, count, now, warnings.Add);
        if (!ok || code is null || grant is null)
        {
            return new JsonObject().Add("ok", false).Add("message", "Centrum kód neprijalo: " + message);
        }

        return new JsonObject()
            .Add("ok", true)
            .Add("message", string.Join(" ", warnings))
            .Add("code", code)
            .Add("group", group)
            .Add("until", grant.NotAfterUtc.ToString("O", CultureInfo.InvariantCulture))
            .Add("count", count)
            .Add("consoles", JsonArray.Of(grant.Consoles().Select(c => c.Name), n => n))
            .Add("command", RegisterCommand(server, code));
    }

    private static JsonObject Remove((Uri Address, string Pin) server, SigningKey key, byte[] body, DateTimeOffset now)
    {
        var f = new JsonFields(JsonReader.Parse(Encoding.UTF8.GetString(body), JsonSyntax.Strict), "odobratie");
        var id = f.String("id");
        f.End();
        if (!PcRules.Id(id))
        {
            return new JsonObject().Add("ok", false).Add("message", "Neznáme PC.");
        }

        var (ok, message) = RemovePc(server, key, id, now);
        return new JsonObject().Add("ok", ok).Add("message", message);
    }

    private static void OpenBrowser(string address)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                Process.Start(new ProcessStartInfo(address) { UseShellExecute = true })?.Dispose();
            }
            else if (OperatingSystem.IsLinux())
            {
                Process.Start(new ProcessStartInfo("xdg-open", [address]) { UseShellExecute = false })?.Dispose();
            }
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // no browser to open: the address is written out
        }
    }
}
