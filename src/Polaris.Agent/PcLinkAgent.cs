using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using Polaris.Core;
using Polaris.Net;
using Polaris.Recipes;
using Polaris.Web;

namespace Polaris.Agent;

/// <summary>The two keys of this PC, made at its registration: one it signs its reports with, one secrets for it will be
/// encrypted to. Kept in the agent's protected folder (system and administrators), encrypted by Windows for this machine
/// (DPAPI) – a copy of the file is useless on another PC. The private halves never leave the PC.</summary>
[SupportedOSPlatform("windows")]
public static partial class PcKeys
{
    public static string File => Path.Combine(Packages.AgentFolder, "pc", "kluce.bin");

    public static bool Exist => System.IO.File.Exists(File);

    /// <summary>The keys, made now when there are none.</summary>
    public static (SigningKey Signing, ECDiffieHellman Secrets) LoadOrCreate()
    {
        if (Load() is { } keys)
        {
            return keys;
        }

        var signing = SigningKey.Create();
        var secrets = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var plain = Pack(signing.ExportPrivate(), secrets.ExportPkcs8PrivateKey());
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(File)!);
            AgentService.ProtectFolder(Packages.AgentFolder);
            System.IO.File.WriteAllBytes(File + ".novy", Protect(plain));
            System.IO.File.Move(File + ".novy", File, overwrite: true);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
        }

        return (signing, secrets);
    }

    public static (SigningKey Signing, ECDiffieHellman Secrets)? Load()
    {
        if (!Exist)
        {
            return null;
        }

        var plain = Unprotect(System.IO.File.ReadAllBytes(File));
        try
        {
            var (a, b) = Unpack(plain);
            var secrets = ECDiffieHellman.Create();
            secrets.ImportPkcs8PrivateKey(b, out _);
            return (SigningKey.FromPrivate(a), secrets);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
        }
    }

    public static PcIdentity Identity(SigningKey signing, ECDiffieHellman secrets, string name)
    {
        using var pub = signing.Public();
        var model = DriverBuilds.Model(DriverPlace.ThisHardware());
        return new PcIdentity(
            Convert.ToBase64String(pub.Export()),
            Convert.ToBase64String(secrets.ExportSubjectPublicKeyInfo()),
            name,
            $"{DriverPlace.ThisEdition()} ({Environment.OSVersion.Version.Build})",
            model ?? "neznámy model",
            Product.Version);
    }

    private static byte[] Pack(byte[] a, byte[] b)
    {
        var all = new byte[8 + a.Length + b.Length];
        BitConverter.TryWriteBytes(all.AsSpan(0, 4), a.Length);
        BitConverter.TryWriteBytes(all.AsSpan(4, 4), b.Length);
        a.CopyTo(all, 8);
        b.CopyTo(all, 8 + a.Length);
        return all;
    }

    private static (byte[] A, byte[] B) Unpack(byte[] all)
    {
        var a = BitConverter.ToInt32(all, 0);
        var b = BitConverter.ToInt32(all, 4);
        return a < 0 || b < 0 || 8L + a + b != all.Length
            ? throw new InvalidDataException("Súbor kľúčov PC je poškodený.")
            : (all.AsSpan(8, a).ToArray(), all.AsSpan(8 + a, b).ToArray());
    }

    private static byte[] Protect(byte[] plain) => Dpapi(plain, protect: true);

    private static byte[] Unprotect(byte[] sealedBytes) => Dpapi(sealedBytes, protect: false);

    private static unsafe byte[] Dpapi(byte[] data, bool protect)
    {
        fixed (byte* p = data)
        {
            var input = new Blob { Size = data.Length, Data = (nint)p };
            var ok = protect
                ? CryptProtectData(ref input, "POLARIS PC", 0, 0, 0, 0x1 | 0x4 /* UI_FORBIDDEN | LOCAL_MACHINE */, out var output)
                : CryptUnprotectData(ref input, 0, 0, 0, 0, 0x1, out output);
            if (!ok)
            {
                throw new Polaris.Windows.SystemCallException(protect ? "Šifrovanie kľúčov PC" : "Dešifrovanie kľúčov PC", Marshal.GetLastPInvokeError());
            }

            try
            {
                var result = new byte[output.Size];
                Marshal.Copy(output.Data, result, 0, output.Size);
                return result;
            }
            finally
            {
                LocalFree(output.Data);
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Blob
    {
        public int Size;
        public nint Data;
    }

    [LibraryImport("crypt32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CryptProtectData(ref Blob input, string description, nint entropy, nint reserved, nint prompt, int flags, out Blob output);

    [LibraryImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CryptUnprotectData(ref Blob input, nint description, nint entropy, nint reserved, nint prompt, int flags, out Blob output);

    [LibraryImport("kernel32.dll")]
    private static partial nint LocalFree(nint memory);
}

/// <summary>The PC in the centre: registration with a code from the console, then a report after every recipe and
/// measurement and every 15 minutes (agreed with the owner 24 Nov 2026: technical facts only).</summary>
[SupportedOSPlatform("windows")]
public static class PcLink
{
    public static readonly TimeSpan ReportEvery = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan Limit = TimeSpan.FromSeconds(30);
    private const int JournalBytes = 400 * 1024;

    /// <summary>The identifier of this PC in the centre (from its key), or null before the registration.</summary>
    public static string? Id()
    {
        using var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(Centre.Key);
        return k?.GetValue("Pc") as string is { } id && PcRules.Id(id) ? id : null;
    }

    public static (bool Ok, string Message, CentreSettings? Settings) Register(Uri address, string pin, string code, string name, DateTimeOffset now)
    {
        if (RegistrationCode.Normalize(code) is not { } normalized)
        {
            return (false, "Kód má 12 znakov, napr. 7K3Q-9XHD-M2PA (vypíše ho konzola: polaris-centrum kod …).", null);
        }

        var (signing, secrets) = PcKeys.LoadOrCreate();
        using (signing)
        using (secrets)
        {
            var identity = PcKeys.Identity(signing, secrets, name);
            var request = RegistrationRequest.Make(normalized, identity, signing, pin, now)!;
            var answer = PinnedClient.Post(new Uri(address, CentrePaths.Register.TrimStart('/')), pin, Encoding.UTF8.GetBytes(request.ToJson()), new Dictionary<string, string>(), 256 * 1024, Limit);
            var (ok, message, fields) = Read(answer);
            if (!ok || fields is null)
            {
                return (false, message, null);
            }

            var group = fields.String("group");
            if (fields.String("pc") != identity.Id || !PcRules.GroupName(group))
            {
                return (false, "Centrum odpovedalo inak, než sa čakalo – registrácia sa nezapísala.", null);
            }

            // the consoles to trust, sealed by the code: only the console that issued it could have sealed them
            var trust = fields.String("trust");
            if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(RegistrationCode.Seal(normalized, trust)), Encoding.ASCII.GetBytes(fields.String("trustSeal"))))
            {
                return (false, "Zoznam konzol od centra nesedí s kódom – registrácia sa nezapísala (pýta sa iné centrum, než vydalo kód?).", null);
            }

            var consoles = TrustList.FromJson(trust);
            foreach (var console in consoles)
            {
                TrustStore.Add(new ConsoleKeyFile { Name = console.Name, Fingerprint = console.Fingerprint, PublicKey = Convert.ToBase64String(console.PublicKey) });
            }

            var settings = new CentreSettings(address, pin, group);
            Centre.Configure(settings);
            using var k = Microsoft.Win32.Registry.LocalMachine.CreateSubKey(Centre.Key, writable: true);
            k.SetValue("Pc", identity.Id, Microsoft.Win32.RegistryValueKind.String);
            k.SetValue("Meno", name, Microsoft.Win32.RegistryValueKind.String);
            return (true, $"PC {name} je zaregistrované v skupine {group} (identifikátor {identity.Id[..8]}); dôveruje konzolám: {string.Join(", ", consoles.Select(c => c.Name))}.", settings);
        }
    }

    /// <summary>Sends a report now; the answer's message (or the problem).</summary>
    public static (bool Ok, string Message) Report(AgentHost host)
    {
        if (Centre.Settings() is not { } settings || Id() is not { } id || PcKeys.Load() is not { } keys)
        {
            return (false, "PC nie je zaregistrované v centre (polaris-agent registrovat …).");
        }

        using var signing = keys.Signing;
        using var secrets = keys.Secrets;
        var body = Encoding.UTF8.GetBytes(Build(host, settings).ToJson());
        var headers = PcSigning.Headers(body, CentrePaths.Report, settings.Pin, id, signing, host.Clock());
        var answer = PinnedClient.Post(settings.Of(CentrePaths.Report.TrimStart('/')), settings.Pin, body, headers, 64 * 1024, Limit);
        var (ok, message, _) = Read(answer);
        return (ok, message);
    }

    /// <summary>What the PC tells the centre about itself.</summary>
    public static PcReport Build(AgentHost host, CentreSettings settings)
    {
        var lines = JournalTail(host.JournalPath);
        var measurements = lines.Where(l => RunReport.ReadJournal(l).FirstOrDefault()?.Recipe == Measurement.Recipe).TakeLast(PcReport.MostMeasurements).ToList();
        var (profile, recipe, _) = Centre.Applied();
        var system = new DriveInfo(Path.GetPathRoot(Environment.SystemDirectory)!);
        int missing;
        try
        {
            missing = Polaris.Windows.Devices.Present().Count(d => d.MissingDriver);
        }
        catch (Polaris.Windows.SystemCallException)
        {
            missing = 0;
        }

        return new PcReport(
            host.Clock(),
            Product.Version,
            $"{DriverPlace.ThisEdition()} ({Environment.OSVersion.Version.Build})",
            DriverBuilds.Model(DriverPlace.ThisHardware()) ?? "neznámy model",
            settings.Group,
            profile ?? "",
            recipe ?? "",
            Environment.TickCount64 / 1000,
            system.IsReady ? system.AvailableFreeSpace : 0,
            system.IsReady ? system.TotalSize : 0,
            missing,
            Trezors(),
            [.. lines.TakeLast(PcReport.MostJournal)],
            measurements);
    }

    /// <summary>The trezors of the PC, one line ("ziak 19. 11. 2026, 221 MB").</summary>
    private static string Trezors()
    {
        var vault = new ProfileVault();
        var parts = new List<string>();
        foreach (var (account, _) in SessionRules.Read())
        {
            try
            {
                if (vault.Manifest(account) is { } m)
                {
                    parts.Add($"{account} {m.SavedUtc.ToLocalTime():d. M. yyyy}, {Measurement.Size(m.ArchiveLength)}");
                }
            }
            catch (Exception e) when (e is InvalidDataException or IOException or UnauthorizedAccessException)
            {
                parts.Add($"{account}: trezor sa nedá prečítať");
            }
        }

        return string.Join("; ", parts);
    }

    /// <summary>The last lines of the journal (at most 400 kB of them, whole lines only).</summary>
    public static List<string> JournalTail(string journal)
    {
        if (!System.IO.File.Exists(journal))
        {
            return [];
        }

        using var stream = new FileStream(journal, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var start = Math.Max(0, stream.Length - JournalBytes);
        stream.Position = start;
        var bytes = new byte[stream.Length - start];
        stream.ReadExactly(bytes);
        var text = Encoding.UTF8.GetString(bytes);
        var lines = text.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0).ToList();
        if (start > 0 && lines.Count > 0)
        {
            lines.RemoveAt(0); // cut in the middle
        }

        return lines.Count > PcReport.MostJournal ? lines.GetRange(lines.Count - PcReport.MostJournal, PcReport.MostJournal) : lines;
    }

    private static (bool Ok, string Message, Polaris.Json.JsonFields? Fields) Read(ClientResult answer)
    {
        if (answer.Status == 0)
        {
            return (false, "centrum nedostupné: " + answer.Error, null);
        }

        try
        {
            var f = new Polaris.Json.JsonFields(Polaris.Json.JsonReader.Parse(Encoding.UTF8.GetString(answer.Body), Polaris.Json.JsonSyntax.Strict), "odpoveď centra");
            return (answer.Ok && f.Bool("ok", false), f.String("message"), f);
        }
        catch (Exception e) when (e is Polaris.Json.JsonError or InvalidDataException)
        {
            return (false, $"centrum odpovedalo {answer.Status}", null);
        }
    }
}
