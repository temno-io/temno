using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Temno.Core.Account;

/// <summary>
/// Loopback HTTPS-free callback bound to 127.0.0.1 only. The first matching GET spends the
/// listener. Query strings are not logged. Provider tokens are never written to the HTML page.
/// </summary>
public sealed class LoopbackCallbackServer : IDisposable
{
    /// <summary>Frame 05 of the sign-in page design (WP-353): "You’re signed in". Self-contained: no network, no script.</summary>
    public const string SuccessHtml = LoopbackPages.Success;

    /// <summary>Frame R2: "Sign-in didn’t finish".</summary>
    public const string FailureHtml = LoopbackPages.Failure;

    /// <summary>
    /// The address carries the authorization code, so nothing on the page may hand it onward (no-referrer), and the page
    /// may load nothing: inline styles and inline SVG only.
    /// </summary>
    internal const string SecurityHeaders =
        "Content-Security-Policy: default-src 'none'; style-src 'unsafe-inline'; img-src data:; base-uri 'none'; form-action 'none'; frame-ancestors 'none'\r\n"
        + "Referrer-Policy: no-referrer\r\nX-Content-Type-Options: nosniff\r\n";

    private TcpListener? _listener;

    public int Port { get; }

    public LoopbackCallbackServer(int port = HostedEndpoints.CallbackPort)
    {
        _listener = new TcpListener(IPAddress.Loopback, port);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
    }

    public async Task<CallbackResult> WaitAsync(
        string expectedPath,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedPath);
        if (_listener is null)
            throw new ObjectDisposedException(nameof(LoopbackCallbackServer));

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linked.CancelAfter(timeout);
        try
        {
            while (!linked.Token.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(linked.Token).ConfigureAwait(false);
                using (client)
                {
                    var result = await ReadRequestAsync(client, expectedPath, linked.Token).ConfigureAwait(false);
                    if (result is null)
                        continue;
                    Stop();
                    return result.Value;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new CallbackResult(CallbackKind.Cancelled, null, "cancelled");
        }
        catch (OperationCanceledException)
        {
            return new CallbackResult(CallbackKind.Expired, null, "expired");
        }

        return new CallbackResult(CallbackKind.Expired, null, "expired");
    }

    public void Dispose() => Stop();

    public void Stop()
    {
        try { _listener?.Stop(); }
        catch { /* already stopped */ }
        _listener = null;
    }

    private static async Task<CallbackResult?> ReadRequestAsync(
        TcpClient client,
        string expectedPath,
        CancellationToken cancellationToken)
    {
        using var stream = client.GetStream();
        using var reader = new StreamReader(stream, Encoding.ASCII, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
        var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(line) || !line.StartsWith("GET ", StringComparison.Ordinal))
        {
            await WriteHtmlAsync(stream, FailureHtml, 400, cancellationToken).ConfigureAwait(false);
            return null;
        }

        var target = line.Split(' ')[1];
        if (!Uri.TryCreate("http://127.0.0.1" + target, UriKind.Absolute, out var uri))
        {
            await WriteHtmlAsync(stream, FailureHtml, 400, cancellationToken).ConfigureAwait(false);
            return null;
        }

        if (!string.Equals(uri.AbsolutePath, expectedPath, StringComparison.OrdinalIgnoreCase))
        {
            await WriteHtmlAsync(stream, FailureHtml, 404, cancellationToken).ConfigureAwait(false);
            return null;
        }

        var error = QueryValue(uri.Query, "error");
        var code = QueryValue(uri.Query, "code");
        if (!string.IsNullOrWhiteSpace(error))
        {
            await WriteHtmlAsync(stream, FailureHtml, 400, cancellationToken).ConfigureAwait(false);
            return new CallbackResult(CallbackKind.Rejected, null, "provider_error");
        }

        if (string.IsNullOrWhiteSpace(code))
        {
            await WriteHtmlAsync(stream, FailureHtml, 400, cancellationToken).ConfigureAwait(false);
            return new CallbackResult(CallbackKind.Rejected, null, "missing_code");
        }

        await WriteHtmlAsync(stream, SuccessHtml, 200, cancellationToken).ConfigureAwait(false);
        return new CallbackResult(CallbackKind.Code, code, null);
    }

    internal static string? QueryValue(string query, string name)
    {
        if (string.IsNullOrEmpty(query))
            return null;
        var trimmed = query.TrimStart('?');
        foreach (var part in trimmed.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = part.IndexOf('=');
            var key = eq < 0 ? part : part[..eq];
            if (!string.Equals(Uri.UnescapeDataString(key.Replace('+', ' ')), name, StringComparison.Ordinal))
                continue;
            return eq < 0 ? "" : Uri.UnescapeDataString(part[(eq + 1)..].Replace('+', ' '));
        }

        return null;
    }

    private static async Task WriteHtmlAsync(NetworkStream stream, string html, int status, CancellationToken cancellationToken)
    {
        var reason = status == 200 ? "OK" : "Bad Request";
        var bytes = Encoding.UTF8.GetBytes(html);
        var header =
            $"HTTP/1.1 {status} {reason}\r\nContent-Type: text/html; charset=utf-8\r\nCache-Control: no-store\r\n{SecurityHeaders}Content-Length: {bytes.Length}\r\nConnection: close\r\n\r\n";
        var headBytes = Encoding.ASCII.GetBytes(header);
        await stream.WriteAsync(headBytes, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }
}

public enum CallbackKind
{
    Code,
    Rejected,
    Expired,
    Cancelled,
}

public readonly record struct CallbackResult(CallbackKind Kind, string? Code, string? Detail);
