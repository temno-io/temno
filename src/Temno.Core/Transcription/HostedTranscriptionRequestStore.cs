using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Temno.Core.Account;
using Temno.Core.IO;

namespace Temno.Core.Transcription;

public sealed class HostedTranscriptionRequest
{
    public int Version { get; set; }
    public string AccountId { get; set; } = "";
    public string Endpoint { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public string Language { get; set; } = "";
    public string? SourcePath { get; set; }
    public string? SourceIdentity { get; set; }
    public Guid UploadKey { get; set; }
    public Guid JobKey { get; set; }
    public string? UploadId { get; set; }
    public long? QuotedCredits { get; set; }
    public string? JobId { get; set; }
    public bool SubmitStarted { get; set; }
    public bool CancelRequested { get; set; }
    public bool ResultAcknowledged { get; set; }
}

/// <summary>Metadata only. Credentials, audio and transcript text never enter this journal.</summary>
public sealed class HostedTranscriptionRequestStore
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private readonly string _root;

    public HostedTranscriptionRequestStore(string? root = null)
        => _root = root ?? Path.Combine(UserDataRoots.LocalAppDataRoot, "hosted-transcription");

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private string Partition(string account, string endpoint) => Path.Combine(_root, Hash(account + "\n" + endpoint));
    private string RequestPath(string account, string endpoint, string sha, string language)
        => Path.Combine(Partition(account, endpoint), Hash(sha + "\n" + language) + ".json");

    public IReadOnlyList<HostedTranscriptionRequest> Pending(string account, string endpoint)
    {
        var path = Partition(account, endpoint);
        if (!Directory.Exists(path)) return [];
        var pending = new List<HostedTranscriptionRequest>();
        foreach (var file in Directory.EnumerateFiles(path, "*.json"))
        {
            try
            {
                var request = Read(file);
                if (request.AccountId == account && request.Endpoint == endpoint && !request.ResultAcknowledged)
                    pending.Add(request);
            }
            catch (Exception error) when (error is HostedApiException or IOException or UnauthorizedAccessException)
            {
                // Preserve the unreadable request; it must not create fresh keys or block other jobs.
            }
        }
        return pending;
    }

    public async Task<Lease> AcquireAsync(string account, string endpoint, string sha, string language,
        string? sourcePath, CancellationToken ct, bool existingOnly = false, string? sourceIdentity = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(account);
        ArgumentException.ThrowIfNullOrWhiteSpace(endpoint);
        var path = RequestPath(account, endpoint, sha, language);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        FileStream gate;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            try { gate = new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); break; }
            catch (IOException) { await Task.Delay(250, ct).ConfigureAwait(false); }
        }
        try
        {
            if (existingOnly && !File.Exists(path)) throw new FileNotFoundException("The pending request has finished.");
            var request = File.Exists(path) ? Read(path) : new HostedTranscriptionRequest
            {
                Version = 1, AccountId = account, Endpoint = endpoint, Sha256 = sha, Language = language,
                UploadKey = Guid.NewGuid(), JobKey = Guid.NewGuid(),
                SourcePath = sourcePath,
                SourceIdentity = sourceIdentity ?? (sourcePath is null ? null : TranscriptDiskCache.KeyForPath(sourcePath)),
            };
            if (request.AccountId != account || request.Endpoint != endpoint || request.Sha256 != sha || request.Language != language)
                throw InvalidJournal();
            var lease = new Lease(path, gate, request);
            lease.Save(); // Both keys reach disk before the first network mutation.
            return lease;
        }
        catch { gate.Dispose(); throw; }
    }

    private static HostedTranscriptionRequest Read(string path)
    {
        try
        {
            var request = JsonSerializer.Deserialize<HostedTranscriptionRequest>(File.ReadAllBytes(path), Json);
            if (request is null || request.Version != 1 || string.IsNullOrWhiteSpace(request.AccountId)
                || string.IsNullOrWhiteSpace(request.Endpoint) || request.Sha256 is not { Length: 64 }
                || !request.Sha256.All(Uri.IsHexDigit)
                || request.UploadKey == Guid.Empty || request.JobKey == Guid.Empty
                || request.Language is not ("en" or "es")
                || request.UploadId is not null && !Guid.TryParseExact(request.UploadId, "D", out _)
                || request.JobId is not null && !Guid.TryParseExact(request.JobId, "D", out _)
                || request.JobId is not null && !request.SubmitStarted
                || request.ResultAcknowledged && request.JobId is null
                || request.SubmitStarted && (request.UploadId is null || request.QuotedCredits is not (>= 5 and <= 30)))
                throw InvalidJournal();
            return request;
        }
        catch (JsonException) { throw InvalidJournal(); }
    }

    private static HostedApiException InvalidJournal() => new(
        "The saved cloud transcription request could not be read. It was kept to prevent a duplicate charge.",
        code: "transcription_recovery_invalid");

    public sealed class Lease : IDisposable
    {
        private readonly string _path;
        private readonly FileStream _gate;
        public HostedTranscriptionRequest Request { get; }
        internal Lease(string path, FileStream gate, HostedTranscriptionRequest request)
            => (_path, _gate, Request) = (path, gate, request);

        public void Save()
        {
            var temp = _path + ".tmp";
            var bytes = JsonSerializer.SerializeToUtf8Bytes(Request, Json);
            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temp, _path, overwrite: true);
        }

        public void Remove() => File.Delete(_path);
        public void Dispose() => _gate.Dispose();
    }
}
