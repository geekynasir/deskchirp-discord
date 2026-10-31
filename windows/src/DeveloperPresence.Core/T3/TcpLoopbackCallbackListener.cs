using System.Net;
using System.Net.Sockets;
using System.Text;

namespace DeveloperPresence.Core.T3;

/// <summary>
/// Loopback callback listener backed by a retained TcpListener on 127.0.0.1 port 0.
/// Avoids Windows http.sys URL ACL / administrator elevation requirements, port rebind race conditions,
/// and trailing-slash prefix mismatches.
/// </summary>
public sealed class TcpLoopbackCallbackListener : ILoopbackCallbackListener
{
    private readonly TcpListener _listener;
    private readonly int _port;
    private readonly string _callbackUri;
    private TcpClient? _activeClient;
    private bool _isDisposed;

    public const int MaxHeaderBytes = 16384; // 16 KiB header limit

    public int Port => _port;
    public string CallbackUriString => _callbackUri;

    public TcpLoopbackCallbackListener()
    {
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        _port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _callbackUri = $"http://127.0.0.1:{_port}/callback";
    }

    public static ILoopbackCallbackListener Create() => new TcpLoopbackCallbackListener();

    public async Task<OAuthCallbackRequest> WaitForCallbackAsync(TimeSpan timeout, CancellationToken ct)
    {
        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

        TcpClient client;
        try
        {
            client = await _listener.AcceptTcpClientAsync(linkedCts.Token).ConfigureAwait(false);
            _activeClient = client;
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            throw new TimeoutException("Timeout waiting for callback.");
        }

        var stream = client.GetStream();
        byte[] buffer = new byte[4096];
        using var ms = new MemoryStream();
        int totalBytes = 0;
        int headerEndIndex = -1;

        // Read headers with bounded read timeout
        client.ReceiveTimeout = 10000;

        while (headerEndIndex == -1)
        {
            linkedCts.Token.ThrowIfCancellationRequested();
            int read = await stream.ReadAsync(buffer, 0, buffer.Length, linkedCts.Token).ConfigureAwait(false);
            if (read == 0) break;

            totalBytes += read;
            if (totalBytes > MaxHeaderBytes)
            {
                throw new InvalidOperationException("Callback request headers exceeded maximum allowed size.");
            }

            ms.Write(buffer, 0, read);
            byte[] currentBytes = ms.ToArray();
            headerEndIndex = FindHeaderEnd(currentBytes);
        }

        if (headerEndIndex == -1)
        {
            throw new InvalidOperationException("Incomplete HTTP request received on callback listener.");
        }

        string rawRequest = Encoding.ASCII.GetString(ms.ToArray(), 0, headerEndIndex);
        string requestLine = rawRequest.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None)[0];
        string[] parts = requestLine.Split(' ');

        if (parts.Length < 2 || !string.Equals(parts[0], "GET", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Invalid HTTP method on callback listener; expected GET.");
        }

        string target = parts[1];
        int questionIndex = target.IndexOf('?');
        string path = questionIndex >= 0 ? target.Substring(0, questionIndex) : target;
        string queryString = questionIndex >= 0 ? target.Substring(questionIndex + 1) : string.Empty;

        // Normalize path: must match /callback
        string cleanPath = path.TrimEnd('/');
        if (!string.Equals(cleanPath, "/callback", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Invalid callback path: '{path}'. Expected '/callback'.");
        }

        // Parse query string and enforce duplicate query parameter rejection
        var queryParams = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!string.IsNullOrWhiteSpace(queryString))
        {
            string[] pairs = queryString.Split('&', StringSplitOptions.RemoveEmptyEntries);
            foreach (var pair in pairs)
            {
                string[] kv = pair.Split('=', 2);
                string key = Uri.UnescapeDataString(kv[0]);
                string val = kv.Length > 1 ? Uri.UnescapeDataString(kv[1]) : string.Empty;

                if (queryParams.ContainsKey(key))
                {
                    throw new InvalidOperationException($"Duplicate query parameter '{key}' received in callback.");
                }
                queryParams[key] = val;
            }
        }

        return new OAuthCallbackRequest
        {
            Code = queryParams.GetValueOrDefault("code"),
            State = queryParams.GetValueOrDefault("state"),
            Issuer = queryParams.GetValueOrDefault("iss"),
            Error = queryParams.GetValueOrDefault("error"),
            ErrorDescription = queryParams.GetValueOrDefault("error_description")
        };
    }

    public async Task RespondAsync(bool success, bool exchangeCompleted, CancellationToken ct = default)
    {
        var client = _activeClient;
        if (client == null || !client.Connected)
        {
            return;
        }

        string title;
        string body;

        if (success && exchangeCompleted)
        {
            title = "Deskchirp is connected to T3 Code";
            body = "You can close this tab and return to Deskchirp.";
        }
        else if (success && !exchangeCompleted)
        {
            title = "Approval received";
            body = "You can close this tab and return to Deskchirp.";
        }
        else
        {
            title = "Sign-in didn’t finish";
            body = "Return to Deskchirp and try connecting again.";
        }

        string html =
            $"<!DOCTYPE html><html><head><meta charset=\"utf-8\"><title>{title}</title></head>" +
            $"<body><h1>{title}</h1><p>{body}</p></body></html>";

        byte[] htmlBytes = Encoding.UTF8.GetBytes(html);
        int statusCode = success ? 200 : 400;
        string statusText = success ? "OK" : "Bad Request";

        string httpResponse =
            $"HTTP/1.1 {statusCode} {statusText}\r\n" +
            $"Content-Type: text/html; charset=utf-8\r\n" +
            $"Content-Length: {htmlBytes.Length}\r\n" +
            $"Cache-Control: no-store\r\n" +
            $"Connection: close\r\n" +
            $"\r\n";

        byte[] headerBytes = Encoding.ASCII.GetBytes(httpResponse);

        try
        {
            var stream = client.GetStream();
            await stream.WriteAsync(headerBytes, ct).ConfigureAwait(false);
            await stream.WriteAsync(htmlBytes, ct).ConfigureAwait(false);
            await stream.FlushAsync(ct).ConfigureAwait(false);
        }
        catch { }
        finally
        {
            try { client.Close(); } catch { }
            _activeClient = null;
        }
    }

    private static int FindHeaderEnd(byte[] bytes)
    {
        for (int i = 0; i < bytes.Length - 3; i++)
        {
            if (bytes[i] == '\r' && bytes[i + 1] == '\n' && bytes[i + 2] == '\r' && bytes[i + 3] == '\n')
            {
                return i + 4;
            }
        }
        for (int i = 0; i < bytes.Length - 1; i++)
        {
            if (bytes[i] == '\n' && bytes[i + 1] == '\n')
            {
                return i + 2;
            }
        }
        return -1;
    }

    public ValueTask DisposeAsync()
    {
        if (_isDisposed) return ValueTask.CompletedTask;
        _isDisposed = true;

        try { _activeClient?.Dispose(); } catch { }
        try { _listener.Stop(); } catch { }

        return ValueTask.CompletedTask;
    }
}
