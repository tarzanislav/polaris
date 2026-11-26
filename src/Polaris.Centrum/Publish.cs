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
        (Uri Address, string Pin)? target = null;
        var at = rest.IndexOf("--server");
        if (at >= 0)
        {
            if (at + 2 >= rest.Count || !Uri.TryCreate(rest[at + 1], UriKind.Absolute, out var address) || address.Scheme != Uri.UriSchemeHttps
                || rest[at + 2].Length != 64 || !rest[at + 2].All(char.IsAsciiHexDigit))
            {
                throw new UsageException("Použitie: zverejnit KLUC --server https://SERVER:8443 ODTLAČOK");
            }

            target = (address, rest[at + 2].ToLowerInvariant());
            rest.RemoveRange(at, 3);
        }

        Need(rest, 1, "zverejnit KLUC [--server https://SERVER:8443 ODTLAČOK]");
        if (data.Groups.Count == 0)
        {
            throw new UsageException("Nie je žiadna skupina PC – nie je čo zverejniť.");
        }

        if (PublishProblems(data, store, out _) is { Count: > 0 } problems)
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
        var lines = new List<string>();
        var ok = PublishWith(data, store, key, keyFile.Name, target, utcNow, lines);
        foreach (var line in lines)
        {
            o.WriteLine(line);
        }

        return ok ? 0 : 1;
    }

    /// <summary>What stops a publication: unreadable, unchecked or unsigned recipes, a timetable that names unknown
    /// groups or profiles. The fingerprints and files of the good ones come out.</summary>
    internal static List<string> PublishProblems(Classroom data, ClassroomStore store, out Dictionary<string, (string Fingerprint, string File)> recipes)
    {
        recipes = new Dictionary<string, (string, string)>(StringComparer.Ordinal);
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

            if (!File.Exists(RecipeSigning.SignatureFileOf(file)))
            {
                problems.Add($"Profil {p.Name}: recept nie je podpísaný (podpisat {p.File} KLUC) – PC ho neprijmú.");
                continue;
            }

            recipes[p.Name] = (RecipeText.Fingerprint(RecipeText.Load(file)), file);
        }

        foreach (var g in data.Groups.Where(g => data.FindProfile(g.DefaultProfile) is null))
        {
            problems.Add($"Skupina {g.Name}: profil {g.DefaultProfile} neexistuje.");
        }

        problems.AddRange(data.Timetable.Problems(data.Profiles.Select(p => p.Name).ToHashSet(), data.Groups.Select(g => g.Name).ToHashSet()));
        return problems;
    }

    /// <summary>The publication: the recipes, then the plan of every group signed by this console, into the folder of
    /// the settings – and, with a centre, handed to it (an envelope signed for that centre). False when something failed.</summary>
    internal static bool PublishWith(Classroom data, ClassroomStore store, SigningKey key, string console, (Uri Address, string Pin)? target, DateTimeOffset utcNow, List<string> lines)
    {
        if (PublishProblems(data, store, out var recipes) is { Count: > 0 } problems)
        {
            lines.Add("Nezverejnené – najprv oprav:");
            lines.AddRange(problems.Select(p => "– " + p));
            return false;
        }

        var root = Path.Combine(store.Folder, PublicationFolder);
        Directory.CreateDirectory(Path.Combine(root, "recepty"));
        Directory.CreateDirectory(Path.Combine(root, "skupiny"));

        // recipes first, plans last: a PC never sees a plan whose recipe is not there yet
        foreach (var (_, (fingerprint, file)) in recipes)
        {
            WriteSafely(Path.Combine(root, "recepty", fingerprint + ".json"), File.ReadAllBytes(file));
            WriteSafely(Path.Combine(root, "recepty", fingerprint + ".podpis"), File.ReadAllBytes(RecipeSigning.SignatureFileOf(file)));
        }

        var fingerprints = recipes.ToDictionary(r => r.Key, r => r.Value.Fingerprint, StringComparer.Ordinal);
        foreach (var group in data.Groups.OrderBy(g => g.Name, StringComparer.Ordinal))
        {
            var plan = GroupPlan.For(data, group.Name, fingerprints, utcNow);
            var signed = PlanSigning.Sign(plan, key, console);
            WriteSafely(Path.Combine(root, "skupiny", group.Name + ".json"), Utf8NoBom.GetBytes(PlanSigning.ToJson(signed)));
            lines.Add($"Skupina {group.Name}: plán {plan.Sequence} – profily {string.Join(", ", plan.Profiles.Select(p => p.Name))}.");
        }

        lines.Add($"Zverejnené v {root}, podpísala konzola {console}.");
        if (target is not { } server)
        {
            lines.Add("PC si plán prevezmú od centra, ktoré tento priečinok odovzdáva, pri najbližšom pohľade (do 5 minút).");
            return true;
        }

        // the same publication handed to the centre on the server, signed by this console for that centre only
        var bundle = new PublicationFolder(root).Bundle(data.Groups.Select(g => g.Name));
        var body = Utf8NoBom.GetBytes(bundle.ToJson());
        var headers = UploadSigning.Headers(body, UploadPath, server.Pin, key, utcNow);
        var answer = PinnedClient.Post(new Uri(server.Address, UploadPath.TrimStart('/')), server.Pin, body, headers, 1024 * 1024, TimeSpan.FromMinutes(2));
        if (answer.Status == 0)
        {
            lines.Add("Odovzdanie na centrum zlyhalo: " + answer.Error + ".");
            return false;
        }

        lines.Add($"Centrum {server.Address}:");
        lines.AddRange(Lines(answer.Body).Select(l => "  " + l));
        return answer.Ok;
    }

    public const string UploadPath = "/v1/zverejnenie";

    private static IEnumerable<string> Lines(byte[] body)
    {
        try
        {
            var f = new Polaris.Json.JsonFields(Polaris.Json.JsonReader.Parse(Encoding.UTF8.GetString(body)), "odpoveď centra");
            return f.Strings("lines");
        }
        catch (Exception e) when (e is FormatException or InvalidDataException)
        {
            return [Encoding.UTF8.GetString(body).Trim()];
        }
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

        if (rest.Count > 0 && rest[0] == "odtlacok")
        {
            if (!File.Exists(certificateFile))
            {
                throw new UsageException("Centrum nemá certifikát – najprv: server certifikat NÁZOV-SERVERA IP-ADRESA");
            }

            using var existing = Certificates.Load(certificateFile, keyFile);
            o.WriteLine(PinnedClient.Pin(existing));
            return 0;
        }

        var trustFolder = Path.Combine(store.Folder, "konzoly");
        if (rest.Count > 0 && rest[0] is "doverovat" or "konzoly")
        {
            return ServerTrust(trustFolder, rest, o);
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
                    throw new UsageException("Použitie: server [--port ČÍSLO] [--zasoba CESTA] | server certifikat NÁZOV… | server doverovat KONZOLA.verejny.json | server konzoly");
            }
        }

        if (!File.Exists(certificateFile))
        {
            throw new UsageException("Centrum nemá certifikát – najprv: server certifikat NÁZOV-SERVERA IP-ADRESA");
        }

        using var certificate = Certificates.Load(certificateFile, keyFile);
        var publication = Path.Combine(store.Folder, PublicationFolder);
        var pin = PinnedClient.Pin(certificate);
        var upload = Upload(publication, pin, () => Trusted(trustFolder), () => DateTimeOffset.UtcNow);
        var pcs = PcRoutes(new PcRegistry(Path.Combine(store.Folder, "pc")), pin, () => Trusted(trustFolder), () => DateTimeOffset.UtcNow);
        var updates = UpdateUpload(publication, pin, () => Trusted(trustFolder), () => DateTimeOffset.UtcNow);
        var server = new WebServer(new IPEndPoint(IPAddress.IPv6Any, port), certificate, Routes(publication, supply, upload, pcs, updates), line => o.WriteLine(line), BodyPaths.Contains);
        o.WriteLine($"POLARIS centrum počúva na porte {port} (HTTPS). Odtlačok: {pin}");
        server.RunAsync(stop).GetAwaiter().GetResult();
        o.WriteLine("Centrum zastavené.");
        return 0;
    }

    /// <summary>What the server hands out – nothing else exists for it.</summary>
    public static Func<ServerRequest, ServerResponse> Routes(string publication, string? supply, Func<ServerRequest, ServerResponse>? upload = null, Func<ServerRequest, ServerResponse>? pcs = null, Func<ServerRequest, ServerResponse>? updates = null) => request =>
    {
        if (request.Method == "POST")
        {
            if (request.Path == UploadPath && upload is not null)
            {
                return upload(request);
            }

            if (request.Path == UpdateSigning.UploadPath && updates is not null)
            {
                return updates(request);
            }

            if (request.Path != UploadPath && BodyPaths.Contains(request.Path) && pcs is not null)
            {
                try
                {
                    return pcs(request);
                }
                catch (InvalidDataException e)
                {
                    return Reply(400, false, e.Message);
                }
                catch (Polaris.Json.JsonError e)
                {
                    return Reply(400, false, e.Message);
                }
            }

            return ServerResponse.Text(405, "Sem sa nič neodovzdáva.");
        }

        var parts = request.Path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        switch (parts)
        {
            case ["v1", "stav"]:
                return ServerResponse.Json($$"""{"polaris":"centrum","verzia":"{{Polaris.Core.Product.Version}}"}""");
            case ["v1", "skupiny", var file] when file.EndsWith(".json", StringComparison.Ordinal) && GroupName().IsMatch(file[..^5]):
                return Published(Path.Combine(publication, "skupiny", file));
            case ["v1", "recepty", var file] when RecipeFile().IsMatch(file):
                return Published(Path.Combine(publication, "recepty", file));
            case ["v1", "aktualizacie", var file] when file.EndsWith(".json", StringComparison.Ordinal) && UpdateProducts.All.Contains(file[..^5]):
                return Published(Path.Combine(publication, "aktualizacie", file));
            case ["v1", "aktualizacie", var file] when UpdateFile().IsMatch(file) && File.Exists(Path.Combine(publication, "aktualizacie", file)):
                return ServerResponse.File(Path.Combine(publication, "aktualizacie", file), "\"" + file[..64] + "\"");
            case ["v1", "baliky", var id, var file] when supply is not null && new PackageStore(supply).Exists(id):
                var package = new PackageStore(supply).Load(id);
                return file == PackageFormat.FileName
                    ? Published(Path.Combine(new PackageStore(supply).FolderOf(id), PackageFormat.FileName))
                    : file == package.File ? ServerResponse.File(new PackageStore(supply).InstallerOf(package), "\"" + package.Sha256 + "\"") : NotFound();
            default:
                return NotFound();
        }
    };

    /// <summary>An upload from a console: the envelope says which trusted console sent it for this centre now; then every
    /// plan and recipe in it is checked on its own.</summary>
    public static Func<ServerRequest, ServerResponse> Upload(string publication, string serverPin, Func<IReadOnlyList<TrustedConsole>> trusted, Func<DateTimeOffset> clock) => request =>
    {
        var consoles = trusted();
        var body = request.Body ?? [];
        var (console, problem) = UploadSigning.Verify(request.Header, body, request.Path, serverPin, consoles, clock());
        if (console is null)
        {
            return Answer(403, false, [problem]);
        }

        PublicationBundle bundle;
        try
        {
            bundle = PublicationBundle.FromJson(Encoding.UTF8.GetString(body));
        }
        catch (InvalidDataException e)
        {
            return Answer(400, false, [e.Message]);
        }

        var (lines, refused) = new PublicationFolder(publication).Accept(bundle, consoles);
        lines.Insert(0, $"odovzdala konzola {console.Name}");
        return Answer(refused ? 409 : 200, !refused, lines);
    };

    /// <summary>A new version from a console: the envelope says which trusted console sent it for this centre now, the
    /// manifest (signed by the console on its own) names exactly these bytes.</summary>
    public static Func<ServerRequest, ServerResponse> UpdateUpload(string publication, string serverPin, Func<IReadOnlyList<TrustedConsole>> trusted, Func<DateTimeOffset> clock) => request =>
    {
        var consoles = trusted();
        var body = request.Body ?? [];
        var (console, problem) = UploadSigning.Verify(request.Header, body, request.Path, serverPin, consoles, clock());
        if (console is null)
        {
            return Answer(403, false, [problem]);
        }

        UpdateManifest manifest;
        try
        {
            manifest = UpdateManifest.FromJson(Encoding.UTF8.GetString(Convert.FromBase64String(request.Header(UpdateSigning.ManifestHeader.ToLowerInvariant()) ?? "")));
        }
        catch (Exception e) when (e is InvalidDataException or FormatException)
        {
            return Answer(400, false, ["popis aktualizácie chýba alebo je poškodený: " + e.Message]);
        }

        var (ok, message) = new UpdateFolder(Path.Combine(publication, "aktualizacie")).Accept(manifest, body, consoles);
        return Answer(ok ? 200 : 409, ok, [$"odovzdala konzola {console.Name}", message]);
    };

    private static ServerResponse Answer(int status, bool ok, List<string> lines) => new()
    {
        Status = status,
        Body = Utf8NoBom.GetBytes(Polaris.Json.JsonText.Canonical(new Polaris.Json.JsonObject()
            .Add("ok", ok)
            .Add("lines", Polaris.Json.JsonArray.Of(lines, l => l)))),
    };

    /// <summary>The consoles the centre accepts uploads from: their public key files in the folder konzoly.</summary>
    public static List<TrustedConsole> Trusted(string folder)
    {
        var list = new List<TrustedConsole>();
        if (!Directory.Exists(folder))
        {
            return list;
        }

        foreach (var file in Directory.GetFiles(folder, "*.json").Order(StringComparer.Ordinal))
        {
            try
            {
                list.Add(RecipeSigning.Trust(RecipeSigning.KeyFromJson(File.ReadAllText(file))));
            }
            catch (InvalidDataException)
            {
                // a damaged file trusts nobody
            }
        }

        return list;
    }

    private static int ServerTrust(string folder, List<string> rest, TextWriter o)
    {
        if (rest[0] == "konzoly")
        {
            var trusted = Trusted(folder);
            o.WriteLine(trusted.Count == 0 ? "Centrum neprijíma odovzdania od žiadnej konzoly." : "Centrum prijíma odovzdania od konzol:");
            foreach (var t in trusted)
            {
                o.WriteLine($"  {t.Name} ({t.Fingerprint})");
            }

            return 0;
        }

        Need(rest, 2, "server doverovat KONZOLA.verejny.json");
        var key = RecipeSigning.KeyFromJson(File.ReadAllText(rest[1]));
        if (key.EncryptedPrivateKey is not null)
        {
            throw new UsageException("Toto je súkromný kľúč konzoly – na server patrí len verejný (KONZOLA.verejny.json).");
        }

        var console = RecipeSigning.Trust(key);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, console.Fingerprint + ".json"), RecipeSigning.ToJson(key));
        o.WriteLine($"Centrum prijíma odovzdania od konzoly {console.Name} ({console.Fingerprint}).");
        return 0;
    }

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

    [GeneratedRegex("^[0-9a-f]{64}\\.bin$")]
    private static partial Regex UpdateFile();
}
