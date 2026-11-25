using System.Net;
using System.Text;
using Polaris.Agent;
using Polaris.Core;
using Polaris.Net;
using Polaris.Recipes;
using Polaris.Web;

namespace Polaris.Tests;

/// <summary>The centre and the PC over the own HTTPS: the server, the pinned client, publishing signed plans and the
/// agent taking only what a trusted console signed. All of it runs on any system (the TLS is the system's).</summary>
public static class CentreLinkChecks
{
    private static readonly DateTimeOffset T = new(2026, 11, 17, 6, 0, 0, TimeSpan.Zero); // Tuesday 7:00 in Bratislava
    private const string Password = "heslo pre skusku centra";

    /// <summary>A running server on a free port of this computer, stopped at the end.</summary>
    private sealed class Running : IDisposable
    {
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _task;

        public Running(Func<ServerRequest, ServerResponse> handler, string folder, Func<string, bool>? acceptsBody = null)
        {
            using var created = Certificates.Create(["localhost", "127.0.0.1"], T);
            var cert = Path.Combine(folder, "server.pem");
            var key = Path.Combine(folder, "server.key");
            Certificates.Save(created, cert, key);
            Certificate = Certificates.Load(cert, key);
            Pin = PinnedClient.Pin(Certificate);
            var server = new WebServer(new IPEndPoint(IPAddress.Loopback, 0), Certificate, handler, _ => { }, acceptsBody);
            var started = new TaskCompletionSource();
            _task = server.RunAsync(_stop.Token, started);
            started.Task.Wait(TimeSpan.FromSeconds(10));
            Address = new Uri($"https://127.0.0.1:{server.Port}/");
        }

        public System.Security.Cryptography.X509Certificates.X509Certificate2 Certificate { get; }

        public string Pin { get; }

        public Uri Address { get; }

        public void Dispose()
        {
            _stop.Cancel();
            _task.Wait(TimeSpan.FromSeconds(10));
            Certificate.Dispose();
        }
    }

    [Check]
    public static void TheServerAndThePinnedClient()
    {
        var dir = Directory.CreateTempSubdirectory("polaris-web-").FullName;
        try
        {
            using var server = new Running(r => r.Path == "/ahoj" ? ServerResponse.Json("{\"a\":1}", "\"v1\"") : ServerResponse.Text(404, "nie"), dir);
            var limit = TimeSpan.FromSeconds(20);
            var ok = PinnedClient.Get(new Uri(server.Address, "ahoj"), server.Pin, null, 1000, limit);
            Expect.True(ok.Ok && Encoding.UTF8.GetString(ok.Body) == "{\"a\":1}" && ok.ETag == "\"v1\"", "odpoveď: " + ok.Status + " " + ok.Error);
            Expect.True(PinnedClient.Get(new Uri(server.Address, "ahoj"), server.Pin, "\"v1\"", 1000, limit).NotModified, "nezmenené → 304");
            Expect.Equal(404, PinnedClient.Get(new Uri(server.Address, "nic"), server.Pin, null, 1000, limit).Status, "neznáma cesta");

            var stranger = PinnedClient.Get(new Uri(server.Address, "ahoj"), new string('a', 64), null, 1000, limit);
            Expect.True(!stranger.Ok && stranger.Error is not null && stranger.Body.Length == 0, "iný kľúč centra → nič: " + stranger.Error);
            Expect.True(PinnedClient.Get(new Uri(server.Address, "ahoj"), server.Pin, null, 3, limit).Error?.Contains("väčšia", StringComparison.Ordinal) == true, "príliš veľká odpoveď");
            Expect.True(PinnedClient.Get(new Uri($"http://127.0.0.1:{server.Address.Port}/ahoj"), server.Pin, null, 1000, limit).Error?.Contains("https", StringComparison.Ordinal) == true, "len https");

            Expect.Equal(405, WebServer.Parse("POST /a HTTP/1.1", IPAddress.Loopback).Refusal?.Status ?? 0, "len čítanie");
            foreach (var bad in new[] { "GET /../etc/passwd HTTP/1.1", "GET /a%2e HTTP/1.1", "GET a HTTP/1.1", "GET /a?b HTTP/1.1", "GET /a HTTP/2", "GET /a\\b HTTP/1.1" })
            {
                Expect.Equal(400, WebServer.Parse(bad, IPAddress.Loopback).Refusal?.Status ?? 0, "odmietnuté: " + bad);
            }

            Expect.True(WebServer.Parse("GET /v1/stav HTTP/1.1\r\nHost: x\r\nIf-None-Match: \"a\"", IPAddress.Loopback).Request?.Header("If-None-Match") == "\"a\"", "hlavičky");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Check]
    public static void PlansGoFromTheConsoleToThePc()
    {
        var dir = Directory.CreateTempSubdirectory("polaris-plan-").FullName;
        try
        {
            // the console: a key, two signed recipes, a classroom
            var (secret, pub) = RecipeSigning.NewConsoleKey("spravca", Password);
            var keyFile = Path.Combine(dir, "spravca.kluc");
            File.WriteAllText(keyFile, RecipeSigning.ToJson(secret));
            using var key = RecipeSigning.OpenPrivate(secret, Password);
            string Recipe(string name, int version)
            {
                var recipe = new Recipe { Name = name, Version = version, Elements = [new FeatureElement { Id = "f", Name = "TelnetClient", Present = false }] };
                var file = Path.Combine(dir, "recepty", name.ToLowerInvariant() + ".json");
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                File.WriteAllText(file, RecipeText.ToJson(recipe));
                File.WriteAllText(RecipeSigning.SignatureFileOf(file), RecipeSigning.ToJson(RecipeSigning.Sign(recipe, key, "spravca", T)));
                return file;
            }

            var settings = Path.Combine(dir, "centrum.json");
            (int Code, string Text) Centrum(string line, DateTimeOffset? at = null)
            {
                var w = new StringWriter();
                var args = new[] { "--subor", settings }.Concat(line.Split(' ', StringSplitOptions.RemoveEmptyEntries)).ToArray();
                var when = at ?? T;
                return (Polaris.Centrum.Commands.Run(args, w, when.ToLocalTime().DateTime, when, _ => Password), w.ToString());
            }

            void Ok(string line) => Expect.Equal(0, Centrum(line).Code, line);
            Ok($"profil ZAKLADNY {Recipe("ZAKLADNY", 1)}");
            Ok($"profil KANCELARSKY {Recipe("KANCELARSKY", 3)}");
            Ok("zvonenie 1 8:00 8:45");
            Ok("skupina ucebna ZAKLADNY");
            Ok("hodina ut 1 ucebna KANCELARSKY");
            var (code, text) = Centrum($"zverejnit {keyFile}");
            Expect.Equal(0, code, text);
            Expect.True(File.Exists(Path.Combine(dir, "zverejnene", "skupiny", "ucebna.json")), "plán skupiny zverejnený");

            using var server = new Running(Polaris.Centrum.Commands.Routes(Path.Combine(dir, "zverejnene"), null), dir);
            var trusted = new List<TrustedConsole> { RecipeSigning.Trust(pub) };
            var cache = Path.Combine(dir, "pc");
            var sync = new CentreSync(new CentreSettings(server.Address, server.Pin, "ucebna"), cache, () => trusted);

            var first = sync.Fetch(0);
            Expect.True(first.Changed && first.Sequence == T.ToUnixTimeMilliseconds(), "prvý plán: " + first.Message);
            var plan = sync.Plan()!;
            Expect.Equal(("KANCELARSKY", RecipeText.Fingerprint(RecipeText.Load(Path.Combine(dir, "recepty", "kancelarsky.json")))),
                CentrePolicy.Wanted(plan, new DateTime(2026, 11, 17, 8, 10, 0))!.Value, "v utorok na 1. hodine KANCELARSKY");
            Expect.Equal("ZAKLADNY", CentrePolicy.Wanted(plan, new DateTime(2026, 11, 17, 7, 0, 0))!.Value.Profile, "pred vyučovaním ZAKLADNY");
            Expect.True(sync.Recipe(plan.Find("KANCELARSKY")!.Fingerprint) is { Version: 3 }, "recept je v PC a prešiel podpisom");
            Expect.Equal(false, sync.Fetch(first.Sequence).Changed, "druhý pohľad nič nové");

            // a newer publication is taken, an older one is not
            var later = T.AddMinutes(10);
            Expect.Equal(0, Centrum($"zverejnit {keyFile}", later).Code, "nové zverejnenie");
            var second = new CentreSync(new CentreSettings(server.Address, server.Pin, "ucebna"), cache, () => trusted).Fetch(first.Sequence);
            Expect.True(second.Changed && second.Sequence == later.ToUnixTimeMilliseconds(), "novší plán: " + second.Message);
            var old = new CentreSync(new CentreSettings(server.Address, server.Pin, "ucebna"), cache, () => trusted).Fetch(second.Sequence + 1);
            Expect.True(!old.Changed && old.Message.Contains("starší", StringComparison.Ordinal), "starší plán odmietnutý: " + old.Message);

            // what the server hands out must be signed by a console the PC trusts
            var published = Path.Combine(dir, "zverejnene", "skupiny", "ucebna.json");
            var good = File.ReadAllText(published);
            File.WriteAllText(published, good.Replace("\"KANCELARSKY\"", "\"ZAKLADNY\"", StringComparison.Ordinal));
            var forged = new CentreSync(new CentreSettings(server.Address, server.Pin, "ucebna"), Path.Combine(dir, "pc2"), () => trusted).Fetch(0);
            Expect.True(!forged.Changed && forged.Message.Contains("odmietnutý", StringComparison.Ordinal), "zmenený plán: " + forged.Message);
            File.WriteAllText(published, good);

            var (_, other) = RecipeSigning.NewConsoleKey("cudzia", Password);
            var foreign = new CentreSync(new CentreSettings(server.Address, server.Pin, "ucebna"), Path.Combine(dir, "pc3"), () => [RecipeSigning.Trust(other)]).Fetch(0);
            Expect.True(!foreign.Changed && foreign.Message.Contains("nedôveruje", StringComparison.Ordinal), "cudzia konzola: " + foreign.Message);
            var none = new CentreSync(new CentreSettings(server.Address, server.Pin, "ucebna"), Path.Combine(dir, "pc4"), () => []).Fetch(0);
            Expect.True(!none.Changed && none.Message.Contains("žiadnej konzole", StringComparison.Ordinal), "bez dôvery nič: " + none.Message);
            var stranger = new CentreSync(new CentreSettings(server.Address, new string('b', 64), "ucebna"), Path.Combine(dir, "pc5"), () => trusted).Fetch(0);
            Expect.True(!stranger.Changed, "podvrhnuté centrum (iný kľúč): " + stranger.Message);

            // an unsigned recipe is not published
            File.Delete(RecipeSigning.SignatureFileOf(Path.Combine(dir, "recepty", "zakladny.json")));
            (code, text) = Centrum($"zverejnit {keyFile}");
            Expect.True(code == 1 && text.Contains("nie je podpísaný", StringComparison.Ordinal), "nepodpísaný recept: " + text);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Check]
    public static void AConsoleHandsThePublicationToTheCentre()
    {
        var dir = Directory.CreateTempSubdirectory("polaris-odovzdanie-").FullName;
        try
        {
            var (secret, pub) = RecipeSigning.NewConsoleKey("spravca", Password);
            var keyFile = Path.Combine(dir, "spravca.kluc");
            var publicFile = Path.Combine(dir, "spravca.verejny.json");
            File.WriteAllText(keyFile, RecipeSigning.ToJson(secret));
            File.WriteAllText(publicFile, RecipeSigning.ToJson(pub));
            using var key = RecipeSigning.OpenPrivate(secret, Password);
            var recipe = new Recipe { Name = "ZAKLADNY", Version = 1, Elements = [] };
            var recipeFile = Path.Combine(dir, "konzola", "zakladny.json");
            Directory.CreateDirectory(Path.GetDirectoryName(recipeFile)!);
            File.WriteAllText(recipeFile, RecipeText.ToJson(recipe));
            File.WriteAllText(RecipeSigning.SignatureFileOf(recipeFile), RecipeSigning.ToJson(RecipeSigning.Sign(recipe, key, "spravca", T)));

            (int Code, string Text) Run(string settings, params string[] words)
            {
                var w = new StringWriter();
                return (Polaris.Centrum.Commands.Run(["--subor", settings, .. words], w, T.LocalDateTime, DateTimeOffset.UtcNow, _ => Password), w.ToString());
            }

            var console = Path.Combine(dir, "konzola", "centrum.json");
            Expect.Equal(0, Run(console, "profil", "ZAKLADNY", recipeFile).Code, "profil");
            Expect.Equal(0, Run(console, "skupina", "ucebna", "ZAKLADNY").Code, "skupina");

            // the centre on the server trusts this console for uploads
            var serverSettings = Path.Combine(dir, "server", "centrum.json");
            Directory.CreateDirectory(Path.GetDirectoryName(serverSettings)!);
            Expect.Equal(0, Run(serverSettings, "server", "doverovat", publicFile).Code, "server dôveruje konzole");
            Expect.Equal(2, Run(serverSettings, "server", "doverovat", keyFile).Code, "súkromný kľúč na server nepatrí");
            var publication = Path.Combine(dir, "server", "zverejnene");
            var trustFolder = Path.Combine(dir, "server", "konzoly");
            string? pin = null;
            using var server = new Running(
                r => Polaris.Centrum.Commands.Routes(publication, null, Polaris.Centrum.Commands.Upload(publication, pin!, () => Polaris.Centrum.Commands.Trusted(trustFolder), () => DateTimeOffset.UtcNow))(r),
                Path.Combine(dir, "server"),
                p => p == Polaris.Centrum.Commands.UploadPath);
            pin = server.Pin;

            var (code, text) = Run(console, "zverejnit", keyFile, "--server", server.Address.ToString(), server.Pin);
            Expect.True(code == 0 && text.Contains("odovzdala konzola spravca", StringComparison.Ordinal) && text.Contains("plán ucebna: zverejnený", StringComparison.Ordinal), "odovzdanie: " + text);
            var sync = new CentreSync(new CentreSettings(server.Address, server.Pin, "ucebna"), Path.Combine(dir, "pc"), () => [RecipeSigning.Trust(pub)]);
            Expect.True(sync.Fetch(0).Changed, "PC si odovzdaný plán prevezme od servera");

            // the same publication again changes nothing; a console the centre does not trust is refused
            (code, text) = Run(console, "zverejnit", keyFile, "--server", server.Address.ToString(), server.Pin);
            Expect.True(code == 0, "nové zverejnenie: " + text);
            var bundle = new PublicationFolder(Path.Combine(dir, "konzola", "zverejnene")).Bundle(["ucebna"]);
            var body = Encoding.UTF8.GetBytes(bundle.ToJson());
            var old = new PublicationFolder(publication).Accept(bundle with { Plans = [bundle.Plans[0] with { }] }, [RecipeSigning.Trust(pub)]);
            Expect.True(old.Lines.Any(l => l.Contains("ponechaný", StringComparison.Ordinal)), "rovnaký plán druhýkrát nič nemení");

            var (otherSecret, _) = RecipeSigning.NewConsoleKey("cudzia", Password);
            using var otherKey = RecipeSigning.OpenPrivate(otherSecret, Password);
            var stranger = PinnedClient.Post(new Uri(server.Address, "v1/zverejnenie"), server.Pin, body, UploadSigning.Headers(body, Polaris.Centrum.Commands.UploadPath, server.Pin, otherKey, DateTimeOffset.UtcNow), 100_000, TimeSpan.FromSeconds(20));
            Expect.True(stranger.Status == 403 && Encoding.UTF8.GetString(stranger.Body).Contains("nedôveruje", StringComparison.Ordinal), "cudzia konzola: " + stranger.Status);
            var unsigned = PinnedClient.Post(new Uri(server.Address, "v1/zverejnenie"), server.Pin, body, new Dictionary<string, string>(), 100_000, TimeSpan.FromSeconds(20));
            Expect.Equal(403, unsigned.Status, "bez podpisu");
            var elsewhere = PinnedClient.Post(new Uri(server.Address, "v1/stav"), server.Pin, body, new Dictionary<string, string>(), 100_000, TimeSpan.FromSeconds(20));
            Expect.Equal(405, elsewhere.Status, "inde sa nič neodovzdáva");

            // the envelope is bound to the bytes, this centre and the time
            var trusted = new List<TrustedConsole> { RecipeSigning.Trust(pub) };
            var headers = UploadSigning.Headers(body, "/v1/zverejnenie", server.Pin, key, T);
            string? H(string n) => headers.TryGetValue(n, out var v) ? v : null;
            Expect.True(UploadSigning.Verify(H, body, "/v1/zverejnenie", server.Pin, trusted, T.AddMinutes(5)).Console is not null, "platná obálka");
            Expect.True(UploadSigning.Verify(H, body, "/v1/zverejnenie", server.Pin, trusted, T.AddMinutes(20)).Problem.Contains("10 minút", StringComparison.Ordinal), "staré odovzdanie");
            Expect.True(UploadSigning.Verify(H, body, "/v1/zverejnenie", new string('c', 64), trusted, T).Console is null, "pre iné centrum");
            Expect.True(UploadSigning.Verify(H, [.. body, 32], "/v1/zverejnenie", server.Pin, trusted, T).Console is null, "zmenené bajty");

            // a recipe whose text does not fit its fingerprint is refused, the rest of the centre stays
            var forged = bundle with { Recipes = [bundle.Recipes[0] with { Recipe = bundle.Recipes[0].Recipe.Replace("ZAKLADNY", "INY", StringComparison.Ordinal) }], Plans = [] };
            var (lines, refused) = new PublicationFolder(publication).Accept(forged, trusted);
            Expect.True(refused && lines.Any(l => l.Contains("odmietnutý", StringComparison.Ordinal)), "podvrhnutý recept: " + string.Join("; ", lines));
            Expect.True(sync.Recipe(bundle.Recipes[0].Fingerprint) is not null, "pôvodný recept ostal");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Check]
    public static void WhenAProfileIsApplied()
    {
        var wanted = ("KANCELARSKY", "aa");
        Expect.True(CentrePolicy.ShouldApply(wanted, null, null, null, T), "prvýkrát");
        Expect.True(!CentrePolicy.ShouldApply(wanted, "KANCELARSKY", "aa", T, T.AddMinutes(30)), "ten istý profil, nedávno");
        Expect.True(CentrePolicy.ShouldApply(wanted, "KANCELARSKY", "aa", T, T.AddHours(1)), "raz za hodinu znova (napraví, čo sa zmenilo)");
        Expect.True(CentrePolicy.ShouldApply(wanted, "ZAKLADNY", "aa", T, T.AddMinutes(1)), "iný profil hneď");
        Expect.True(CentrePolicy.ShouldApply(wanted, "KANCELARSKY", "bb", T, T.AddMinutes(1)), "nová verzia receptu hneď");
    }

    [Check]
    public static void TheSupplyIsServedOnlyAsDescribed()
    {
        var dir = Directory.CreateTempSubdirectory("polaris-zasoba-web-").FullName;
        try
        {
            var installer = Path.Combine(dir, "setup.exe");
            File.WriteAllBytes(installer, [0x4D, 0x5A, 9, 9, 9]);
            var supply = new PackageStore(Path.Combine(dir, "zasoba"));
            var p = supply.Add(installer, new Package { Id = "skuska-1", Title = "Skúška", Kind = PackageKind.Exe, Arguments = "/S", DisplayName = "Skúška", AddedUtc = T });
            var routes = Polaris.Centrum.Commands.Routes(Path.Combine(dir, "zverejnene"), supply.Root);
            ServerResponse Get(string path) => routes(new ServerRequest("GET", path, new Dictionary<string, string>(), IPAddress.Loopback));
            Expect.Equal(200, Get("/v1/baliky/skuska-1/balik.json").Status, "popis balíka");
            var file = Get("/v1/baliky/skuska-1/setup.exe");
            Expect.True(file.Status == 200 && file.FilePath is not null && file.ETag == "\"" + p.Sha256 + "\"", "inštalátor");
            Expect.Equal(404, Get("/v1/baliky/skuska-1/iny.exe").Status, "iný súbor nie");
            Expect.Equal(404, Get("/v1/baliky/neexistuje/balik.json").Status, "neznámy balík");
            Expect.Equal(404, Get("/v1/skupiny/ucebna.json").Status, "nič nezverejnené");
            Expect.Equal(200, Get("/v1/stav").Status, "stav");
            Expect.True(Encoding.UTF8.GetString(Get("/v1/stav").Body!).Contains(Product.Version, StringComparison.Ordinal), "verzia");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Check]
    public static void APcRegistersWithACodeAndReportsToTheCentre()
    {
        var dir = Directory.CreateTempSubdirectory("polaris-pc-").FullName;
        try
        {
            var (secret, pub) = RecipeSigning.NewConsoleKey("spravca", Password);
            var keyFile = Path.Combine(dir, "spravca.kluc");
            File.WriteAllText(keyFile, RecipeSigning.ToJson(secret));
            var (_, notebook) = RecipeSigning.NewConsoleKey("notebook2", Password);
            File.WriteAllText(Path.Combine(dir, "notebook2.verejny.json"), RecipeSigning.ToJson(notebook));
            var registry = new PcRegistry(Path.Combine(dir, "pc"));
            var now = T;
            Running? server = null;
            var trusted = new List<TrustedConsole> { RecipeSigning.Trust(pub) };
            var pcs = (ServerRequest r) => Polaris.Centrum.Commands.PcRoutes(registry, server!.Pin, () => trusted, () => now)(r);
            server = new Running(Polaris.Centrum.Commands.Routes(Path.Combine(dir, "zverejnene"), null, null, pcs), dir, Polaris.Centrum.Commands.BodyPaths.Contains);
            using var running = server;
            var settings = Path.Combine(dir, "centrum.json");
            (int Code, string Text) Centrum(string line)
            {
                var w = new StringWriter();
                var args = new[] { "--subor", settings }.Concat(line.Split(' ', StringSplitOptions.RemoveEmptyEntries)).ToArray();
                return (Polaris.Centrum.Commands.Run(args, w, now.ToLocalTime().DateTime, now, _ => Password), w.ToString());
            }

            string NewCode(string extra = "")
            {
                var (code, text) = Centrum($"kod ucebna {keyFile} --server {server.Address} {server.Pin} {extra}");
                Expect.Equal(0, code, text);
                return text.Split('\n')[0].Split(": ")[1].Trim();
            }

            // the PC: its two keys, who it is, a registration signed by it
            var code = NewCode("--pocet 2");
            Expect.True(RegistrationCode.Normalize(code) is { Length: 12 }, "kód z konzoly: " + code);
            using var pcKey = SigningKey.Create();
            using var secrets = System.Security.Cryptography.ECDiffieHellman.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256);
            PcIdentity Identity(SigningKey k, string name)
            {
                using var p = k.Public();
                return new PcIdentity(Convert.ToBase64String(p.Export()), Convert.ToBase64String(secrets.ExportSubjectPublicKeyInfo()), name, "Windows 11 Pro (26200)", "Vyrobca Skusobny 1", "0.1.0");
            }

            (bool Ok, string Message, Polaris.Json.JsonFields F) Post(string path, string body, IReadOnlyDictionary<string, string>? headers = null)
            {
                var answer = PinnedClient.Post(new Uri(server.Address, path.TrimStart('/')), server.Pin, Encoding.UTF8.GetBytes(body), headers ?? new Dictionary<string, string>(), 1 << 20, TimeSpan.FromSeconds(20));
                var f = new Polaris.Json.JsonFields(Polaris.Json.JsonReader.Parse(Encoding.UTF8.GetString(answer.Body)), "odpoveď");
                return (answer.Ok && f.Bool("ok", false), f.String("message"), f);
            }

            var identity = Identity(pcKey, "PC-01");
            var registered = Post(CentrePaths.Register, RegistrationRequest.Make(code.ToLowerInvariant().Replace("-", " ", StringComparison.Ordinal), identity, pcKey, server.Pin, now)!.ToJson());
            Expect.True(registered.Ok && registered.F.String("group") == "ucebna" && registered.F.String("pc") == identity.Id, "PC zaregistrované kódom (aj napísaným malými písmenami s medzerami): " + registered.Message);
            var trustText = registered.F.String("trust");
            var normalized = RegistrationCode.Normalize(code)!;
            Expect.Equal(RegistrationCode.Seal(normalized, trustText), registered.F.String("trustSeal"), "zoznam konzol zapečatený kódom");
            Expect.True(RegistrationCode.Seal(normalized, trustText.Replace("spravca", "cudzia", StringComparison.Ordinal)) != registered.F.String("trustSeal"), "zmenený zoznam konzol by PC odhalilo");
            Expect.Equal("notebook2, spravca", string.Join(", ", TrustList.FromJson(trustText).Select(c => c.Name).Order(StringComparer.Ordinal)), "PC dostane všetky konzoly správcu (kľúče vedľa kľúča konzoly)");
            Expect.True(Post(CentrePaths.Register, RegistrationRequest.Make(code, identity, pcKey, server.Pin, now)!.ToJson()).Ok, "opakovaná registrácia toho istého PC (stratená odpoveď) prejde");

            var wrong = Post(CentrePaths.Register, RegistrationRequest.Make("AAAA-BBBB-CCCC", Identity(SigningKey.Create(), "PC-X"), pcKey, server.Pin, now)!.ToJson());
            Expect.True(!wrong.Ok, "registrácia podpísaná iným kľúčom, než ktorý PC uvádza, neprejde: " + wrong.Message);
            using var otherKey = SigningKey.Create();
            Expect.True(Post(CentrePaths.Register, RegistrationRequest.Make("AAAA-BBBB-CCCC", Identity(otherKey, "PC-X"), otherKey, server.Pin, now)!.ToJson()).Message.Contains("nevydalo", StringComparison.Ordinal), "neznámy kód");
            Expect.True(!Post(CentrePaths.Register, RegistrationRequest.Make(code, Identity(otherKey, "PC-X"), otherKey, new string('c', 64), now)!.ToJson()).Ok, "registrácia pre iné centrum neprejde");

            // reports: only signed by the PC's own key
            var report = new PcReport(now, "0.1.0", "Windows 11 Pro (26200)", "Vyrobca Skusobny 1", "ucebna", "ZAKLADNY", "abc", 3600, 40L << 30, 100L << 30, 1, "ziak 19. 11. 2026, 221 MB",
                [RunReport.ToLine(new RunRecord { StartedUtc = now, FinishedUtc = now, Recipe = "ZAKLADNY", Results = [new ChangeResult("x", "služba Xbox", ChangeKind.Install, false, "prístup odmietnutý", 0)] })],
                [RunReport.ToLine(Measurement.Record("ziak", 21, (8UL << 30, 5UL << 30), [], 0, now, now))]);
            var body = report.ToJson();
            var sent = Post(CentrePaths.Report, body, PcSigning.Headers(Encoding.UTF8.GetBytes(body), CentrePaths.Report, server.Pin, identity.Id, pcKey, now));
            Expect.True(sent.Ok, "hlásenie prijaté: " + sent.Message);
            Expect.True(!Post(CentrePaths.Report, body, PcSigning.Headers(Encoding.UTF8.GetBytes(body), CentrePaths.Report, server.Pin, identity.Id, otherKey, now)).Ok, "hlásenie podpísané cudzím kľúčom odmietnuté");
            Expect.True(!Post(CentrePaths.Report, body + " ", PcSigning.Headers(Encoding.UTF8.GetBytes(body), CentrePaths.Report, server.Pin, identity.Id, pcKey, now)).Ok, "zmenené hlásenie odmietnuté");
            Expect.True(!Post(CentrePaths.Report, body, PcSigning.Headers(Encoding.UTF8.GetBytes(body), CentrePaths.Report, server.Pin, identity.Id, pcKey, now.AddMinutes(-30))).Ok, "staré hlásenie odmietnuté");

            // the console sees the PC
            var (listCode, list) = Centrum($"stav {keyFile} --server {server.Address} {server.Pin}");
            Expect.True(listCode == 0 && list.Contains("PC-01", StringComparison.Ordinal) && list.Contains("chyby receptov: 1", StringComparison.Ordinal) && list.Contains("bez ovládača: 1", StringComparison.Ordinal), "konzola vidí PC: " + list);
            var (_, detail) = Centrum($"stav {keyFile} --server {server.Address} {server.Pin} PC-01");
            Expect.True(detail.Contains("služba Xbox: prístup odmietnutý", StringComparison.Ordinal) && detail.Contains("pracovná plocha za 21 s", StringComparison.Ordinal) && detail.Contains("221 MB", StringComparison.Ordinal), "detail PC: " + detail);
            now = now.AddHours(2);
            Expect.True(Centrum($"stav {keyFile} --server {server.Address} {server.Pin}").Text.Contains("neozvalo sa", StringComparison.Ordinal), "PC, ktoré dlho mlčí");

            // the code's limits: count, time, trust
            using var second = SigningKey.Create();
            Expect.True(Post(CentrePaths.Register, RegistrationRequest.Make(code, Identity(second, "PC-02"), second, server.Pin, now)!.ToJson()).Ok, "druhé PC kódom pre 2");
            using var third = SigningKey.Create();
            Expect.True(Post(CentrePaths.Register, RegistrationRequest.Make(code, Identity(third, "PC-03"), third, server.Pin, now)!.ToJson()).Message.Contains("už použilo", StringComparison.Ordinal), "tretie PC už nie");
            var daily = NewCode();
            now = now.AddDays(1).AddMinutes(1);
            Expect.True(Post(CentrePaths.Register, RegistrationRequest.Make(daily, Identity(third, "PC-03"), third, server.Pin, now)!.ToJson()).Message.Contains("platil do", StringComparison.Ordinal), "kód po dni neplatí");
            trusted.Clear();
            Expect.True(Centrum($"kod ucebna {keyFile} --server {server.Address} {server.Pin}").Code != 0, "nedôverovaná konzola kód nevydá");
            trusted.Add(RecipeSigning.Trust(pub));

            // removed in the console: the PC's reports are refused
            Expect.Equal(0, Centrum($"stav {keyFile} --server {server.Address} {server.Pin} {identity.Id[..8]} odobrat").Code, "PC odobraté");
            Expect.True(!Post(CentrePaths.Report, body, PcSigning.Headers(Encoding.UTF8.GetBytes(body), CentrePaths.Report, server.Pin, identity.Id, pcKey, now)).Ok, "odobraté PC sa už nehlási");

            // the record lets the console (which knew the code) confirm the PC for another centre
            var kept = registry.Find(second.Public() is var p2 ? PcIdentity.IdOf(Convert.ToBase64String(p2.Export())) : "")!;
            Expect.Equal(PcRecord.ProofOf(RegistrationCode.Normalize(code)!, kept.Pc), kept.CodeProof, "dôkaz kódu pri PC");
            var proof = registry.Proof(kept.Id);
            Expect.True(proof is { } pr && pr.Request.Proven(server.Pin, pr.Request.Time is var t ? DateTimeOffset.Parse(t, System.Globalization.CultureInfo.InvariantCulture) : now)
                && RegistrationCode.GrantHash(pr.Request.Ticket) == pr.Grant.CodeHash && pr.Grant.Check(trusted, pr.Grant.NotAfterUtc.AddMinutes(-1)).Console is not null,
                "registrácia PC sa dá neskôr overiť aj inde: podpis PC, kód z konzoly, podpis konzoly");
            Expect.True(!Directory.EnumerateFiles(Path.Combine(dir, "pc"), "*", SearchOption.AllDirectories).Any(f => File.ReadAllText(f).Contains(RegistrationCode.Normalize(code)!, StringComparison.Ordinal)), "samotný kód centrum nikde nemá");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Check]
    public static void RegistrationCodesAreForgivingButExact()
    {
        var code = RegistrationCode.Generate();
        Expect.True(code.Length == 14 && code[4] == '-' && code[9] == '-', "tvar XXXX-XXXX-XXXX: " + code);
        Expect.Equal(RegistrationCode.Normalize(code), RegistrationCode.Normalize(code.ToLowerInvariant().Replace('-', ' ')), "malé písmená a medzery");
        Expect.Equal("01V0ABCDEFGH", RegistrationCode.Normalize("oiu0-abcd-efgh"), "O → 0, I → 1, U → V");
        Expect.True(RegistrationCode.Normalize("ABCD-EFGH") is null && RegistrationCode.Normalize("ABCD-EFGH-JKM!") is null, "krátky alebo s iným znakom");
        Expect.True(Enumerable.Range(0, 50).Select(_ => RegistrationCode.Generate()).Distinct().Count() == 50, "kódy sa neopakujú");
    }

    [Check]
    public static void TheCentreFileIsReadAsTheServerWritesIt()
    {
        var pin = new string('a', 64);
        var written = CentreFile.Parse($"https://172.16.0.10:8443 {pin}\r\n");
        Expect.True(written is { } w && w.Address.ToString() == "https://172.16.0.10:8443/" && w.Pin == pin, "riadok zo servera");
        Expect.Equal("https://172.16.0.10:8443/", CentreFile.Parse($"172.16.0.10 {pin.ToUpperInvariant()}")?.Address.ToString(), "holá adresa dostane https a port 8443");
        Expect.True(CentreFile.Parse($"http://172.16.0.10:8443 {pin}") is null, "nie http");
        Expect.True(CentreFile.Parse("https://172.16.0.10:8443 abc") is null && CentreFile.Parse("") is null, "krátky odtlačok, prázdny súbor");
        Expect.Equal(written, CentreFile.Parse(CentreFile.Line(written!.Value.Address, written.Value.Pin)), "čo sa zapíše, to sa prečíta");
    }

    /// <summary>A plain HTTP request to the console on this PC (its own server speaks HTTP only on 127.0.0.1).</summary>
    private static (int Status, string Head, string Body) Http(int port, string method, string path, string? host = null, string? token = null, string? body = null)
    {
        using var client = new System.Net.Sockets.TcpClient("127.0.0.1", port);
        using var stream = client.GetStream();
        var bytes = body is null ? [] : Encoding.UTF8.GetBytes(body);
        var head = $"{method} {path} HTTP/1.1\r\nHost: {host ?? $"127.0.0.1:{port}"}\r\n"
            + (token is null ? "" : $"X-Polaris-Konzola: {token}\r\n")
            + (body is null ? "" : $"Content-Type: application/json\r\nContent-Length: {bytes.Length}\r\n")
            + "Connection: close\r\n\r\n";
        stream.Write(Encoding.ASCII.GetBytes(head));
        stream.Write(bytes);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var all = reader.ReadToEnd();
        var cut = all.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        var status = int.Parse(all.AsSpan(9, 3), System.Globalization.CultureInfo.InvariantCulture);
        return (status, all[..cut], all[(cut + 4)..]);
    }

    [Check]
    public static void TheConsoleInTheBrowserShowsTheClassroomAndIssuesCodes()
    {
        var dir = Directory.CreateTempSubdirectory("polaris-konzola-").FullName;
        try
        {
            var (secret, pub) = RecipeSigning.NewConsoleKey("notebook1", Password);
            var keyPath = Path.Combine(dir, "notebook1.kluc");
            File.WriteAllText(keyPath, RecipeSigning.ToJson(secret));
            var registry = new PcRegistry(Path.Combine(dir, "pc"));
            var now = T;
            Running? centre = null;
            var trusted = new List<TrustedConsole> { RecipeSigning.Trust(pub) };
            var pcs = (ServerRequest r) => Polaris.Centrum.Commands.PcRoutes(registry, centre!.Pin, () => trusted, () => now)(r);
            centre = new Running(Polaris.Centrum.Commands.Routes(Path.Combine(dir, "zverejnene"), null, null, pcs), dir, Polaris.Centrum.Commands.BodyPaths.Contains);
            using var running = centre;

            var free = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            free.Start();
            var port = ((IPEndPoint)free.LocalEndpoint).Port;
            free.Stop();
            using var key = RecipeSigning.OpenPrivate(secret, Password);
            var token = "skuskovy-kluc-tohto-behu";
            var routes = Polaris.Centrum.Commands.ConsoleRoutes((centre.Address, centre.Pin), keyPath, secret, key, token, port, () => now);
            using var stop = new CancellationTokenSource();
            var started = new TaskCompletionSource();
            var web = new WebServer(new IPEndPoint(IPAddress.Loopback, port), null, routes, _ => { }, p => p is "/api/kod" or "/api/odobrat");
            var task = web.RunAsync(stop.Token, started);
            started.Task.Wait(TimeSpan.FromSeconds(10));
            Expect.Throws<ArgumentException>(() => _ = new WebServer(new IPEndPoint(IPAddress.Any, 0), null, routes, _ => { }), "bez certifikátu nikdy na sieti");

            var page = Http(port, "GET", "/");
            Expect.True(page.Status == 200 && page.Body.Contains("POLARIS", StringComparison.Ordinal) && page.Head.Contains("Content-Security-Policy: default-src 'self'", StringComparison.Ordinal) && page.Head.Contains("X-Frame-Options: DENY", StringComparison.Ordinal), "stránka konzoly s prísnymi hlavičkami");
            Expect.True(Http(port, "GET", "/konzola.js").Body.Contains("X-Polaris-Konzola", StringComparison.Ordinal), "skript konzoly");
            Expect.Equal(403, Http(port, "GET", "/api/stav").Status, "bez kľúča behu nič");
            Expect.Equal(403, Http(port, "GET", "/api/stav", token: "iny").Status, "s cudzím kľúčom nič");
            Expect.Equal(403, Http(port, "GET", "/", host: $"zly.example:{port}").Status, "cudzie meno (DNS rebinding) nič");

            var empty = Http(port, "GET", "/api/stav", token: token);
            Expect.True(empty.Status == 200 && empty.Body.Contains("\"pcs\":[]", StringComparison.Ordinal) && empty.Body.Contains("notebook1", StringComparison.Ordinal), "prázdna učebňa: " + empty.Body);

            var issued = Http(port, "POST", "/api/kod", token: token, body: "{\"group\":\"ucebna\",\"days\":1,\"count\":5}");
            var f = new Polaris.Json.JsonFields(Polaris.Json.JsonReader.Parse(issued.Body), "kód");
            Expect.True(f.Bool("ok", false) && f.String("command").Contains(centre.Pin, StringComparison.Ordinal), "kód z konzoly v prehliadači: " + issued.Body);
            var code = f.String("code");

            using var pcKey = SigningKey.Create();
            using var secrets = System.Security.Cryptography.ECDiffieHellman.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256);
            using var p = pcKey.Public();
            var identity = new PcIdentity(Convert.ToBase64String(p.Export()), Convert.ToBase64String(secrets.ExportSubjectPublicKeyInfo()), "PC-07", "Windows 11 Pro (26200)", "Vyrobca Skusobny 1", "0.1.0");
            var reg = PinnedClient.Post(new Uri(centre.Address, CentrePaths.Register.TrimStart('/')), centre.Pin, Encoding.UTF8.GetBytes(RegistrationRequest.Make(code, identity, pcKey, centre.Pin, now)!.ToJson()), new Dictionary<string, string>(), 1 << 20, TimeSpan.FromSeconds(20));
            Expect.True(reg.Ok, "PC zaregistrované kódom z konzoly v prehliadači");
            var report = new PcReport(now, "0.1.0", "Windows 11 Pro (26200)", "Vyrobca Skusobny 1", "ucebna", "", "", 600, 9L << 30, 100L << 30, 0, "",
                [RunReport.ToLine(new RunRecord { StartedUtc = now, FinishedUtc = now, Recipe = "ZAKLAD", Results = [new ChangeResult("x", "služba Xbox", ChangeKind.Install, false, "prístup odmietnutý", 0)] })], []);
            var body = Encoding.UTF8.GetBytes(report.ToJson());
            Expect.True(PinnedClient.Post(new Uri(centre.Address, CentrePaths.Report.TrimStart('/')), centre.Pin, body, PcSigning.Headers(body, CentrePaths.Report, centre.Pin, identity.Id, pcKey, now), 1 << 20, TimeSpan.FromSeconds(20)).Ok, "hlásenie");

            var state = Http(port, "GET", "/api/stav", token: token).Body;
            Expect.True(state.Contains("PC-07", StringComparison.Ordinal) && state.Contains("\"mark\":\"!\"", StringComparison.Ordinal) && state.Contains("služba Xbox: prístup odmietnutý", StringComparison.Ordinal) && state.Contains("málo miesta na disku", StringComparison.Ordinal), "učebňa v konzole: " + state);

            Expect.True(Http(port, "POST", "/api/odobrat", token: token, body: $"{{\"id\":\"{identity.Id}\"}}").Body.Contains("\"ok\":true", StringComparison.Ordinal), "odobratie z konzoly");
            Expect.True(Http(port, "GET", "/api/stav", token: token).Body.Contains("\"pcs\":[]", StringComparison.Ordinal), "PC zmizlo");
            stop.Cancel();
            task.Wait(TimeSpan.FromSeconds(10));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Check]
    public static void TheConsoleProgramStartsLockedAndIsSetUpInItsWindow()
    {
        var dir = Directory.CreateTempSubdirectory("polaris-okno-").FullName;
        try
        {
            using var state = new Polaris.Centrum.ConsoleState(Path.Combine(dir, "konzola"));
            var now = T;
            Running? centre = null;
            var registry = new PcRegistry(Path.Combine(dir, "pc"));
            var trusted = new List<TrustedConsole>();
            var pcs = (ServerRequest r) => Polaris.Centrum.Commands.PcRoutes(registry, centre!.Pin, () => trusted, () => now)(r);
            centre = new Running(Polaris.Centrum.Commands.Routes(Path.Combine(dir, "zverejnene"), null, null, pcs), dir, Polaris.Centrum.Commands.BodyPaths.Contains);
            using var running = centre;
            var free = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            free.Start();
            var port = ((IPEndPoint)free.LocalEndpoint).Port;
            free.Stop();
            const string token = "kluc-behu-okna";
            using var stop = new CancellationTokenSource();
            var started = new TaskCompletionSource();
            var web = new WebServer(new IPEndPoint(IPAddress.Loopback, port), null, Polaris.Centrum.Commands.ConsoleRoutes(state, token, port, () => now), _ => { }, Polaris.Centrum.Commands.ConsoleBody.Contains);
            var task = web.RunAsync(stop.Token, started);
            started.Task.Wait(TimeSpan.FromSeconds(10));

            Expect.True(Http(port, "GET", "/api/start", token: token).Body.Contains("\"keys\":[]", StringComparison.Ordinal), "prvé spustenie: žiadny kľúč");
            Expect.True(Http(port, "GET", "/api/stav", token: token).Body.Contains("\"locked\":true", StringComparison.Ordinal), "zamknutá konzola centru nič nepovie");
            Expect.True(Http(port, "POST", "/api/novy-kluc", token: token, body: "{\"name\":\"ucitel\",\"password\":\"kratke\"}").Body.Contains("\"ok\":false", StringComparison.Ordinal), "krátke heslo");
            Expect.True(Http(port, "POST", "/api/novy-kluc", token: token, body: $"{{\"name\":\"ucitel\",\"password\":\"{Password}\"}}").Body.Contains("\"ok\":true", StringComparison.Ordinal), "kľúč z okna");
            Expect.True(File.Exists(Path.Combine(dir, "konzola", "ucitel.verejny.json")), "verejný kľúč na server");
            trusted.Add(RecipeSigning.Trust(RecipeSigning.KeyFromJson(File.ReadAllText(Path.Combine(dir, "konzola", "ucitel.verejny.json")))));
            Expect.True(Http(port, "POST", "/api/centrum", token: token, body: "{\"address\":\"172.16.0.10\",\"pin\":\"abc\"}").Body.Contains("\"ok\":false", StringComparison.Ordinal), "zlý odtlačok");
            Expect.True(Http(port, "POST", "/api/centrum", token: token, body: $"{{\"address\":\"{centre.Address}\",\"pin\":\"{centre.Pin}\"}}").Body.Contains("\"ok\":true", StringComparison.Ordinal), "centrum z okna");
            Expect.True(File.Exists(Path.Combine(dir, "konzola", CentreFile.Name)), "centrum.txt uložené");
            Expect.True(Http(port, "POST", "/api/odomknut", token: token, body: "{\"name\":\"ucitel\",\"password\":\"zle heslo zle heslo\"}").Body.Contains("Nesprávne heslo", StringComparison.Ordinal), "zlé heslo");
            Expect.Equal(403, Http(port, "POST", "/api/odomknut", body: $"{{\"name\":\"ucitel\",\"password\":\"{Password}\"}}").Status, "odomknúť len s kľúčom behu");
            Expect.True(Http(port, "POST", "/api/odomknut", token: token, body: $"{{\"name\":\"ucitel\",\"password\":\"{Password}\"}}").Body.Contains("\"ok\":true", StringComparison.Ordinal), "odomknuté");
            var room = Http(port, "GET", "/api/stav", token: token).Body;
            Expect.True(room.Contains("\"ok\":true", StringComparison.Ordinal) && room.Contains("\"pcs\":[]", StringComparison.Ordinal), "po odomknutí učebňa: " + room);
            Expect.True(Http(port, "GET", "/ikona.svg").Body.Contains("<svg", StringComparison.Ordinal), "ikona okna");
            stop.Cancel();
            task.Wait(TimeSpan.FromSeconds(10));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Check]
    public static void AnUpdateIsSignedByTheConsoleAndCheckedByEveryone()
    {
        using var key = SigningKey.Create();
        using var other = SigningKey.Create();
        using var pub = key.Public();
        var trusted = new List<TrustedConsole> { new("ucitel", pub.Fingerprint(), pub.Export()) };
        var file = Encoding.UTF8.GetBytes("nový klient");
        var m = UpdateSigning.Sign(UpdateProducts.Client, "0.1.3", file, key, "ucitel", T);
        Expect.Equal(SignatureState.Valid, UpdateSigning.Check(m, trusted).State, "podpísaná dôverovanou konzolou");
        Expect.Equal(SignatureState.Valid, UpdateSigning.Check(UpdateManifest.FromJson(m.ToJson()), trusted).State, "aj po ceste ako text");
        Expect.Equal(SignatureState.Broken, UpdateSigning.Check(m with { Version = "9.9.9" }, trusted).State, "iná verzia – podpis nesedí");
        Expect.Equal(SignatureState.Broken, UpdateSigning.Check(m with { Sha256 = new string('0', 64) }, trusted).State, "iný súbor – podpis nesedí");
        Expect.Equal(SignatureState.UnknownKey, UpdateSigning.Check(UpdateSigning.Sign(UpdateProducts.Client, "0.1.3", file, other, "cudzia", T), trusted).State, "cudzia konzola");
        Expect.Equal(SignatureState.ContentChanged, UpdateSigning.Check(m with { Product = "iny" }, trusted).State, "neznámy produkt");
        using (var bytes = new MemoryStream(file))
        {
            Expect.True(UpdateSigning.Matches(m, bytes), "súbor je ten podpísaný");
        }

        using (var bytes = new MemoryStream(Encoding.UTF8.GetBytes("nový klienT")))
        {
            Expect.True(!UpdateSigning.Matches(m, bytes), "zmenený bajt");
        }

        Expect.True(UpdateSigning.Newer("0.1.3", "0.1.2") && UpdateSigning.Newer("0.2.0", "0.1.12") && UpdateSigning.Newer("v1.0", "0.9.9"), "novšie verzie");
        Expect.True(!UpdateSigning.Newer("0.1.2", "0.1.2") && !UpdateSigning.Newer("0.1.2", "0.1.3"), "rovnaká a staršia");
        Expect.True(UpdateSigning.Newer("0.1.3", "0.1.3~ci90") && !UpdateSigning.Newer("0.1.3~ci90", "0.1.3") && UpdateSigning.Newer("0.1.3~ci90", "0.1.2"), "zostavenie z CI je pred vydaním");
    }

    [Check]
    public static void ANewClientGoesFromTheConsoleThroughTheCentreToThePc()
    {
        var dir = Directory.CreateTempSubdirectory("polaris-aktualizacia-").FullName;
        try
        {
            var (secret, pub) = RecipeSigning.NewConsoleKey("ucitel", Password);
            using var key = RecipeSigning.OpenPrivate(secret, Password);
            var (_, strangerPub) = RecipeSigning.NewConsoleKey("cudzia", Password);
            var trusted = new List<TrustedConsole> { RecipeSigning.Trust(pub) };
            var publication = Path.Combine(dir, "zverejnene");
            Running? centre = null;
            var updates = (ServerRequest r) => Polaris.Centrum.Commands.UpdateUpload(publication, centre!.Pin, () => trusted, () => T)(r);
            centre = new Running(Polaris.Centrum.Commands.Routes(publication, null, null, null, updates), dir, Polaris.Centrum.Commands.BodyPaths.Contains);
            using var running = centre;
            var server = (centre.Address, centre.Pin);

            var client = new byte[300_000];
            new Random(7).NextBytes(client);
            var (ok, message) = Polaris.Centrum.Commands.HandUpdate(server, key, "ucitel", UpdateProducts.Client, "0.1.3", client, T);
            Expect.True(ok, "centrum prijalo klienta 0.1.3: " + message);
            var versions = Polaris.Centrum.Commands.CentreVersions(server);
            Expect.Equal("0.1.3", versions[UpdateProducts.Client], "centrum rozosiela 0.1.3");
            Expect.Equal("", versions[UpdateProducts.ServerDebian], "server zatiaľ nie");

            Expect.True(!Polaris.Centrum.Commands.HandUpdate(server, key, "ucitel", UpdateProducts.Client, "0.1.2", client, T).Ok, "staršia verzia sa neprijme");
            using (var stranger = SigningKey.Create())
            {
                Expect.True(!Polaris.Centrum.Commands.HandUpdate(server, stranger, "cudzia", UpdateProducts.Client, "0.1.4", client, T).Ok, "cudzia konzola nič");
            }

            // the PC: takes only what a console it trusts signed, newer than its own, byte for byte
            var settings = new CentreSettings(centre.Address, centre.Pin, "ucebna");
            var folder = Path.Combine(dir, "pc-aktualizacia");
            var pc = new ClientUpdate(settings, folder, () => trusted, () => T);
            var (program, said) = pc.Look("0.1.2");
            Expect.True(program is not null && File.ReadAllBytes(program).AsSpan().SequenceEqual(client), "PC stiahlo presne podpísaný súbor: " + said);
            Expect.True(said.Contains("0.1.2 → 0.1.3", StringComparison.Ordinal), "záznam do denníka: " + said);
            Expect.True(new ClientUpdate(settings, folder, () => trusted, () => T).Look("0.1.2").Program is null, "ten istý pokus nie hneď znova");
            Expect.True(new ClientUpdate(settings, folder, () => trusted, () => T.AddHours(2)).Look("0.1.2").Program is not null, "po hodine znova");
            Expect.True(new ClientUpdate(settings, Path.Combine(dir, "pc2"), () => trusted, () => T).Look("0.1.3").Program is null, "rovnaká verzia nie");
            var (refused, why) = new ClientUpdate(settings, Path.Combine(dir, "pc3"), () => [RecipeSigning.Trust(strangerPub)], () => T).Look("0.1.2");
            Expect.True(refused is null && why.Contains("odmietnutá", StringComparison.Ordinal), "PC s inou konzolou odmietne: " + why);

            // a changed file on the centre (the centre is not trusted with the bytes)
            var bin = Directory.GetFiles(Path.Combine(publication, "aktualizacie"), "*.bin").Single();
            var bad = File.ReadAllBytes(bin);
            bad[1000] ^= 1;
            File.WriteAllBytes(bin, bad);
            var (taken, note) = new ClientUpdate(settings, Path.Combine(dir, "pc4"), () => trusted, () => T).Look("0.1.2");
            Expect.True(taken is null && note.Contains("nie je ten", StringComparison.Ordinal), "zmenený súbor na centre odmietnutý: " + note);

            // the check the Debian server runs as root: only consoles in its own folder count
            var keys = Path.Combine(dir, "konzoly");
            Directory.CreateDirectory(keys);
            File.WriteAllText(Path.Combine(keys, "ucitel.verejny.json"), RecipeSigning.ToJson(pub));
            var deb = new byte[1000];
            Expect.True(Polaris.Centrum.Commands.HandUpdate(server, key, "ucitel", UpdateProducts.ServerDebian, "0.1.3", deb, T).Ok, "server-debian na centre");
            var manifest = Path.Combine(publication, "aktualizacie", "server-debian.json");
            var o = new StringWriter();
            Expect.Equal(0, Polaris.Centrum.Commands.Run(["aktualizacia", "over", manifest, "--konzoly", keys, "--produkt", "server-debian"], o, T.LocalDateTime, T), "over: " + o);
            Expect.Equal($"0.1.3 {Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(deb))}", o.ToString().Trim(), "verzia a odtlačok pre skript");
            Expect.Equal(1, Polaris.Centrum.Commands.Run(["aktualizacia", "over", manifest, "--konzoly", keys, "--produkt", "klient"], new StringWriter(), T.LocalDateTime, T), "iný produkt");
            Expect.Equal(1, Polaris.Centrum.Commands.Run(["aktualizacia", "over", manifest, "--konzoly", Path.Combine(dir, "nikto"), "--produkt", "server-debian"], new StringWriter(), T.LocalDateTime, T), "bez dôvery nič");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Check]
    public static void TheConsoleReadsAReleaseOfGitHub()
    {
        const string json = """
        {"tag_name":"v0.1.3","published_at":"2026-11-25T10:00:00Z","author":{"login":"x"},"assets":[
          {"name":"POLARIS-Klient-pre-PC-ucebne.exe","size":7209984,"digest":"sha256:ABCDEF0123456789abcdef0123456789abcdef0123456789abcdef0123456789","browser_download_url":"https://github.com/tarzanislav/polaris/releases/download/v0.1.3/POLARIS-Klient-pre-PC-ucebne.exe"},
          {"name":"SHA256SUMS.txt","size":300,"browser_download_url":"https://github.com/tarzanislav/polaris/releases/download/v0.1.3/SHA256SUMS.txt"},
          {"name":"zly.exe","size":1,"browser_download_url":"http://example.com/zly.exe"}]}
        """;
        var r = Polaris.Centrum.GitHubReleases.Parse(json);
        Expect.Equal("0.1.3", r.Version, "verzia z tagu");
        Expect.Equal("abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789", r.Asset("POLARIS-Klient-pre-PC-ucebne.exe")?.Sha256, "odtlačok od GitHubu");
        Expect.True(r.Asset("SHA256SUMS.txt")?.Sha256 is null && r.Asset("zly.exe") is null, "bez odtlačku; nie http");
        var sums = "1111111111111111111111111111111111111111111111111111111111111111  POLARIS-Server-Debian.deb\n2222222222222222222222222222222222222222222222222222222222222222 *POLARIS-Konzola-spravcu.exe\n";
        Expect.Equal(new string('2', 64), Polaris.Centrum.GitHubReleases.Sum(sums, "POLARIS-Konzola-spravcu.exe"), "riadok sha256sum");
        Expect.True(Polaris.Centrum.GitHubReleases.Sum(sums, "iny.exe") is null, "chýba");

        var job = new Polaris.Centrum.UpdateJob(canRestart: false, () => (r, ""));
        job.Look(null, force: true);
        Expect.True(job.Pending().Contains(UpdateProducts.Title(UpdateProducts.Client)) && !job.Pending().Contains(UpdateProducts.Title(UpdateProducts.ServerDebian)), "čaká klient (vo vydaní), server nie (nie je vo vydaní)");
        Expect.True(job.Status().ToString()!.Length > 0, "stav pre okno");
    }

    [Check]
    public static void ANewPcAppliesAndTheConsoleApprovesIt()
    {
        var dir = Directory.CreateTempSubdirectory("polaris-schvalenie-").FullName;
        try
        {
            var (secret, pub) = RecipeSigning.NewConsoleKey("ucitel", Password);
            var keyPath = Path.Combine(dir, "ucitel.kluc");
            File.WriteAllText(keyPath, RecipeSigning.ToJson(secret));
            var registry = new PcRegistry(Path.Combine(dir, "pc"));
            var trusted = new List<TrustedConsole> { RecipeSigning.Trust(pub) };
            Running? centre = null;
            var pcs = (ServerRequest r) => Polaris.Centrum.Commands.PcRoutes(registry, centre!.Pin, () => trusted, () => T)(r);
            centre = new Running(Polaris.Centrum.Commands.Routes(Path.Combine(dir, "zverejnene"), null, null, pcs), dir, Polaris.Centrum.Commands.BodyPaths.Contains);
            using var running = centre;

            // the PC applies (no code): it waits
            using var pcKey = SigningKey.Create();
            using var secrets = System.Security.Cryptography.ECDiffieHellman.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256);
            using var p = pcKey.Public();
            var identity = new PcIdentity(Convert.ToBase64String(p.Export()), Convert.ToBase64String(secrets.ExportSubjectPublicKeyInfo()), "PC-11", "Windows 11 Pro (26200)", "Vyrobca Skusobny 1", "0.1.3");
            string Apply() => Encoding.UTF8.GetString(PinnedClient.Post(new Uri(centre.Address, CentrePaths.Apply.TrimStart('/')), centre.Pin, Encoding.UTF8.GetBytes(PcApplication.Make(identity, pcKey, centre.Pin, T).ToJson()), new Dictionary<string, string>(), 1 << 20, TimeSpan.FromSeconds(20)).Body);
            Expect.True(Apply().Contains("\"state\":\"caka\"", StringComparison.Ordinal), "PC čaká na schválenie");
            var forged = PcApplication.Make(identity, pcKey, centre.Pin, T) with { Time = T.AddMinutes(1).ToString("O", System.Globalization.CultureInfo.InvariantCulture) };
            Expect.True(!PinnedClient.Post(new Uri(centre.Address, CentrePaths.Apply.TrimStart('/')), centre.Pin, Encoding.UTF8.GetBytes(forged.ToJson()), new Dictionary<string, string>(), 1 << 20, TimeSpan.FromSeconds(20)).Ok, "prihláška s cudzím podpisom nie");

            // the console sees it with the check code and approves it
            using var key = RecipeSigning.OpenPrivate(secret, Password);
            var free = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            free.Start();
            var port = ((IPEndPoint)free.LocalEndpoint).Port;
            free.Stop();
            const string token = "kluc-behu-schvalenia";
            using var stop = new CancellationTokenSource();
            var started = new TaskCompletionSource();
            var web = new WebServer(new IPEndPoint(IPAddress.Loopback, port), null, Polaris.Centrum.Commands.ConsoleRoutes((centre.Address, centre.Pin), keyPath, secret, key, token, port, () => T), _ => { }, Polaris.Centrum.Commands.ConsoleBody.Contains);
            var task = web.RunAsync(stop.Token, started);
            started.Task.Wait(TimeSpan.FromSeconds(10));
            var check = PcApplication.CheckCode(identity.Id);
            var room = Http(port, "GET", "/api/stav", token: token).Body;
            Expect.True(room.Contains(check, StringComparison.Ordinal) && room.Contains("PC-11", StringComparison.Ordinal), "konzola vidí čakajúce PC s kontrolným kódom: " + room);
            var approved = Http(port, "POST", "/api/schvalit", token: token, body: $"{{\"ids\":[\"{identity.Id}\"],\"group\":\"ucebna-125\"}}").Body;
            Expect.True(approved.Contains("\"ok\":true", StringComparison.Ordinal), "prijaté: " + approved);
            Expect.Equal("ucebna-125", registry.Find(identity.Id)?.Group, "PC je v skupine");

            // the PC asks again and takes the approval only from a console of its centrum.txt
            var f = new Polaris.Json.JsonFields(Polaris.Json.JsonReader.Parse(Apply()), "odpoveď");
            Expect.Equal("schvalene", f.String("state"), "PC dostalo schválenie");
            var approval = PcApproval.FromJson(f.String("approval"));
            var (trust, problem) = approval.Check(identity.Id, centre.Pin, [trusted[0].Fingerprint]);
            Expect.True(trust is { Count: > 0 } && trust[0].Fingerprint == trusted[0].Fingerprint, "dôvera ku konzole: " + problem);
            Expect.True(approval.Check(identity.Id, centre.Pin, ["0000000000000000"]).Trust is null, "konzola mimo centrum.txt nie");
            Expect.True(approval.Check(identity.Id, new string('b', 64), [trusted[0].Fingerprint]).Trust is null, "pre iné centrum nie");
            Expect.True((approval with { Group = "ina" }).Check(identity.Id, centre.Pin, [trusted[0].Fingerprint]).Trust is null, "zmenená skupina nie");
            Expect.True(approval.Check(new string('c', 32), centre.Pin, [trusted[0].Fingerprint]).Trust is null, "iné PC nie");

            // another PC is turned away
            using var otherKey = SigningKey.Create();
            using var op = otherKey.Public();
            var other = identity with { SigningKey = Convert.ToBase64String(op.Export()), Name = "CUDZIE" };
            PinnedClient.Post(new Uri(centre.Address, CentrePaths.Apply.TrimStart('/')), centre.Pin, Encoding.UTF8.GetBytes(PcApplication.Make(other, otherKey, centre.Pin, T).ToJson()), new Dictionary<string, string>(), 1 << 20, TimeSpan.FromSeconds(20));
            Expect.True(Http(port, "POST", "/api/zamietnut", token: token, body: $"{{\"id\":\"{other.Id}\"}}").Body.Contains("\"ok\":true", StringComparison.Ordinal), "zamietnuté");
            Expect.True(registry.WaitingNow(T).Count == 0 && registry.Find(other.Id) is null, "nič nečaká, cudzie PC nie je v centre");
            stop.Cancel();
            task.Wait(TimeSpan.FromSeconds(10));

            // centrum.txt names the consoles that may approve
            var keys = Path.Combine(dir, "konzoly");
            Directory.CreateDirectory(keys);
            File.WriteAllText(Path.Combine(keys, "ucitel.verejny.json"), RecipeSigning.ToJson(pub));
            var o = new StringWriter();
            Expect.Equal(0, Polaris.Centrum.Commands.Run(["centrum-txt", "https://172.16.0.10:8443", centre.Pin, "--konzoly", keys], o, T.LocalDateTime, T), "centrum-txt");
            Expect.Equal(centre.Pin, CentreFile.Parse(o.ToString())?.Pin, "odtlačok centra");
            Expect.Equal(trusted[0].Fingerprint, CentreFile.Consoles(o.ToString()).Single().Fingerprint, "konzola na schvaľovanie");
            Expect.True(CentreFile.Consoles($"https://172.16.0.10:8443 {centre.Pin}\r\n").Count == 0, "starý centrum.txt bez konzol");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
