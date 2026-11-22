using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Temno.Core.IO;

namespace Temno.Core.Agent;

/// <summary>
/// WP-400: the hosted turns this install created, by retry key and account. It lives in the per-user data root
/// (<c>%LOCALAPPDATA%\Temno\hosted-turn-origins.json</c>, or <c>Temno-Dev</c>), never in a package, so a chat file
/// cannot claim to have been sent from here. Only a pending turn this record matches resumes without a click.
/// </summary>
public static class HostedTurnOrigins
{
    public const string FileName = "hosted-turn-origins.json";

    // Content of a settled turn expires a day after it finishes; a month covers any recovery worth doing.
    public static readonly TimeSpan Retention = TimeSpan.FromDays(30);
    public const int MaxRecords = 500;

    private const int CurrentVersion = 1;
    private static readonly object Gate = new();
    private static string? _pathOverride;
    private static string? _defaultPathOverride;
    private static (string Path, DateTime Written, long Length, Document Document)? _cache;

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Per-test file. <c>null</c> falls back to <see cref="SetDefaultPathForTests"/>.</summary>
    public static void SetPathForTests(string? path)
    {
        lock (Gate)
        {
            _pathOverride = path;
            _cache = null;
        }
    }

    /// <summary>Process-wide test file, installed by the test assembly so no test touches the user's real record.</summary>
    public static void SetDefaultPathForTests(string? path)
    {
        lock (Gate)
        {
            _defaultPathOverride = path;
            _cache = null;
        }
    }

    public static string FilePath
    {
        get
        {
            lock (Gate)
                return _pathOverride ?? _defaultPathOverride ?? Path.Combine(UserDataRoots.LocalAppDataRoot, FileName);
        }
    }

    /// <summary>
    /// The digest a record keeps of a turn's frozen submission. A chat whose submission differs from what this install
    /// sent is not the turn it recorded. Computed after a JSON round trip, so a chat saved and read back digests the same.
    /// </summary>
    public static string Digest(HostedQuoteRequest? submission)
    {
        if (submission is null)
            return "none";
        var canonical = JsonNode.Parse(JsonSerializer.SerializeToUtf8Bytes(submission, ChatJson.Options))!.ToJsonString();
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }

    /// <summary>
    /// Records that this install is about to send the turn with <paramref name="idempotencyKey"/> and the frozen
    /// submission digested as <paramref name="submissionDigest"/> for <paramref name="accountId"/>. Throws
    /// <see cref="IOException"/> or <see cref="UnauthorizedAccessException"/> when the record cannot be written, so
    /// the caller sends nothing.
    /// </summary>
    public static void Record(string idempotencyKey, string accountId, string submissionDigest, DateTimeOffset? now = null)
    {
        if (!Guid.TryParse(idempotencyKey, out var key) || key == Guid.Empty)
            throw new ArgumentException("A hosted turn needs a retry key.", nameof(idempotencyKey));
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);
        ArgumentException.ThrowIfNullOrWhiteSpace(submissionDigest);
        var at = now ?? DateTimeOffset.UtcNow;
        Change(document =>
        {
            document.Turns.RemoveAll(t => t.Key == key || at - t.RecordedAt > Retention);
            document.Turns.Add(new TurnRecord { Key = key, Account = accountId, Submission = submissionDigest, RecordedAt = at });
            if (document.Turns.Count > MaxRecords)
                document.Turns = document.Turns.OrderByDescending(t => t.RecordedAt).Take(MaxRecords).ToList();
            return true;
        });
    }

    /// <summary>
    /// Adds the Gateway's turn id to a recorded turn, before the chat saves it, so the record vouches for that turn only.
    /// Throws as <see cref="Record"/> does; a caller that cannot record it carries on, and the turn then needs a click.
    /// </summary>
    public static void RecordTurn(string? idempotencyKey, Guid turnId)
    {
        if (!Guid.TryParse(idempotencyKey, out var key) || key == Guid.Empty || turnId == Guid.Empty)
            return;
        Change(document =>
        {
            var record = document.Turns.FirstOrDefault(t => t.Key == key);
            if (record is null || record.Turn == turnId) return false;
            record.Turn = turnId;
            return true;
        });
    }

    /// <summary>
    /// Drops the record of a turn that settled or was withdrawn: a finished turn vouches for nothing, so a chat edited to
    /// look unfinished again waits for a click. Throws as <see cref="Record"/> does.
    /// </summary>
    public static void Forget(string? idempotencyKey)
    {
        if (!Guid.TryParse(idempotencyKey, out var key) || key == Guid.Empty)
            return;
        Change(document => document.Turns.RemoveAll(t => t.Key == key) > 0);
    }

    /// <summary>
    /// True when this install recorded the turn with <paramref name="idempotencyKey"/> for exactly
    /// <paramref name="accountId"/>, with the same frozen submission, and no other turn id: a chat may lag the record
    /// (no turn id yet), never differ from it. A missing key or account, and an unreadable record, are not a match.
    /// </summary>
    public static bool Matches(string? idempotencyKey, string? accountId, string submissionDigest, string? turnId)
    {
        if (string.IsNullOrWhiteSpace(accountId) || !Guid.TryParse(idempotencyKey, out var key) || key == Guid.Empty)
            return false;
        Guid? turn = Guid.TryParse(turnId, out var parsed) && parsed != Guid.Empty ? parsed : null;
        if (turn is null && !string.IsNullOrWhiteSpace(turnId))
            return false;
        lock (Gate)
        {
            Document document;
            try
            {
                document = Cached(FilePath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return false;
            }
            return document.Turns.Exists(t => t.Key == key && string.Equals(t.Account, accountId, StringComparison.Ordinal)
                && string.Equals(t.Submission, submissionDigest, StringComparison.Ordinal)
                && (turn is null || t.Turn == turn));
        }
    }

    private static void Change(Func<Document, bool> change)
    {
        lock (Gate)
        {
            var file = FilePath;
            using var crossProcess = CrossProcessLock(file);
            var document = Read(file, out var damaged);
            if (damaged)
                File.Move(file, file + ".damaged-" + DateTime.UtcNow.ToString("yyyyMMddHHmmssfff"), overwrite: false);
            if (!change(document) && !damaged)
                return;
            document.Version = CurrentVersion;
            Write(file, document);
            _cache = null;
        }
    }

    // Read under Gate. The panel asks on every render, so an unchanged file is not parsed again.
    private static Document Cached(string file)
    {
        var info = new FileInfo(file);
        if (!info.Exists)
            return new Document();
        if (_cache is { } cached && cached.Path == file && cached.Written == info.LastWriteTimeUtc && cached.Length == info.Length)
            return cached.Document;
        var document = Read(file, out _);
        _cache = (file, info.LastWriteTimeUtc, info.Length, document);
        return document;
    }

    private static Document Read(string file, out bool damaged)
    {
        damaged = false;
        if (!File.Exists(file))
            return new Document();
        var bytes = File.ReadAllBytes(file);
        try
        {
            var document = JsonSerializer.Deserialize<Document>(bytes, Json);
            if (document is null || document.Version > CurrentVersion)
            {
                damaged = true;
                return new Document();
            }
            document.Turns = document.Turns?.Where(t => t is not null && t.Key != Guid.Empty && !string.IsNullOrWhiteSpace(t.Submission)
                && !string.IsNullOrWhiteSpace(t.Account)).ToList() ?? [];
            return document;
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            damaged = true;
            return new Document();
        }
    }

    private static IDisposable CrossProcessLock(string file)
    {
        var name = @"Local\Temno.HostedTurnOrigins." + Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(file.ToUpperInvariant())))[..32];
        var mutex = new Mutex(initiallyOwned: false, name);
        try
        {
            if (!mutex.WaitOne(TimeSpan.FromSeconds(10)))
                throw new IOException("Timed out waiting for another Temno process to finish writing " + FileName + ".");
        }
        catch (AbandonedMutexException)
        {
            // The previous holder exited mid-write; the atomic replace means the file is whole.
        }
        catch
        {
            mutex.Dispose();
            throw;
        }
        return new Releaser(mutex);
    }

    private sealed class Releaser(Mutex mutex) : IDisposable
    {
        public void Dispose()
        {
            mutex.ReleaseMutex();
            mutex.Dispose();
        }
    }

    private static void Write(string file, Document document)
    {
        var directory = Path.GetDirectoryName(file);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);
        var temporary = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllBytes(temporary, JsonSerializer.SerializeToUtf8Bytes(document, Json));
            File.Move(temporary, file, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }

    private sealed class Document
    {
        [JsonPropertyName("version")]
        public int Version { get; set; } = CurrentVersion;

        [JsonPropertyName("turns")]
        public List<TurnRecord> Turns { get; set; } = [];
    }

    private sealed class TurnRecord
    {
        [JsonPropertyName("key")]
        public Guid Key { get; set; }

        [JsonPropertyName("account")]
        public string Account { get; set; } = "";

        [JsonPropertyName("submission")]
        public string Submission { get; set; } = "";

        [JsonPropertyName("turn")]
        public Guid? Turn { get; set; }

        [JsonPropertyName("recordedAt")]
        public DateTimeOffset RecordedAt { get; set; }
    }
}
