using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace Polaris.Web;

/// <summary>A request as the server understood it. The path is already checked: it starts with '/', has no "..", no
/// backslash, no percent-escapes and no query.</summary>
public sealed record ServerRequest(string Method, string Path, IReadOnlyDictionary<string, string> Headers, IPAddress Remote, byte[]? Body = null)
{
    public string? Header(string name) => Headers.TryGetValue(name.ToLowerInvariant(), out var v) ? v : null;
}

/// <summary>An answer: bytes, or a file sent in pieces (installers are big). The ETag lets a client ask "only if it
/// changed" and get 304 without the body.</summary>
public sealed class ServerResponse
{
    public int Status { get; init; } = 200;

    public string ContentType { get; init; } = "application/json; charset=utf-8";

    public byte[]? Body { get; init; }

    public string? FilePath { get; init; }

    public string? ETag { get; init; }

    public static ServerResponse Text(int status, string text) =>
        new() { Status = status, ContentType = "text/plain; charset=utf-8", Body = Encoding.UTF8.GetBytes(text + "\n") };

    public static ServerResponse Json(string json, string? etag = null) => new() { Body = Encoding.UTF8.GetBytes(json), ETag = etag };

    public static ServerResponse File(string path, string? etag = null) => new() { ContentType = "application/octet-stream", FilePath = path, ETag = etag };

    /// <summary>A page or a script of the console: only from this server, never in a frame of another page.</summary>
    public static ServerResponse Page(string contentType, string text) => new() { ContentType = contentType, Body = Encoding.UTF8.GetBytes(text), Strict = true };

    /// <summary>Strict headers for pages: content only from this server, no frames, no referrer.</summary>
    public bool Strict { get; init; }
}

/// <summary>The centre's own web server: TLS 1.2/1.3 by the system, HTTP/1.1, one request per connection, read-only.
/// Every connection has a time limit and a size limit for its head; at most <see cref="MaxConnections"/> at once.</summary>
public sealed class WebServer(IPEndPoint endpoint, X509Certificate2? certificate, Func<ServerRequest, ServerResponse> handler, Action<string> log, Func<string, bool>? acceptsBody = null)
{
    /// <summary>Without a certificate the server speaks plain HTTP – only on the loopback address (the console on the
    /// administrator's own PC), never on the network.</summary>
    private readonly bool _plain = certificate is null
        ? IPAddress.IsLoopback(endpoint.Address) ? true : throw new ArgumentException("Bez certifikátu len na 127.0.0.1.", nameof(certificate))
        : false;

    public const int MaxHead = 8 * 1024;

    /// <summary>The only requests with a body are uploads to the paths <c>acceptsBody</c> names, never bigger than this.</summary>
    public const int MaxBody = 16 * 1024 * 1024;
    public static readonly TimeSpan BodyLimit = TimeSpan.FromSeconds(60);
    public const int MaxConnections = 64;
    public static readonly TimeSpan HeadLimit = TimeSpan.FromSeconds(15);

    private readonly SemaphoreSlim _slots = new(MaxConnections);

    /// <summary>The port really used (0 in the endpoint = any free one, for tests).</summary>
    public int Port { get; private set; }

    public async Task RunAsync(CancellationToken stop, TaskCompletionSource? started = null)
    {
        TcpListener listener;
        try
        {
            listener = new TcpListener(endpoint);
            if (endpoint.Address.Equals(IPAddress.IPv6Any))
            {
                listener.Server.DualMode = true; // IPv4 and IPv6 on one port
            }
        }
        catch (SocketException) when (endpoint.Address.Equals(IPAddress.IPv6Any))
        {
            // a server with IPv6 switched off (Debian with ipv6.disable=1): IPv4 alone
            listener = new TcpListener(new IPEndPoint(IPAddress.Any, endpoint.Port));
        }

        using var listening = listener;

        listener.Start(MaxConnections);
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        started?.TrySetResult();
        try
        {
            while (!stop.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(stop).ConfigureAwait(false);
                if (!_slots.Wait(0))
                {
                    client.Dispose(); // too many at once: refuse rather than queue without end
                    continue;
                }

                _ = Task.Run(() => ServeAsync(client, stop), CancellationToken.None);
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            listener.Stop();
        }
    }

    private async Task ServeAsync(TcpClient client, CancellationToken stop)
    {
        var remote = (client.Client.RemoteEndPoint as IPEndPoint)?.Address ?? IPAddress.None;
        try
        {
            using (client)
            using (var limit = CancellationTokenSource.CreateLinkedTokenSource(stop))
            {
                limit.CancelAfter(HeadLimit);
                client.NoDelay = true;
                Stream tls = client.GetStream();
                if (!_plain)
                {
                    var secure = new SslStream(tls, leaveInnerStreamOpen: false);
                    tls = secure;
                    await secure.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                    {
                        ServerCertificate = certificate,
                        EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                        ClientCertificateRequired = false,
                        CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                    }, limit.Token).ConfigureAwait(false);
                }

                await using var owned = tls;

                var head = await ReadHeadAsync(tls, limit.Token).ConfigureAwait(false);
                var (request, refusal) = Parse(head, remote, acceptsBody);
                if (request is { Method: "POST" })
                {
                    limit.CancelAfter(BodyLimit);
                    var length = int.Parse(request.Header("content-length")!, System.Globalization.CultureInfo.InvariantCulture);
                    var body = new byte[length];
                    await tls.ReadExactlyAsync(body, limit.Token).ConfigureAwait(false);
                    request = request with { Body = body };
                }

                var response = refusal ?? Answer(request!);
                await WriteAsync(tls, request, response, stop).ConfigureAwait(false);
            }
        }
        catch (Exception e) when (e is IOException or AuthenticationException or OperationCanceledException or SocketException or ObjectDisposedException)
        {
            // a client that went away, spoke no TLS or was too slow; nothing to answer
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            log($"{remote}: {e.GetType().Name}: {e.Message}");
        }
        finally
        {
            _slots.Release();
        }
    }

    private ServerResponse Answer(ServerRequest request)
    {
        try
        {
            var response = handler(request);
            if (response.ETag is { } etag && response.Status == 200 && request.Header("if-none-match") == etag)
            {
                return new ServerResponse { Status = 304, ETag = etag, Body = [] };
            }

            return response;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            log($"{request.Remote} {request.Path}: {e.Message}");
            return ServerResponse.Text(500, "Centrum túto odpoveď teraz nevie pripraviť.");
        }
    }

    /// <summary>The head of the request: up to the empty line, never more than <see cref="MaxHead"/> bytes.</summary>
    private static async Task<string?> ReadHeadAsync(Stream stream, CancellationToken token)
    {
        var buffer = new byte[MaxHead];
        var length = 0;
        while (length < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(length, 1), token).ConfigureAwait(false);
            if (read == 0)
            {
                return null;
            }

            length++;
            if (length >= 4 && buffer[length - 4] == '\r' && buffer[length - 3] == '\n' && buffer[length - 2] == '\r' && buffer[length - 1] == '\n')
            {
                return Encoding.ASCII.GetString(buffer, 0, length - 4);
            }
        }

        return null; // too long
    }

    public static (ServerRequest? Request, ServerResponse? Refusal) Parse(string? head, IPAddress remote, Func<string, bool>? acceptsBody = null)
    {
        if (head is null)
        {
            return (null, ServerResponse.Text(400, "Neplatná požiadavka."));
        }

        var lines = head.Split("\r\n");
        var first = lines[0].Split(' ');
        if (first.Length != 3 || !first[2].StartsWith("HTTP/1.", StringComparison.Ordinal))
        {
            return (null, ServerResponse.Text(400, "Neplatná požiadavka."));
        }

        var path = first[1];
        if (!path.StartsWith('/') || path.Contains("..", StringComparison.Ordinal) || path.IndexOfAny(['\\', '%', '?', '#', '\0']) >= 0 || path.Length > 512)
        {
            return (null, ServerResponse.Text(400, "Neplatná cesta."));
        }

        var upload = first[0] == "POST" && acceptsBody?.Invoke(path) == true;
        if (first[0] is not ("GET" or "HEAD") && !upload)
        {
            return (null, ServerResponse.Text(405, "Centrum tu len odpovedá, nič neprijíma."));
        }

        var headers = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in lines.Skip(1))
        {
            var colon = line.IndexOf(':');
            if (colon <= 0)
            {
                return (null, ServerResponse.Text(400, "Neplatná hlavička."));
            }

            headers[line[..colon].Trim().ToLowerInvariant()] = line[(colon + 1)..].Trim();
        }

        if (upload && !(headers.TryGetValue("content-length", out var lengthText) && int.TryParse(lengthText, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var length) && length is > 0 and <= MaxBody))
        {
            return (null, ServerResponse.Text(413, $"Odovzdanie musí mať dĺžku 1 až {MaxBody} bajtov."));
        }

        if (headers.ContainsKey("transfer-encoding"))
        {
            return (null, ServerResponse.Text(400, "Kúskové posielanie centrum neprijíma."));
        }

        return (new ServerRequest(first[0], path, headers, remote), null);
    }

    private static async Task WriteAsync(Stream stream, ServerRequest? request, ServerResponse response, CancellationToken stop)
    {
        long length = response.FilePath is { } file ? new FileInfo(file).Length : response.Body?.Length ?? 0;
        var head = new StringBuilder()
            .Append("HTTP/1.1 ").Append(response.Status).Append(' ').Append(Reason(response.Status)).Append("\r\n")
            .Append("Content-Type: ").Append(response.ContentType).Append("\r\n")
            .Append("Content-Length: ").Append(response.Status == 304 ? 0 : length).Append("\r\n")
            .Append("Cache-Control: no-cache\r\n")
            .Append("X-Content-Type-Options: nosniff\r\n")
            .Append("Connection: close\r\n");
        if (response.ETag is { } etag)
        {
            head.Append("ETag: ").Append(etag).Append("\r\n");
        }

        if (response.Strict)
        {
            head.Append("Content-Security-Policy: default-src 'self'; frame-ancestors 'none'; base-uri 'none'; form-action 'none'\r\n")
                .Append("X-Frame-Options: DENY\r\n")
                .Append("Referrer-Policy: no-referrer\r\n");
        }

        head.Append("\r\n");
        await stream.WriteAsync(Encoding.ASCII.GetBytes(head.ToString()), stop).ConfigureAwait(false);
        if (request?.Method != "HEAD" && response.Status != 304)
        {
            if (response.FilePath is { } path)
            {
                await using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, useAsync: true);
                await source.CopyToAsync(stream, 64 * 1024, stop).ConfigureAwait(false);
            }
            else if (response.Body is { Length: > 0 } body)
            {
                await stream.WriteAsync(body, stop).ConfigureAwait(false);
            }
        }

        await stream.FlushAsync(stop).ConfigureAwait(false);
    }

    private static string Reason(int status) => status switch
    {
        200 => "OK",
        304 => "Not Modified",
        400 => "Bad Request",
        404 => "Not Found",
        405 => "Method Not Allowed",
        403 => "Forbidden",
        409 => "Conflict",
        413 => "Content Too Large",
        _ => "Error",
    };
}
