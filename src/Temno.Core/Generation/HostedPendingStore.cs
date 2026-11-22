using System.Text.Json;
using System.Text.Json.Serialization;
using Temno.Core.IO;

namespace Temno.Core.Generation;

public sealed class HostedPendingJob
{
    public string Environment { get; set; } = "";
    public string AccountId { get; set; } = "";
    public string DeviceId { get; set; } = "";
    public string ProjectPath { get; set; } = "";
    public string PlaceholderId { get; set; } = "";
    public string? JobId { get; set; }
    public string? QuoteId { get; set; }
    public string? SubmitKey { get; set; }
    public string Kind { get; set; } = "media";
    public string OutputType { get; set; } = "video";
    public string Phase { get; set; } = HostedJobPhases.Pending;
    public string? ServerReason { get; set; }
    public string? Name { get; set; }
    public List<string>? ResultUrls { get; set; }
    public long? CostCredits { get; set; }
    public bool CancelRequested { get; set; }
    public bool CantCancel { get; set; }

    [JsonIgnore]
    public bool SubmitInFlight { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class HostedNotice
{
    public string Kind { get; set; } = "";
    public string Message { get; set; } = "";
    public DateTimeOffset At { get; set; }
}

/// <summary>
/// The pending list lives outside every project, so a job can land after Temno was closed and into
/// a project that is not the one now open.
/// </summary>
public sealed class HostedPendingStore
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
    };

    private readonly object _gate = new();
    private readonly string _path;
    private HostedPendingDocument _document;

    public HostedPendingStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = path;
        _document = Load(path);
    }

    public string Path => _path;

    public void Upsert(HostedPendingJob job)
    {
        ArgumentNullException.ThrowIfNull(job);
        lock (_gate)
        {
            var existing = Find(job.PlaceholderId);
            if (existing is null)
                _document.Jobs.Add(job);
            else
                Copy(job, existing);
            Save();
        }
    }

    public void Update(string placeholderId, Action<HostedPendingJob> edit)
    {
        ArgumentNullException.ThrowIfNull(edit);
        lock (_gate)
        {
            var existing = Find(placeholderId);
            if (existing is null)
                return;
            edit(existing);
            existing.UpdatedAt = DateTimeOffset.UtcNow;
            Save();
        }
    }

    public HostedPendingJob? FindByPlaceholder(string placeholderId)
    {
        lock (_gate)
            return Clone(Find(placeholderId));
    }

    public HostedPendingJob? FindOpenByQuote(string accountId, string quoteId)
    {
        if (string.IsNullOrWhiteSpace(quoteId))
            return null;
        lock (_gate)
        {
            var found = _document.Jobs.FirstOrDefault(job =>
                HostedJobPhases.IsOpen(job.Phase)
                && string.Equals(job.QuoteId, quoteId, StringComparison.OrdinalIgnoreCase)
                && (string.IsNullOrEmpty(job.AccountId)
                    || string.Equals(job.AccountId, accountId, StringComparison.Ordinal)));
            return Clone(found);
        }
    }

    public IReadOnlyList<HostedPendingJob> Active(string environment, string accountId)
    {
        lock (_gate)
        {
            return _document.Jobs
                .Where(job => HostedJobPhases.IsOpen(job.Phase)
                    && string.Equals(job.Environment, environment, StringComparison.Ordinal)
                    && (string.IsNullOrEmpty(job.AccountId)
                        || string.Equals(job.AccountId, accountId, StringComparison.Ordinal)))
                .Select(Clone)
                .Where(job => job is not null)
                .Cast<HostedPendingJob>()
                .ToList();
        }
    }

    public void AdoptAccount(string accountId)
    {
        if (string.IsNullOrWhiteSpace(accountId))
            return;
        lock (_gate)
        {
            var changed = false;
            foreach (var job in _document.Jobs)
            {
                if (!string.IsNullOrEmpty(job.AccountId) || !HostedJobPhases.IsOpen(job.Phase))
                    continue;
                job.AccountId = accountId;
                changed = true;
            }
            if (changed)
                Save();
        }
    }

    public bool IsSuspended(string accountId)
    {
        lock (_gate)
            return _document.SuspendedAccounts.Contains(accountId, StringComparer.Ordinal);
    }

    public void Suspend(string accountId)
    {
        if (string.IsNullOrWhiteSpace(accountId))
            return;
        lock (_gate)
        {
            if (_document.SuspendedAccounts.Contains(accountId, StringComparer.Ordinal))
                return;
            _document.SuspendedAccounts.Add(accountId);
            Save();
        }
    }

    public void AddNotice(string kind, string message)
    {
        lock (_gate)
        {
            _document.Notices.Add(new HostedNotice
            {
                Kind = kind,
                Message = message,
                At = DateTimeOffset.UtcNow,
            });
            Save();
        }
    }

    public IReadOnlyList<HostedNotice> TakeNotices()
    {
        lock (_gate)
        {
            if (_document.Notices.Count == 0)
                return [];
            var taken = _document.Notices.Select(notice => new HostedNotice
            {
                Kind = notice.Kind,
                Message = notice.Message,
                At = notice.At,
            }).ToList();
            _document.Notices.Clear();
            Save();
            return taken;
        }
    }

    private HostedPendingJob? Find(string placeholderId)
        => _document.Jobs.FirstOrDefault(job => string.Equals(job.PlaceholderId, placeholderId, StringComparison.Ordinal));

    private void Save()
    {
        var directory = System.IO.Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);
        var json = JsonSerializer.Serialize(_document, Json);
        var temporary = _path + ".tmp";
        File.WriteAllText(temporary, json);
        File.Move(temporary, _path, overwrite: true);
    }

    private static HostedPendingDocument Load(string path)
    {
        try
        {
            if (!File.Exists(path))
                return new HostedPendingDocument();
            var document = JsonSerializer.Deserialize<HostedPendingDocument>(File.ReadAllText(path), Json);
            document ??= new HostedPendingDocument();
            document.Jobs ??= [];
            document.Notices ??= [];
            document.SuspendedAccounts ??= [];
            return document;
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            return new HostedPendingDocument
            {
                Notices =
                [
                    new HostedNotice
                    {
                        Kind = "store",
                        Message = "The list of unfinished generations could not be read. New ones are still tracked.",
                        At = DateTimeOffset.UtcNow,
                    },
                ],
            };
        }
    }

    private static void Copy(HostedPendingJob from, HostedPendingJob to)
    {
        to.Environment = from.Environment;
        to.AccountId = from.AccountId;
        to.DeviceId = from.DeviceId;
        to.ProjectPath = from.ProjectPath;
        to.JobId = from.JobId;
        to.QuoteId = from.QuoteId;
        to.SubmitKey = from.SubmitKey;
        to.Kind = from.Kind;
        to.OutputType = from.OutputType;
        to.Phase = from.Phase;
        to.ServerReason = from.ServerReason;
        to.Name = from.Name;
        to.ResultUrls = from.ResultUrls;
        to.CostCredits = from.CostCredits;
        to.CancelRequested = from.CancelRequested;
        to.CantCancel = from.CantCancel;
        to.SubmitInFlight = from.SubmitInFlight;
        to.CreatedAt = from.CreatedAt;
        to.UpdatedAt = from.UpdatedAt;
    }

    private static HostedPendingJob? Clone(HostedPendingJob? job)
    {
        if (job is null)
            return null;
        var copy = new HostedPendingJob();
        copy.PlaceholderId = job.PlaceholderId;
        Copy(job, copy);
        return copy;
    }

    private sealed class HostedPendingDocument
    {
        public List<HostedPendingJob> Jobs { get; set; } = [];
        public List<HostedNotice> Notices { get; set; } = [];
        public List<string> SuspendedAccounts { get; set; } = [];
    }
}

public static class HostedDeviceId
{
    public static string DefaultPath =>
        Path.Combine(UserDataRoots.LocalAppDataRoot, "hosted-device-id");

    public static string GetOrCreate(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var directory = System.IO.Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);
        if (File.Exists(path))
        {
            var existing = File.ReadAllText(path).Trim();
            if (Guid.TryParse(existing, out _))
                return existing;
        }

        var created = Guid.NewGuid().ToString("D");
        File.WriteAllText(path, created);
        return created;
    }
}
