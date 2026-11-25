using System.Globalization;
using System.Text;
using Polaris.Net;
using Polaris.Web;

namespace Polaris.Agent;

/// <summary>The client updates itself through the centre: a new version a trusted console signed (decided 25. 11. 2026)
/// is downloaded into the agent's protected folder, checked byte by byte against the console's manifest and started
/// to take the place of this one. Nothing else is ever started: no other file, no unsigned or older version.</summary>
public sealed class ClientUpdate(CentreSettings settings, string folder, Func<IReadOnlyList<TrustedConsole>> trusted, Func<DateTimeOffset> clock)
{
    public static readonly TimeSpan RetryAfter = TimeSpan.FromHours(1);
    private static readonly TimeSpan Limit = TimeSpan.FromMinutes(5);
    private string? _etag;

    public string Folder => folder;

    private string Attempt => Path.Combine(folder, "pokus.txt");

    /// <summary>One look at the centre: the checked new program to start, and a line for the journal (empty when there
    /// is nothing new).</summary>
    public (string? Program, string Message) Look(string current)
    {
        var answer = PinnedClient.Get(settings.Of(UpdateSigning.ManifestPath(UpdateProducts.Client).TrimStart('/')), settings.Pin, _etag, 64 * 1024, CentreSync.Limit);
        if (answer.NotModified || answer.Status == 404)
        {
            return (null, "");
        }

        if (!answer.Ok)
        {
            return (null, "");
        }

        UpdateManifest m;
        try
        {
            m = UpdateManifest.FromJson(Encoding.UTF8.GetString(answer.Body));
        }
        catch (InvalidDataException e)
        {
            return (null, e.Message);
        }

        _etag = answer.ETag;
        if (m.Product != UpdateProducts.Client || !UpdateSigning.Newer(m.Version, current))
        {
            return (null, "");
        }

        if (UpdateSigning.Check(m, trusted()) is { State: not SignatureState.Valid } bad)
        {
            return (null, $"aktualizácia klienta {m.Version} odmietnutá: {bad.Message}");
        }

        if (TriedRecently(m.Sha256))
        {
            return (null, "");
        }

        Directory.CreateDirectory(folder);
        foreach (var old in Directory.GetFiles(folder, "*.exe"))
        {
            TryDelete(old);
        }

        var program = Path.Combine(folder, $"polaris-agent-{m.Sha256[..12]}.exe");
        var file = PinnedClient.Get(settings.Of(UpdateSigning.FilePath(m.Sha256).TrimStart('/')), settings.Pin, null, m.Size, Limit, program);
        if (!file.Ok)
        {
            TryDelete(program);
            return (null, $"aktualizácia klienta {m.Version}: súbor sa nedá stiahnuť ({file.Error ?? file.Status.ToString(CultureInfo.InvariantCulture)})");
        }

        using (var stream = File.OpenRead(program))
        {
            if (!UpdateSigning.Matches(m, stream))
            {
                stream.Dispose();
                TryDelete(program);
                return (null, $"aktualizácia klienta {m.Version} odmietnutá: súbor nie je ten, ktorý konzola podpísala");
            }
        }

        File.WriteAllText(Attempt, $"{m.Sha256} {clock().ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)}");
        return (program, $"aktualizácia klienta {current} → {m.Version} ({(UpdateSigning.Check(m, trusted()).Message)})");
    }

    private bool TriedRecently(string sha256)
    {
        try
        {
            var parts = File.ReadAllText(Attempt).Split(' ');
            return parts.Length == 2 && parts[0] == sha256
                && DateTimeOffset.TryParse(parts[1], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var at)
                && clock() - at < RetryAfter;
        }
        catch (IOException)
        {
            return false;
        }
    }

    private static void TryDelete(string file)
    {
        try
        {
            File.Delete(file);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }
}
