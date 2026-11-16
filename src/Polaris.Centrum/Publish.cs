using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Polaris.Net;
using Polaris.Recipes;
using Polaris.Web;

namespace Polaris.Centrum;

/// <summary>Publishing for the PCs and the centre's read-only web server. A console signs the plans with its own key
/// (the key never lives on the server); the server only hands out what was published, and the PCs check every
/// signature again against the consoles they trust.</summary>
public static partial class Commands
{
    public const int DefaultPort = 8443;
    public const string PublicationFolder = "zverejnene";
    private static readonly UTF8Encoding Utf8NoBom = new(false);

    /// <summary>zverejnit KLUC: the plan of every group, signed, and the recipes they need.</summary>
    private static int Publish(Classroom data, ClassroomStore store, List<string> rest, TextWriter o, DateTimeOffset utcNow, Func<string, string?> secret)
    {
        Need(rest, 1, "zverejnit KLUC");
        if (data.Groups.Count == 0)
        {
            throw new UsageException("Nie je žiadna skupina PC – nie je čo zverejniť.");
        }

        var fingerprints = new Dictionary<string, string>(StringComparer.Ordinal);
        var files = new Dictionary<string, string>(StringComparer.Ordinal);
        var problems = new List<string>();
        foreach (var p in data.Profiles)
        {
            var (_, found) = RecipeState(store, p);
            problems.AddRange(found);
            var file = store.RecipeFile(p);
            if (found.Count > 0 || !File.Exists(file))
            {
                continue;
            }

            var recipe = RecipeText.Load(file);
            var signatureFile = RecipeSigning.SignatureFileOf(file);
            if (!File.Exists(signatureFile))
            {
                problems.Add($"Profil {p.Name}: recept nie je podpísaný (podpisat {p.File} KLUC) – PC ho neprijmú.");
                continue;
            }

            fingerprints[p.Name] = RecipeText.Fingerprint(recipe);
            files[p.Name] = file;
        }

        problems.AddRange(data.Timetable.Problems(data.Profiles.Select(p => p.Name).ToHashSet(), data.Groups.Select(g => g.Name).ToHashSet()));
        if (problems.Count > 0)
        {
            o.WriteLine("Nezverejnené – najprv oprav:");
            foreach (var p in problems)
            {
                o.WriteLine("  – " + p);
            }

            return 1;
        }

        var keyFile = RecipeSigning.KeyFromJson(File.ReadAllText(rest[0]));
        using var key = RecipeSigning.OpenPrivate(keyFile, secret($"Heslo ku kľúču {keyFile.Name}: ") ?? "");
        var root = Path.Combine(store.Folder, PublicationFolder);
        Directory.CreateDirectory(Path.Combine(root, "recepty"));
        Directory.CreateDirectory(Path.Combine(root, "skupiny"));

        // recipes first, plans last: a PC never sees a plan whose recipe is not there yet
        foreach (var (name, file) in files)
        {
            var fingerprint = fingerprints[name];
            WriteSafely(Path.Combine(root, "recepty", fingerprint + ".json"), File.ReadAllBytes(file));
            WriteSafely(Path.Combine(root, "recepty", fingerprint + ".podpis"), File.ReadAllBytes(RecipeSigning.SignatureFileOf(file)));
        }

        foreach (var group in data.Groups.OrderBy(g => g.Name, StringComparer.Ordinal))
        {
            var plan = GroupPlan.For(data, group.Name, fingerprints, utcNow);
            var signed = PlanSigning.Sign(plan, key, keyFile.Name);
            WriteSafely(Path.Combine(root, "skupiny", group.Name + ".json"), Utf8NoBom.GetBytes(PlanSigning.ToJson(signed)));
            o.WriteLine($"Skupina {group.Name}: plán {plan.Sequence} – profily {string.Join(", ", plan.Profiles.Select(p => p.Name))}.");
        }

        o.WriteLine($"Zverejnené v {root}, podpísala konzola {keyFile.Name}. PC si plán prevezmú pri najbližšom pohľade (do 5 minút).");
        return 0;
    }

    /// <summary>server certifikat NÁZOV… | server [--port N] [--zasoba CESTA]</summary>
    private static int Server(ClassroomStore store, List<string> rest, TextWriter o, DateTimeOffset utcNow, CancellationToken stop)
    {
        var certificateFile = Path.Combine(store.Folder, "server.pem");
        var keyFile = Path.Combine(store.Folder, "server.key");
        if (rest.Count > 0 && rest[0] == "certifikat")
        {
            if (rest.Count < 2)
            {
                throw new UsageException("Použitie: server certifikat NÁZOV-ALEBO-IP… (ako PC centrum oslovujú, napr. server certifikat polaris-server 192.168.1.10)");
            }

            if (File.Exists(certificateFile) || File.Exists(keyFile))
            {
                throw new UsageException($"Certifikát už existuje ({certificateFile}) – nový by zneplatnil pripnutie na všetkých PC. Ak to chceš, starý najprv presuň.");
            }

            using var created = Certificates.Create(rest.Skip(1), utcNow);
            Certificates.Save(created, certificateFile, keyFile);
            o.WriteLine($"Certifikát centra: {certificateFile} (súkromný kľúč {keyFile}, čitateľný len pre vlastníka).");
            o.WriteLine($"Odtlačok na PC (polaris-agent centrum https://{rest[1]}:{DefaultPort} ODTLAČOK SKUPINA):");
            o.WriteLine("  " + PinnedClient.Pin(created));
            return 0;
        }

        var port = DefaultPort;
        string? supply = null;
        for (var i = 0; i < rest.Count; i++)
        {
            switch (rest[i])
            {
                case "--port" when i + 1 < rest.Count && int.TryParse(rest[i + 1], out var p) && p is > 0 and < 65536:
                    port = p;
                    i++;
                    break;
                case "--zasoba" when i + 1 < rest.Count:
                    supply = rest[++i];
                    break;
                default:
                    throw new UsageException("Použitie: server [--port ČÍSLO] [--zasoba CESTA] | server certifikat NÁZOV…");
            }
        }

        if (!File.Exists(certificateFile))
        {
            throw new UsageException("Centrum nemá certifikát – najprv: server certifikat NÁZOV-SERVERA IP-ADRESA");
        }

        using var certificate = Certificates.Load(certificateFile, keyFile);
        var server = new WebServer(new IPEndPoint(IPAddress.IPv6Any, port), certificate, Routes(Path.Combine(store.Folder, PublicationFolder), supply), line => o.WriteLine(line));
        o.WriteLine($"POLARIS centrum počúva na porte {port} (HTTPS, len na čítanie). Odtlačok: {PinnedClient.Pin(certificate)}");
        server.RunAsync(stop).GetAwaiter().GetResult();
        o.WriteLine("Centrum zastavené.");
        return 0;
    }

    /// <summary>What the server hands out – nothing else exists for it.</summary>
    public static Func<ServerRequest, ServerResponse> Routes(string publication, string? supply) => request =>
    {
        var parts = request.Path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        switch (parts)
        {
            case ["v1", "stav"]:
                return ServerResponse.Json($$"""{"polaris":"centrum","verzia":"{{Polaris.Core.Product.Version}}"}""");
            case ["v1", "skupiny", var file] when file.EndsWith(".json", StringComparison.Ordinal) && GroupName().IsMatch(file[..^5]):
                return Published(Path.Combine(publication, "skupiny", file));
            case ["v1", "recepty", var file] when RecipeFile().IsMatch(file):
                return Published(Path.Combine(publication, "recepty", file));
            case ["v1", "baliky", var id, var file] when supply is not null && new PackageStore(supply).Exists(id):
                var package = new PackageStore(supply).Load(id);
                return file == PackageFormat.FileName
                    ? Published(Path.Combine(new PackageStore(supply).FolderOf(id), PackageFormat.FileName))
                    : file == package.File ? ServerResponse.File(new PackageStore(supply).InstallerOf(package), "\"" + package.Sha256 + "\"") : NotFound();
            default:
                return NotFound();
        }
    };

    private static ServerResponse Published(string file)
    {
        if (!File.Exists(file))
        {
            return NotFound();
        }

        var bytes = File.ReadAllBytes(file);
        return new ServerResponse { Body = bytes, ETag = "\"" + Convert.ToHexStringLower(SHA256.HashData(bytes))[..32] + "\"" };
    }

    private static ServerResponse NotFound() => ServerResponse.Text(404, "Nič také centrum nemá.");

    /// <summary>A new file appears whole: written beside and moved over the old one.</summary>
    private static void WriteSafely(string path, byte[] bytes)
    {
        var temp = path + ".novy";
        File.WriteAllBytes(temp, bytes);
        File.Move(temp, path, overwrite: true);
    }

    [GeneratedRegex("^[A-Za-z0-9_-]{1,40}$")]
    private static partial Regex GroupName();

    [GeneratedRegex("^[0-9a-f]{64}\\.(json|podpis)$")]
    private static partial Regex RecipeFile();
}
