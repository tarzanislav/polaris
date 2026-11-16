using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace Polaris.Web;

public sealed record ClientResult(int Status, byte[] Body, string? ETag, string? Error = null)
{
    public bool Ok => Status == 200;

    public bool NotModified => Status == 304;
}

/// <summary>The agent's side: one GET over TLS to the centre. The centre is recognised by the pinned SHA-256 of its public
/// key – a certificate authority is not needed and not trusted; any other key ends the connection before a byte is
/// sent. Answers bigger than allowed are refused.</summary>
public static class PinnedClient
{
    /// <summary>The pin of a certificate: SHA-256 of its public key (SubjectPublicKeyInfo), small hex letters.</summary>
    public static string Pin(X509Certificate2 certificate) =>
        Convert.ToHexStringLower(SHA256.HashData(certificate.PublicKey.ExportSubjectPublicKeyInfo()));

    public static ClientResult Get(Uri address, string pin, string? etag, long maxBytes, TimeSpan limit, string? saveTo = null) =>
        Send(address, pin, limit, token => GetAsync(address, pin, etag, maxBytes, saveTo, null, null, token));

    /// <summary>An upload: the body and its own headers (e.g. the console's signature). The answer is read like a GET.</summary>
    public static ClientResult Post(Uri address, string pin, byte[] body, IReadOnlyDictionary<string, string> headers, long maxAnswer, TimeSpan limit) =>
        Send(address, pin, limit, token => GetAsync(address, pin, null, maxAnswer, null, body, headers, token));

    private static ClientResult Send(Uri address, string pin, TimeSpan limit, Func<CancellationToken, Task<ClientResult>> call)
    {
        using var stop = new CancellationTokenSource(limit);
        try
        {
            return call(stop.Token).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            return new ClientResult(0, [], null, "centrum neodpovedalo včas");
        }
        catch (AuthenticationException e)
        {
            return new ClientResult(0, [], null, "spojenie s centrom nie je dôveryhodné (" + e.Message + ")");
        }
        catch (Exception e) when (e is IOException or SocketException or InvalidDataException)
        {
            return new ClientResult(0, [], null, "centrum nie je dostupné (" + e.Message + ")");
        }
    }

    private static async Task<ClientResult> GetAsync(Uri address, string pin, string? etag, long maxBytes, string? saveTo, byte[]? upload, IReadOnlyDictionary<string, string>? extra, CancellationToken token)
    {
        if (address.Scheme != Uri.UriSchemeHttps)
        {
            throw new InvalidDataException("adresa centra musí začínať https://");
        }

        using var tcp = new TcpClient();
        await tcp.ConnectAsync(address.Host, address.Port, token).ConfigureAwait(false);
        await using var tls = new SslStream(tcp.GetStream(), false, (_, certificate, _, _) =>
            certificate is not null && string.Equals(Pin(new X509Certificate2(certificate)), pin, StringComparison.OrdinalIgnoreCase));
        await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
        {
            TargetHost = address.Host,
            EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
            CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
        }, token).ConfigureAwait(false);

        var head = new StringBuilder()
            .Append(upload is null ? "GET " : "POST ").Append(address.AbsolutePath).Append(" HTTP/1.1\r\n")
            .Append("Host: ").Append(address.Authority).Append("\r\n")
            .Append("User-Agent: POLARIS\r\n")
            .Append("Connection: close\r\n");
        if (etag is not null)
        {
            head.Append("If-None-Match: ").Append(etag).Append("\r\n");
        }

        foreach (var (name, value) in extra ?? new Dictionary<string, string>())
        {
            if (name.Any(c => c is '\r' or '\n' or ':') || value.Any(c => c is '\r' or '\n'))
            {
                throw new InvalidDataException("neplatná hlavička");
            }

            head.Append(name).Append(": ").Append(value).Append("\r\n");
        }

        if (upload is not null)
        {
            head.Append("Content-Type: application/json\r\nContent-Length: ").Append(upload.Length).Append("\r\n");
        }

        head.Append("\r\n");
        await tls.WriteAsync(Encoding.ASCII.GetBytes(head.ToString()), token).ConfigureAwait(false);
        if (upload is not null)
        {
            await tls.WriteAsync(upload, token).ConfigureAwait(false);
        }


        var (status, headers) = await ReadHeadAsync(tls, token).ConfigureAwait(false);
        headers.TryGetValue("etag", out var newTag);
        if (!headers.TryGetValue("content-length", out var lengthText) || !long.TryParse(lengthText, out var length) || length < 0)
        {
            throw new InvalidDataException("odpoveď centra nemá dĺžku");
        }

        if (length > maxBytes)
        {
            throw new InvalidDataException($"odpoveď centra je väčšia ako {maxBytes} bajtov");
        }

        if (saveTo is not null && status == 200)
        {
            await using var file = new FileStream(saveTo, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024, useAsync: true);
            await CopyExactlyAsync(tls, file, length, token).ConfigureAwait(false);
            return new ClientResult(status, [], newTag);
        }

        using var body = new MemoryStream();
        await CopyExactlyAsync(tls, body, length, token).ConfigureAwait(false);
        return new ClientResult(status, body.ToArray(), newTag);
    }

    private static async Task CopyExactlyAsync(Stream from, Stream to, long length, CancellationToken token)
    {
        var buffer = new byte[64 * 1024];
        while (length > 0)
        {
            var read = await from.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, length)), token).ConfigureAwait(false);
            if (read == 0)
            {
                throw new IOException("spojenie sa prerušilo uprostred odpovede");
            }

            await to.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
            length -= read;
        }
    }

    private static async Task<(int Status, Dictionary<string, string> Headers)> ReadHeadAsync(Stream stream, CancellationToken token)
    {
        var bytes = new List<byte>(512);
        var one = new byte[1];
        while (true)
        {
            if (await stream.ReadAsync(one, token).ConfigureAwait(false) == 0)
            {
                throw new IOException("centrum zavrelo spojenie bez odpovede");
            }

            bytes.Add(one[0]);
            if (bytes.Count > WebServer.MaxHead)
            {
                throw new InvalidDataException("hlavička odpovede je príliš dlhá");
            }

            var n = bytes.Count;
            if (n >= 4 && bytes[n - 4] == '\r' && bytes[n - 3] == '\n' && bytes[n - 2] == '\r' && bytes[n - 1] == '\n')
            {
                break;
            }
        }

        var lines = Encoding.ASCII.GetString([.. bytes]).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        var first = lines[0].Split(' ');
        if (first.Length < 2 || !int.TryParse(first[1], out var status))
        {
            throw new InvalidDataException("neplatná odpoveď centra");
        }

        var headers = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in lines.Skip(1))
        {
            var colon = line.IndexOf(':');
            if (colon > 0)
            {
                headers[line[..colon].Trim().ToLowerInvariant()] = line[(colon + 1)..].Trim();
            }
        }

        return (status, headers);
    }
}
