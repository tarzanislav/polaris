using System.Globalization;
using System.Text;
using Polaris.Json;
using Polaris.Net;
using Polaris.Web;

namespace Polaris.Centrum;

/// <summary>Updates through the console (decided 25. 11. 2026): the console finds a new release on GitHub and shows it;
/// after a click it downloads the files, checks them against the release, signs each with the console's key and hands it
/// to the centre. PCs and servers take only what a console they trust signed. The console updates itself last.</summary>
public static partial class Commands
{
    /// <summary>Hands one file to the centre as a new version of the product, signed by this console.</summary>
    public static (bool Ok, string Message) HandUpdate((Uri Address, string Pin) server, SigningKey key, string console, string product, string version, byte[] file, DateTimeOffset now)
    {
        var manifest = UpdateSigning.Sign(product, version, file, key, console, now);
        var headers = UploadSigning.Headers(file, UpdateSigning.UploadPath, server.Pin, key, now);
        headers[UpdateSigning.ManifestHeader] = Convert.ToBase64String(Encoding.UTF8.GetBytes(manifest.ToJson()));
        var answer = PinnedClient.Post(new Uri(server.Address, UpdateSigning.UploadPath.TrimStart('/')), server.Pin, file, headers, 64 * 1024, TimeSpan.FromMinutes(3));
        if (answer.Status == 0)
        {
            return (false, $"{UpdateProducts.Title(product)}: centrum nedostupné ({answer.Error})");
        }

        var lines = Lines(answer.Body).Skip(1).ToList();
        return (answer.Ok, lines.Count > 0 ? string.Join(" ", lines) : $"{UpdateProducts.Title(product)}: centrum odpovedalo {answer.Status}");
    }

    /// <summary>The version of each product the centre passes on now ("" when none), and the centre's own version.</summary>
    public static Dictionary<string, string> CentreVersions((Uri Address, string Pin) server)
    {
        var versions = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var product in UpdateProducts.All)
        {
            var answer = PinnedClient.Get(new Uri(server.Address, UpdateSigning.ManifestPath(product).TrimStart('/')), server.Pin, null, 64 * 1024, TimeSpan.FromSeconds(15));
            try
            {
                versions[product] = answer.Ok ? UpdateManifest.FromJson(Encoding.UTF8.GetString(answer.Body)).Version : "";
            }
            catch (InvalidDataException)
            {
                versions[product] = "";
            }
        }

        var state = PinnedClient.Get(new Uri(server.Address, "v1/stav"), server.Pin, null, 64 * 1024, TimeSpan.FromSeconds(15));
        try
        {
            versions["centrum"] = state.Ok ? new JsonFields(JsonReader.Parse(Encoding.UTF8.GetString(state.Body)), "stav").String("verzia") : "";
        }
        catch (JsonError)
        {
            versions["centrum"] = "";
        }

        return versions;
    }
}

/// <summary>The update as the console window shows it: what GitHub has, what the centre passes on, and the work of the
/// click (downloads, checks, signatures, hand-overs) running in the background.</summary>
public sealed class UpdateJob(bool canRestart, Func<(Release? Release, string Problem)>? latest = null)
{
    public static readonly TimeSpan LookEvery = TimeSpan.FromHours(1);
    private readonly Lock _gate = new();
    private readonly Func<(Release? Release, string Problem)> _latest = latest ?? GitHubReleases.Latest;
    private Release? _release;
    private string _problem = "";
    private DateTimeOffset _checked = DateTimeOffset.MinValue;
    private Dictionary<string, string> _centre = new(StringComparer.Ordinal);
    private readonly List<string> _lines = [];
    private bool _running;

    /// <summary>The new console to start when the work is done (the program on the desktop only), or null.</summary>
    public string? RestartWith { get; private set; }

    public bool Done { get; private set; }

    /// <summary>Looks at GitHub (at most once an hour unless forced) and at the centre.</summary>
    public void Look((Uri Address, string Pin)? server, bool force)
    {
        if (!force && DateTimeOffset.UtcNow - _checked < LookEvery)
        {
            return;
        }

        var (release, problem) = _latest();
        var centre = server is { } s ? Commands.CentreVersions(s) : new Dictionary<string, string>(StringComparer.Ordinal);
        lock (_gate)
        {
            (_release, _problem, _checked, _centre) = (release ?? _release, problem, DateTimeOffset.UtcNow, centre);
        }
    }

    /// <summary>What can be updated: the console itself, and each product the release has and the centre does not pass on yet.</summary>
    public List<string> Pending()
    {
        lock (_gate)
        {
            if (_release is not { } r)
            {
                return [];
            }

            var list = UpdateProducts.All
                .Where(p => r.Asset(UpdateProducts.ReleaseFile(p)) is not null && UpdateSigning.Newer(r.Version, _centre.GetValueOrDefault(p) is { Length: > 0 } v ? v : "0"))
                .Select(UpdateProducts.Title)
                .ToList();
            if (OperatingSystem.IsWindows() && canRestart && r.Asset(UpdateProducts.ConsoleFile) is not null && UpdateSigning.Newer(r.Version, Polaris.Core.Product.Version))
            {
                list.Insert(0, "táto konzola");
            }

            return list;
        }
    }

    public JsonObject Status()
    {
        var pending = Pending();
        lock (_gate)
        {
            return new JsonObject()
                .Add("ok", true)
                .Add("current", Polaris.Core.Product.Version)
                .Add("latest", _release?.Version ?? "")
                .Add("pending", JsonArray.Of(pending, p => p))
                .Add("problem", _problem)
                .Add("checked", _checked == DateTimeOffset.MinValue ? "" : _checked.ToString("O", CultureInfo.InvariantCulture))
                .Add("client", _centre.GetValueOrDefault(UpdateProducts.Client) ?? "")
                .Add("centre", _centre.GetValueOrDefault("centrum") ?? "")
                .Add("running", _running)
                .Add("done", Done)
                .Add("restart", RestartWith is not null)
                .Add("lines", JsonArray.Of(_lines.ToList(), l => l));
        }
    }

    /// <summary>The click: the work runs in the background; false when it already runs.</summary>
    public bool Start((Uri Address, string Pin) server, SigningKey key, string console)
    {
        lock (_gate)
        {
            if (_running)
            {
                return false;
            }

            (_running, Done) = (true, false);
            _lines.Clear();
        }

        new Thread(() => Run(server, key, console)) { IsBackground = true, Name = "Aktualizácia" }.Start();
        return true;
    }

    private void Say(string line)
    {
        lock (_gate)
        {
            _lines.Add(line);
        }
    }

    private void Run((Uri Address, string Pin) server, SigningKey key, string console)
    {
        try
        {
            Say("Pýtam sa GitHubu na najnovšie vydanie…");
            Look(server, force: true);
            if (_release is not { } release)
            {
                Say("Vydanie sa nedá zistiť: " + _problem);
                return;
            }

            foreach (var product in UpdateProducts.All)
            {
                if (release.Asset(UpdateProducts.ReleaseFile(product)) is not { } asset)
                {
                    continue;
                }

                var onCentre = _centre.GetValueOrDefault(product) ?? "";
                if (onCentre.Length > 0 && !UpdateSigning.Newer(release.Version, onCentre))
                {
                    Say($"{UpdateProducts.Title(product)}: centrum už rozosiela {onCentre}.");
                    continue;
                }

                Say($"{UpdateProducts.Title(product)}: sťahujem {asset.Name} ({asset.Size / 1024 / 1024.0:0.0} MB) a kontrolujem odtlačok…");
                var bytes = GitHubReleases.Download(release, asset);
                var (ok, message) = Commands.HandUpdate(server, key, console, product, release.Version, bytes, DateTimeOffset.UtcNow);
                Say((ok ? "✓ " : "✗ ") + message);
            }

            if (OperatingSystem.IsWindows() && canRestart && UpdateSigning.Newer(release.Version, Polaris.Core.Product.Version)
                && release.Asset(UpdateProducts.ConsoleFile) is { } own)
            {
                Say($"Táto konzola: sťahujem {release.Version}…");
                var bytes = GitHubReleases.Download(release, own);
                var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "POLARIS", "konzola", "aktualizacia");
                Directory.CreateDirectory(folder);
                var file = Path.Combine(folder, UpdateProducts.ConsoleFile);
                File.WriteAllBytes(file, bytes);
                RestartWith = file;
                Say("✓ Konzola sa aktualizuje: okno sa o chvíľu zavrie a otvorí znova s novou verziou.");
            }

            Look(server, force: true);
            Say("Hotovo. PC si novú verziu klienta vezmú do 5 minút, server do 15 minút.");
        }
        catch (Exception e) when (e is InvalidDataException or IOException or HttpRequestException or TaskCanceledException or UnauthorizedAccessException)
        {
            Say("✗ " + e.Message);
        }
        finally
        {
            lock (_gate)
            {
                (_running, Done) = (false, true);
            }
        }
    }
}

public static partial class Commands
{
    private const string UpdateUsage = "aktualizacia odovzdat PRODUKT VERZIA SÚBOR KLUC --server https://SERVER:8443 ODTLAČOK | aktualizacia over POPIS --konzoly PRIEČINOK [--produkt P]";

    private static int UpdateCommand(List<string> rest, TextWriter o, DateTimeOffset utcNow, Func<string, string?> secret)
    {
        if (rest.Count > 0 && rest[0] == "over")
        {
            // run by the updater of the Debian server as root: only the consoles in a folder only root can change count
            var at = rest.IndexOf("--konzoly");
            if (at < 0 || at + 1 >= rest.Count)
            {
                throw new UsageException(UpdateUsage);
            }

            var folder = rest[at + 1];
            rest.RemoveRange(at, 2);
            string? product = null;
            var p = rest.IndexOf("--produkt");
            if (p >= 0 && p + 1 < rest.Count)
            {
                product = rest[p + 1];
                rest.RemoveRange(p, 2);
            }

            Need(rest, 2, UpdateUsage);
            var manifest = UpdateManifest.FromJson(File.ReadAllText(rest[1]));
            if (product is not null && manifest.Product != product)
            {
                o.WriteLine($"Popis je pre {manifest.Product}, nie {product}.");
                return 1;
            }

            var check = UpdateSigning.Check(manifest, Trusted(folder));
            if (check.State != SignatureState.Valid)
            {
                o.WriteLine(check.Message);
                return 1;
            }

            o.WriteLine($"{manifest.Version} {manifest.Sha256}");
            return 0;
        }

        if (rest.Count > 0 && rest[0] == "odovzdat")
        {
            var server = ServerOption(rest, UpdateUsage);
            Need(rest, 5, UpdateUsage);
            var (product, version, file) = (rest[1], rest[2], rest[3]);
            if (!UpdateProducts.All.Contains(product))
            {
                throw new UsageException($"Produkt je jeden z: {string.Join(", ", UpdateProducts.All)}.");
            }

            var keyFile = RecipeSigning.KeyFromJson(File.ReadAllText(rest[4]));
            using var key = RecipeSigning.OpenPrivate(keyFile, secret($"Heslo ku kľúču {keyFile.Name}: ") ?? "");
            var (ok, message) = HandUpdate(server, key, keyFile.Name, product, version, File.ReadAllBytes(file), utcNow);
            o.WriteLine(message);
            return ok ? 0 : 1;
        }

        throw new UsageException(UpdateUsage);
    }
}
