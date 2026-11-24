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
}
