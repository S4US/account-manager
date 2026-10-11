using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AccountManager.Core.Remote;

/// <summary>
/// Small HTTP server for the remote control panel (status, launch/stop, close all, scheduled shutdown).
///
/// Security model:
/// <list type="bullet">
/// <item>Listens on 127.0.0.1 only unless LAN access is explicitly enabled.</item>
/// <item>Every API call requires the 256-bit access token as a Bearer token; comparison is constant-time and
/// repeated failures from one address are throttled.</item>
/// <item>No CORS headers: other websites in the browser cannot read responses. The Host header must be an IP
/// literal or localhost, which defeats DNS-rebinding attacks.</item>
/// <item>Static files are compiled into the app; nothing is read from disk.</item>
/// </list>
/// </summary>
public sealed partial class RemoteServer : IDisposable
{
    private const int MaxHeaderBytes = 16 * 1024;
    private const int MaxBodyBytes = 16 * 1024;
    private const int MaxFailuresPerMinute = 10;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly IRemoteController _controller;
    private readonly byte[] _token;
    private readonly ConcurrentDictionary<IPAddress, (int Count, DateTime Window)> _failures = new();
    private readonly Dictionary<string, (byte[] Body, string Type)> _assets;
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;

    public RemoteServer(IRemoteController controller, string token)
    {
        if (string.IsNullOrEmpty(token) || token.Length < 32)
        {
            throw new ArgumentException("The access token must be at least 32 characters.", nameof(token));
        }

        _controller = controller;
        _token = Encoding.UTF8.GetBytes(token);
        _assets = LoadAssets();
    }

    public int Port { get; private set; }

    public bool AllowLan { get; private set; }

    public bool IsRunning => _listener is not null;

    public static string GenerateToken() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();

    public void Start(int port, bool allowLan)
    {
        Stop();
        var listener = new TcpListener(allowLan ? IPAddress.Any : IPAddress.Loopback, port);
        listener.Start();
        _listener = listener;
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        AllowLan = allowLan;
        _cts = new CancellationTokenSource();
        _ = AcceptLoopAsync(listener, _cts.Token);
    }

    public void Stop()
    {
        _cts?.Cancel();
        _listener?.Stop();
        _listener = null;
        _cts = null;
    }

    public void Dispose() => Stop();

    /// <summary>The local network addresses the panel can be reached on when LAN access is on.</summary>
    public static IReadOnlyList<string> GetLanAddresses()
    {
        try
        {
            return Dns.GetHostAddresses(Dns.GetHostName())
                .Where(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a))
                .Select(a => a.ToString())
                .ToList();
        }
        catch (SocketException)
        {
            return [];
        }
    }

    private async Task AcceptLoopAsync(TcpListener listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                return;
            }

            _ = HandleClientAsync(client, ct);
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
    {
        using (client)
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(10));
                NetworkStream stream = client.GetStream();
                IPAddress remote = ((IPEndPoint)client.Client.RemoteEndPoint!).Address;
                Request? request = await ReadRequestAsync(stream, timeout.Token).ConfigureAwait(false);
                Response response = request is null ? Response.Text(400, "Bad request") : Handle(request, remote);
                await WriteResponseAsync(stream, response, timeout.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or SocketException or ObjectDisposedException)
            {
            }
        }
    }

    internal Response Handle(Request request, IPAddress remote)
    {
        if (!IsAllowedHost(request.Host))
        {
            return Response.Text(421, "Misdirected request");
        }

        string path = request.Path;
        if (request.Method == "GET" && !path.StartsWith("/api/", StringComparison.Ordinal))
        {
            string asset = path == "/" ? "index.html" : path.TrimStart('/');
            return _assets.TryGetValue(asset, out (byte[] Body, string Type) file)
                ? new Response(200, file.Type, file.Body)
                : Response.Text(404, "Not found");
        }

        if (IsThrottled(remote))
        {
            return Response.Text(429, "Too many failed attempts. Wait a minute.");
        }

        if (!IsAuthorized(request))
        {
            RecordFailure(remote);
            return Response.JsonBody(401, new { error = "Invalid or missing access token." });
        }

        try
        {
            return Route(request);
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or FormatException)
        {
            return Response.JsonBody(400, new { error = ex.Message });
        }
    }

    private Response Route(Request request)
    {
        if (request.Method == "GET" && request.Path == "/api/status")
        {
            return Response.JsonBody(200, _controller.GetStatus());
        }

        if (request.Method != "POST")
        {
            return Response.JsonBody(405, new { error = "Method not allowed." });
        }

        Match account = AccountActionRegex().Match(request.Path);
        if (account.Success)
        {
            long id = long.Parse(account.Groups[1].Value, CultureInfo.InvariantCulture);
            bool ok = account.Groups[2].Value switch
            {
                "launch" => _controller.Launch(id),
                "stop" => _controller.Stop(id),
                "restart" => _controller.Restart(id),
                _ => false,
            };
            return ok ? Response.JsonBody(200, new { ok }) : Response.JsonBody(404, new { error = "Unknown account." });
        }

        switch (request.Path)
        {
            case "/api/close-all":
                return Response.JsonBody(200, new { closed = _controller.CloseAll() });
            case "/api/shutdown":
                ShutdownBody body = JsonSerializer.Deserialize<ShutdownBody>(request.Body.Length == 0 ? "{}" : request.Body, Json) ?? new ShutdownBody();
                if (body.Cancel)
                {
                    _controller.CancelSchedule();
                }
                else if (body.Minutes is >= 1 and <= 2880)
                {
                    _controller.ScheduleShutdown(body.Minutes);
                }
                else
                {
                    return Response.JsonBody(400, new { error = "minutes must be between 1 and 2880." });
                }

                return Response.JsonBody(200, new { ok = true });
            default:
                return Response.JsonBody(404, new { error = "Not found." });
        }
    }

    private bool IsAuthorized(Request request)
    {
        if (!request.Headers.TryGetValue("authorization", out string? header) || !header.StartsWith("Bearer ", StringComparison.Ordinal))
        {
            return false;
        }

        byte[] presented = Encoding.UTF8.GetBytes(header["Bearer ".Length..].Trim());
        return CryptographicOperations.FixedTimeEquals(presented, _token);
    }

    /// <summary>Only IP literals and localhost are accepted, so a hostile DNS name can't be pointed at this server.</summary>
    internal static bool IsAllowedHost(string? host)
    {
        if (string.IsNullOrEmpty(host))
        {
            return false;
        }

        string name = host;
        if (name.StartsWith('['))
        {
            int end = name.IndexOf(']');
            name = end > 0 ? name[1..end] : name;
        }
        else
        {
            int colon = name.LastIndexOf(':');
            if (colon > 0)
            {
                name = name[..colon];
            }
        }

        return name.Equals("localhost", StringComparison.OrdinalIgnoreCase) || IPAddress.TryParse(name, out _);
    }

    private bool IsThrottled(IPAddress remote) =>
        _failures.TryGetValue(remote, out (int Count, DateTime Window) entry)
        && DateTime.UtcNow - entry.Window < TimeSpan.FromMinutes(1)
        && entry.Count >= MaxFailuresPerMinute;

    private void RecordFailure(IPAddress remote)
    {
        _failures.AddOrUpdate(
            remote,
            _ => (1, DateTime.UtcNow),
            (_, e) => DateTime.UtcNow - e.Window > TimeSpan.FromMinutes(1) ? (1, DateTime.UtcNow) : (e.Count + 1, e.Window));
    }

    private static async Task<Request?> ReadRequestAsync(NetworkStream stream, CancellationToken ct)
    {
        var buffer = new byte[MaxHeaderBytes + MaxBodyBytes];
        int read = 0;
        int headerEnd = -1;
        while (headerEnd < 0)
        {
            if (read >= MaxHeaderBytes)
            {
                return null;
            }

            int n = await stream.ReadAsync(buffer.AsMemory(read, MaxHeaderBytes - read), ct).ConfigureAwait(false);
            if (n == 0)
            {
                return null;
            }

            read += n;
            headerEnd = IndexOfHeaderEnd(buffer, read);
        }

        string head = Encoding.ASCII.GetString(buffer, 0, headerEnd);
        string[] lines = head.Split("\r\n");
        string[] requestLine = lines[0].Split(' ');
        if (requestLine.Length != 3)
        {
            return null;
        }

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string line in lines.Skip(1))
        {
            int colon = line.IndexOf(':');
            if (colon > 0)
            {
                headers[line[..colon].Trim()] = line[(colon + 1)..].Trim();
            }
        }

        int bodyStart = headerEnd + 4;
        string body;
        if (headers.TryGetValue("transfer-encoding", out string? encoding) && encoding.Contains("chunked", StringComparison.OrdinalIgnoreCase))
        {
            // Read until the terminating zero-length chunk, then decode.
            while (IndexOf(buffer, bodyStart, read, "0\r\n\r\n"u8) < 0)
            {
                if (read >= buffer.Length)
                {
                    return null;
                }

                int n = await stream.ReadAsync(buffer.AsMemory(read, buffer.Length - read), ct).ConfigureAwait(false);
                if (n == 0)
                {
                    return null;
                }

                read += n;
            }

            string? decoded = DecodeChunked(buffer.AsSpan(bodyStart, read - bodyStart));
            if (decoded is null)
            {
                return null;
            }

            body = decoded;
        }
        else
        {
            int contentLength = headers.TryGetValue("content-length", out string? lengthText) && int.TryParse(lengthText, out int length) ? length : 0;
            if (contentLength < 0 || contentLength > MaxBodyBytes)
            {
                return null;
            }

            while (read - bodyStart < contentLength)
            {
                int n = await stream.ReadAsync(buffer.AsMemory(read, bodyStart + contentLength - read), ct).ConfigureAwait(false);
                if (n == 0)
                {
                    return null;
                }

                read += n;
            }

            body = Encoding.UTF8.GetString(buffer, bodyStart, contentLength);
        }

        string target = requestLine[1];
        int query = target.IndexOf('?');
        string path = Uri.UnescapeDataString(query >= 0 ? target[..query] : target);
        headers.TryGetValue("host", out string? host);
        return new Request(requestLine[0].ToUpperInvariant(), path, host, headers, body);
    }

    internal static string? DecodeChunked(ReadOnlySpan<byte> data)
    {
        var output = new MemoryStream();
        int position = 0;
        while (position < data.Length)
        {
            int lineEnd = data[position..].IndexOf("\r\n"u8);
            if (lineEnd < 0)
            {
                return null;
            }

            string sizeText = Encoding.ASCII.GetString(data.Slice(position, lineEnd)).Split(';')[0].Trim();
            if (!int.TryParse(sizeText, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int size) || size < 0 || size > MaxBodyBytes)
            {
                return null;
            }

            position += lineEnd + 2;
            if (size == 0)
            {
                return Encoding.UTF8.GetString(output.ToArray());
            }

            if (position + size + 2 > data.Length || output.Length + size > MaxBodyBytes)
            {
                return null;
            }

            output.Write(data.Slice(position, size));
            position += size + 2;
        }

        return null;
    }

    private static int IndexOf(byte[] buffer, int start, int end, ReadOnlySpan<byte> value) =>
        end <= start ? -1 : buffer.AsSpan(start, end - start).IndexOf(value);

    private static int IndexOfHeaderEnd(byte[] buffer, int length)
    {
        for (int i = 3; i < length; i++)
        {
            if (buffer[i - 3] == '\r' && buffer[i - 2] == '\n' && buffer[i - 1] == '\r' && buffer[i] == '\n')
            {
                return i - 3;
            }
        }

        return -1;
    }

    private static async Task WriteResponseAsync(NetworkStream stream, Response response, CancellationToken ct)
    {
        var head = new StringBuilder();
        head.Append(CultureInfo.InvariantCulture, $"HTTP/1.1 {response.Status} {ReasonPhrase(response.Status)}\r\n");
        head.Append(CultureInfo.InvariantCulture, $"Content-Type: {response.ContentType}\r\n");
        head.Append(CultureInfo.InvariantCulture, $"Content-Length: {response.Body.Length}\r\n");
        head.Append("Cache-Control: no-store\r\n");
        head.Append("X-Content-Type-Options: nosniff\r\n");
        head.Append("X-Frame-Options: DENY\r\n");
        head.Append("Referrer-Policy: no-referrer\r\n");
        head.Append("Content-Security-Policy: default-src 'self'; img-src 'self' https://*.rbxcdn.com data:; frame-ancestors 'none'\r\n");
        head.Append("Connection: close\r\n\r\n");
        await stream.WriteAsync(Encoding.ASCII.GetBytes(head.ToString()), ct).ConfigureAwait(false);
        await stream.WriteAsync(response.Body, ct).ConfigureAwait(false);
    }

    private static string ReasonPhrase(int status) => status switch
    {
        200 => "OK",
        400 => "Bad Request",
        401 => "Unauthorized",
        404 => "Not Found",
        405 => "Method Not Allowed",
        421 => "Misdirected Request",
        429 => "Too Many Requests",
        _ => "Error",
    };

    private static Dictionary<string, (byte[], string)> LoadAssets()
    {
        var assets = new Dictionary<string, (byte[], string)>(StringComparer.Ordinal);
        Assembly assembly = typeof(RemoteServer).Assembly;
        foreach (string name in assembly.GetManifestResourceNames())
        {
            const string prefix = "AccountManager.Core.Remote.Web.";
            if (!name.StartsWith(prefix, StringComparison.Ordinal))
            {
                continue;
            }

            string file = name[prefix.Length..];
            using Stream stream = assembly.GetManifestResourceStream(name)!;
            using var memory = new MemoryStream();
            stream.CopyTo(memory);
            string type = Path.GetExtension(file) switch
            {
                ".html" => "text/html; charset=utf-8",
                ".css" => "text/css; charset=utf-8",
                ".js" => "text/javascript; charset=utf-8",
                ".svg" => "image/svg+xml",
                _ => "application/octet-stream",
            };
            assets[file] = (memory.ToArray(), type);
        }

        return assets;
    }

    [GeneratedRegex(@"^/api/accounts/(\d{1,18})/(launch|stop|restart)$")]
    private static partial Regex AccountActionRegex();

    internal sealed record Request(string Method, string Path, string? Host, Dictionary<string, string> Headers, string Body);

    internal sealed record Response(int Status, string ContentType, byte[] Body)
    {
        public static Response Text(int status, string text) => new(status, "text/plain; charset=utf-8", Encoding.UTF8.GetBytes(text));

        public static Response JsonBody(int status, object value) =>
            new(status, "application/json; charset=utf-8", JsonSerializer.SerializeToUtf8Bytes(value, Json));
    }

    private sealed class ShutdownBody
    {
        public int Minutes { get; set; }

        public bool Cancel { get; set; }
    }
}
