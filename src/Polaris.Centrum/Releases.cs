using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using Polaris.Json;
using Polaris.Net;

namespace Polaris.Centrum;

/// <summary>A file of a release: its name, size, where to download it and its SHA-256 (from GitHub, or from the
/// SHA256SUMS.txt of the release).</summary>
public sealed record ReleaseAsset(string Name, long Size, Uri Url, string? Sha256);

/// <summary>A release of POLARIS on GitHub: the version (from the tag v0.1.3) and its files.</summary>
public sealed record Release(string Tag, string Version, IReadOnlyList<ReleaseAsset> Assets, DateTimeOffset Published)
{
    public ReleaseAsset? Asset(string? name) => name is null ? null : Assets.FirstOrDefault(a => a.Name == name);
}

/// <summary>The newest release of POLARIS on GitHub – the only place on the internet the console asks, and only for
/// what the person at the console then approves. Through the system's own HTTPS (certificates of Windows).</summary>
public static class GitHubReleases
{
    public const string Repository = "tarzanislav/polaris";
    public const string Sums = "SHA256SUMS.txt";

    private static readonly Lazy<HttpClient> Client = new(() =>
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("POLARIS-konzola", Polaris.Core.Product.Version));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return client;
    });

    /// <summary>The newest published release, or why not.</summary>
    public static (Release? Release, string Problem) Latest()
    {
        try
        {
            var text = Client.Value.GetStringAsync($"https://api.github.com/repos/{Repository}/releases/latest").GetAwaiter().GetResult();
            return (Parse(text), "");
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or InvalidDataException or JsonError)
        {
            return (null, "GitHub sa nedá opýtať: " + e.Message);
        }
    }

    /// <summary>The answer of GitHub's API for a release (only the fields POLARIS needs; others are left alone).</summary>
    public static Release Parse(string json)
    {
        var f = new JsonFields(JsonReader.Parse(json, JsonSyntax.Strict with { MaxDepth = 64, MaxLength = 4 * 1024 * 1024 }), "vydanie");
        var tag = f.String("tag_name");
        var assets = new List<ReleaseAsset>();
        foreach (var node in f.Array("assets"))
        {
            var a = new JsonFields(node, "súbor vydania");
            var digest = a.OptionalString("digest");
            var url = a.String("browser_download_url");
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            {
                continue;
            }

            assets.Add(new ReleaseAsset(a.String("name"), a.Long("size", 0), uri, digest is { } d && d.StartsWith("sha256:", StringComparison.Ordinal) ? d[7..].ToLowerInvariant() : null));
        }

        var published = DateTimeOffset.TryParse(f.String("published_at"), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var at) ? at : DateTimeOffset.MinValue;
        return new Release(tag, tag.TrimStart('v'), assets, published);
    }

    /// <summary>Downloads a file of the release and checks it: the size, and the SHA-256 from GitHub or from SHA256SUMS.txt.</summary>
    public static byte[] Download(Release release, ReleaseAsset asset)
    {
        if (asset.Size is <= 0 or > UpdateSigning.MostBytes)
        {
            throw new InvalidDataException($"{asset.Name}: neočakávaná veľkosť {asset.Size} B");
        }

        var expected = asset.Sha256 ?? SumOf(release, asset.Name)
            ?? throw new InvalidDataException($"{asset.Name}: vydanie neuvádza odtlačok SHA-256 – nestiahnem ho");
        var bytes = Client.Value.GetByteArrayAsync(asset.Url).GetAwaiter().GetResult();
        if (bytes.LongLength != asset.Size || !string.Equals(Convert.ToHexStringLower(SHA256.HashData(bytes)), expected, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"{asset.Name}: stiahnutý súbor nesedí s vydaním (veľkosť alebo odtlačok) – zahodený");
        }

        return bytes;
    }

    private static string? SumOf(Release release, string name)
    {
        if (release.Asset(Sums) is not { } sums || sums.Size > 64 * 1024)
        {
            return null;
        }

        var text = Encoding.UTF8.GetString(Client.Value.GetByteArrayAsync(sums.Url).GetAwaiter().GetResult());
        return Sum(text, name);
    }

    /// <summary>The SHA-256 of a file in the lines of sha256sum ("HASH  NAME" or "HASH *NAME").</summary>
    public static string? Sum(string text, string name)
    {
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = line.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 2 && parts[1].TrimStart('*').Trim() == name && parts[0].Length == 64 && parts[0].All(char.IsAsciiHexDigitLower))
            {
                return parts[0];
            }
        }

        return null;
    }
}
