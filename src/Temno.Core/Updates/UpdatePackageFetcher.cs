namespace Temno.Core.Updates;

/// <summary>
/// Copies a remote package into a directory. A local file is returned as-is and never fetched.
/// </summary>
public sealed class UpdatePackageFetcher
{
    public const long DefaultMaxBytes = 4L * 1024 * 1024 * 1024;

    private readonly HttpMessageHandler? _handler;
    private readonly long _maxBytes;

    public UpdatePackageFetcher(HttpMessageHandler? handler = null, long? maxBytes = null)
    {
        _handler = handler;
        _maxBytes = maxBytes ?? DefaultMaxBytes;
    }

    public async Task<string> MaterializeAsync(
        UpdateInstallPlan plan,
        string destinationDirectory,
        CancellationToken cancellationToken = default)
    {
        if (plan.LocalPath is not null)
        {
            if (!File.Exists(plan.LocalPath))
                throw new FileNotFoundException("The update package file was not found.");
            return plan.LocalPath;
        }

        if (plan.Source.Scheme != Uri.UriSchemeHttps)
            throw new InvalidOperationException(UpdateChannelCopy.HttpsOnly);

        Directory.CreateDirectory(destinationDirectory);
        // The name is ours, never the URL's: the file's path is handed to Windows PowerShell.
        var destination = Path.Combine(destinationDirectory, Guid.NewGuid().ToString("N") + ".msix");
        using var client = CreateClient();
        using var response = await client.GetAsync(plan.Source, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        if (response.RequestMessage?.RequestUri is { } final && final.Scheme != Uri.UriSchemeHttps)
            throw new InvalidOperationException(UpdateChannelCopy.HttpsOnly);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Could not download the update: HTTP {(int)response.StatusCode}.");

        var declared = response.Content.Headers.ContentLength;
        if (declared > _maxBytes)
            throw new InvalidOperationException("Could not download the update: the package is larger than 4 GB.");

        var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        var failed = false;
        try
        {
            var buffer = new byte[81920];
            long total = 0;
            while (true)
            {
                var read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (read == 0)
                    break;
                total += read;
                if (total > _maxBytes)
                    throw new InvalidOperationException("Could not download the update: the package is larger than 4 GB.");
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            }
        }
        catch
        {
            failed = true;
            throw;
        }
        finally
        {
            output.Dispose();
            input.Dispose();
            if (failed && File.Exists(destination))
                File.Delete(destination);
        }

        return destination;
    }

    private HttpClient CreateClient()
    {
        var client = _handler is null
            ? new HttpClient()
            : new HttpClient(_handler, disposeHandler: false);
        client.Timeout = TimeSpan.FromMinutes(15);
        return client;
    }
}
