using System.Net.Http;
using System.Security.Cryptography;
using Whisper.net.Ggml;
using Temno.Core.IO;

namespace Temno.Media.Transcription;

/// <summary>
/// Locates or downloads the default English ggml weights.
/// Search order: <c>TEMNO_WHISPER_MODEL</c>, app <c>whisper/</c>, %LocalAppData%\Temno\whisper\.
/// Wherever a model is found, only the pinned bytes reach the native loader: whisper.cpp 1.7.4's
/// <c>whisper_model_load</c> trusts the file it is given (CVE-2026-10298, CVE-2026-110810; WP-401).
/// </summary>
public static class WhisperModelStore
{
    public const string DefaultFileName = "ggml-base.en.bin";
    public const string BundledFolderName = "whisper";
    public static GgmlType DefaultGgmlType => GgmlType.BaseEn;

    /// <summary>The size of the pinned model; scripts/build-msix.ps1 pins the same file.</summary>
    public const long PinnedModelBytes = 147_964_211;

    /// <summary>SHA-256 of the pinned model; scripts/build-msix.ps1 pins the same file.</summary>
    public const string PinnedModelSha256 = "A03779C86DF3323075F5E796CB2CE5029F00EC8869EEE3FDFB897AFE36C6D002";

    /// <summary>
    /// The ggerganov/whisper.cpp revision that last changed the pinned file (2023-03-22). The download comes from it,
    /// never from a moving branch, as scripts/ensure-whisper-model.ps1's does (WP-402).
    /// </summary>
    public const string PinnedModelRevision = "80da2d8bfee42b0e836fc3a9890373e5defc00a6";

    public const string PinnedModelUrl =
        "https://huggingface.co/ggerganov/whisper.cpp/resolve/" + PinnedModelRevision + "/" + DefaultFileName;

    /// <summary>
    /// Throws <see cref="InvalidDataException"/> unless <paramref name="path"/> holds exactly the pinned model.
    /// </summary>
    public static void VerifyPinned(string path) => VerifyPinned(path, PinnedModelBytes, PinnedModelSha256);

    internal static void VerifyPinned(string path, long expectedBytes, string expectedSha256)
    {
        using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan);
        if (stream.Length != expectedBytes)
        {
            throw new InvalidDataException(
                $"The Whisper model at {path} is {stream.Length} bytes, not the pinned {DefaultFileName} "
                + $"({expectedBytes} bytes). Temno loads only the pinned model.");
        }

        var actual = Convert.ToHexString(SHA256.HashData(stream));
        if (!actual.Equals(expectedSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"The Whisper model at {path} has SHA-256 {actual}, not the pinned {DefaultFileName}. "
                + "Temno loads only the pinned model.");
        }
    }

    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static string? _bundledOverride;
    private static string? _userOverride;
    private static Func<CancellationToken, Task<Stream>>? _downloadOverride;

    /// <summary>Replaces the pinned download, for tests; null restores it.</summary>
    internal static void SetDownloadForTests(Func<CancellationToken, Task<Stream>>? download) => _downloadOverride = download;

    public static void SetPathsForTests(string? bundledPath, string? userPath)
    {
        _bundledOverride = bundledPath;
        _userOverride = userPath;
    }

    public static string BundledPath =>
        _bundledOverride ?? Path.Combine(AppContext.BaseDirectory, BundledFolderName, DefaultFileName);

    public static string UserPath =>
        _userOverride ?? Path.Combine(
            UserDataRoots.LocalAppDataRoot,
            BundledFolderName,
            DefaultFileName);

    public static string? TryResolvePath()
    {
        var env = EnvCompat.Get("TEMNO_WHISPER_MODEL");
        if (!string.IsNullOrWhiteSpace(env) && File.Exists(env))
            return env;
        if (File.Exists(BundledPath))
            return BundledPath;
        if (File.Exists(UserPath))
            return UserPath;
        return null;
    }

    public static bool IsReady => TryResolvePath() is not null;

    public static async Task<string> EnsureDefaultAsync(CancellationToken cancellationToken = default)
    {
        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var existing = TryResolvePath();
            // Temno's own download is replaced when it is not the pinned size (an older model, or an interrupted
            // copy); a model named by TEMNO_WHISPER_MODEL or bundled with the app is never touched, and fails closed.
            if (existing is not null
                && !(string.Equals(existing, UserPath, StringComparison.OrdinalIgnoreCase)
                     && new FileInfo(existing).Length != PinnedModelBytes))
                return existing;

            var dest = UserPath;
            var dir = Path.GetDirectoryName(dest);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            var tmp = dest + ".tmp";
            try
            {
                // The implicit DisposeAsync of an `await using` captures context too, so both the
                // stream and the file handle are configured explicitly.
                var modelStream = await (_downloadOverride ?? DownloadPinnedAsync)(cancellationToken).ConfigureAwait(false);
                await using (modelStream.ConfigureAwait(false))
                {
                    var file = File.Create(tmp);
                    await using (file.ConfigureAwait(false))
                        await modelStream.CopyToAsync(file, cancellationToken).ConfigureAwait(false);
                }

                VerifyPinned(tmp);
                File.Move(tmp, dest, overwrite: true);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                try { File.Delete(tmp); } catch { /* best-effort */ }
                throw new InvalidOperationException(
                    "Could not download Whisper model ggml-base.en.bin (~142 MB) into "
                    + dest
                    + ". Check the network, copy the file there, or set TEMNO_WHISPER_MODEL.",
                    ex);
            }

            return dest;
        }
        finally
        {
            Gate.Release();
        }
    }

    // A 142 MB download runs as long as it takes; the caller's token ends it.
    private static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan };

    private static async Task<Stream> DownloadPinnedAsync(CancellationToken cancellationToken)
    {
        var response = await Http.GetAsync(PinnedModelUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        try
        {
            response.EnsureSuccessStatusCode();
            return new ResponseStream(
                await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false), response);
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }

    /// <summary>The body of a response that is disposed with it.</summary>
    private sealed class ResponseStream(Stream inner, HttpResponseMessage response) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => inner.ReadAsync(buffer, cancellationToken);
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => inner.ReadAsync(buffer, offset, count, cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
                response.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
