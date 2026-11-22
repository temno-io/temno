using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using Temno.Core.Models;

namespace Temno.Core.Audio;

/// <summary>
/// In-memory + on-disk cache of <see cref="BeatAnalysis"/> keyed by mediaRef.
/// Disk files are named "{mediaRef}_{size}_{mtime}_beats.json" (Mac BeatDetector cache shape).
/// Mac: Audio/Beats/BeatStore.swift
/// </summary>
public sealed class BeatStore
{
    public const string CacheSubdirectoryName = "BeatAnalysis";
    private const string FileSuffix = "_beats.json";

    private readonly IBeatDetector? _detector;
    private readonly string _cacheDirectory;
    private readonly ConcurrentDictionary<string, BeatAnalysis> _analyses = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _fileTags = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Task<BeatAnalysis>> _tasks = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    /// <param name="cacheDirectory">Directory for beat JSON files (created on first write).</param>
    /// <param name="detector">Optional detector used by <see cref="DetectAsync"/>; null allows hydrate/store only.</param>
    public BeatStore(string cacheDirectory, IBeatDetector? detector = null)
    {
        if (string.IsNullOrEmpty(cacheDirectory))
        {
            throw new ArgumentException("Cache directory cannot be null or empty.", nameof(cacheDirectory));
        }

        _cacheDirectory = cacheDirectory;
        _detector = detector;
    }

    public string CacheDirectory => _cacheDirectory;

    /// <summary>In-memory lookup only; does not touch disk.</summary>
    public BeatAnalysis? GetAnalysis(string mediaRef)
    {
        if (string.IsNullOrEmpty(mediaRef))
        {
            return null;
        }

        return _analyses.TryGetValue(mediaRef, out var analysis) ? analysis : null;
    }

    /// <summary>
    /// Loads a prior analysis from disk into memory when present and size/mtime still match.
    /// Never runs detection.
    /// </summary>
    public BeatAnalysis? Hydrate(string mediaPath, string mediaRef)
    {
        if (string.IsNullOrEmpty(mediaPath) || string.IsNullOrEmpty(mediaRef))
        {
            return null;
        }

        if (_analyses.TryGetValue(mediaRef, out var existing))
        {
            return existing;
        }

        var cached = LoadFromDisk(mediaPath, mediaRef);
        if (cached is null)
        {
            return null;
        }

        var tag = SizeMtimeTag(mediaPath);
        _analyses[mediaRef] = cached;
        _fileTags[mediaRef] = tag;
        return cached;
    }

    /// <summary>Reads disk cache without updating memory.</summary>
    public BeatAnalysis? LoadFromDisk(string mediaPath, string mediaRef)
    {
        if (string.IsNullOrEmpty(mediaPath) || string.IsNullOrEmpty(mediaRef) || !File.Exists(mediaPath))
        {
            return null;
        }

        var path = AnalysisPath(mediaPath, mediaRef);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<BeatAnalysis>(json, JsonOptions.ForProject);
        }
        catch (JsonException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    /// <summary>Writes analysis to memory and disk; removes stale files for the same mediaRef.</summary>
    public void Store(string mediaPath, string mediaRef, BeatAnalysis analysis)
    {
        ArgumentNullException.ThrowIfNull(analysis);
        if (string.IsNullOrEmpty(mediaPath))
        {
            throw new ArgumentException("Media path cannot be null or empty.", nameof(mediaPath));
        }

        if (string.IsNullOrEmpty(mediaRef))
        {
            throw new ArgumentException("Media ref cannot be null or empty.", nameof(mediaRef));
        }

        var tag = SizeMtimeTag(mediaPath);
        var outputPath = AnalysisPath(mediaPath, mediaRef);
        Directory.CreateDirectory(_cacheDirectory);
        RemoveStaleCaches(mediaRef, keeping: outputPath);

        var json = JsonSerializer.Serialize(analysis, JsonOptions.ForProject);
        File.WriteAllText(outputPath, json);

        _analyses[mediaRef] = analysis;
        _fileTags[mediaRef] = tag;
    }

    /// <summary>
    /// Returns cached analysis when valid; otherwise runs the detector, stores the result, and returns it.
    /// Concurrent callers for the same mediaRef share one in-flight task.
    /// </summary>
    public Task<BeatAnalysis> DetectAsync(
        string mediaPath,
        string mediaRef,
        bool force = false,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(mediaPath))
        {
            throw new ArgumentException("Media path cannot be null or empty.", nameof(mediaPath));
        }

        if (string.IsNullOrEmpty(mediaRef))
        {
            throw new ArgumentException("Media ref cannot be null or empty.", nameof(mediaRef));
        }

        if (_detector is null)
        {
            throw new InvalidOperationException("BeatStore was created without an IBeatDetector.");
        }

        var tag = File.Exists(mediaPath) ? SizeMtimeTag(mediaPath) : string.Empty;
        if (!force)
        {
            if (_analyses.TryGetValue(mediaRef, out var mem)
                && _fileTags.TryGetValue(mediaRef, out var memTag)
                && memTag == tag)
            {
                return Task.FromResult(mem);
            }

            var disk = LoadFromDisk(mediaPath, mediaRef);
            if (disk is not null)
            {
                _analyses[mediaRef] = disk;
                _fileTags[mediaRef] = tag;
                return Task.FromResult(disk);
            }
        }

        lock (_gate)
        {
            if (!force
                && _tasks.TryGetValue(mediaRef, out var running)
                && !running.IsCompleted)
            {
                return running;
            }

            var detector = _detector;
            var task = Task.Run(async () =>
            {
                try
                {
                    var analysis = await detector.DetectAsync(mediaPath, cancellationToken).ConfigureAwait(false);
                    cancellationToken.ThrowIfCancellationRequested();
                    Store(mediaPath, mediaRef, analysis);
                    return analysis;
                }
                finally
                {
                    _tasks.TryRemove(mediaRef, out _);
                }
            }, cancellationToken);

            _tasks[mediaRef] = task;
            return task;
        }
    }

    public void Invalidate(string mediaRef)
    {
        if (string.IsNullOrEmpty(mediaRef))
        {
            return;
        }

        _analyses.TryRemove(mediaRef, out _);
        _fileTags.TryRemove(mediaRef, out _);
        _tasks.TryRemove(mediaRef, out _);
    }

    public void Reset()
    {
        _analyses.Clear();
        _fileTags.Clear();
        _tasks.Clear();
    }

    public string AnalysisPath(string mediaPath, string mediaRef)
    {
        var tag = SizeMtimeTag(mediaPath);
        var fileName = mediaRef + "_" + tag + FileSuffix;
        return Path.Combine(_cacheDirectory, fileName);
    }

    /// <summary>Mac DiskCache.sizeMtimeTag: "{sizeBytes}_{mtimeUnixSeconds}".</summary>
    public static string SizeMtimeTag(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists)
        {
            throw new FileNotFoundException("Media file not found.", path);
        }

        var mtime = new DateTimeOffset(info.LastWriteTimeUtc).ToUnixTimeSeconds();
        return string.Create(CultureInfo.InvariantCulture, $"{info.Length}_{mtime}");
    }

    private void RemoveStaleCaches(string mediaRef, string keeping)
    {
        if (!Directory.Exists(_cacheDirectory))
        {
            return;
        }

        var prefix = mediaRef + "_";
        string[] entries;
        try
        {
            entries = Directory.GetFiles(_cacheDirectory);
        }
        catch (IOException)
        {
            return;
        }

        foreach (var entry in entries)
        {
            var name = Path.GetFileName(entry);
            if (!name.StartsWith(prefix, StringComparison.Ordinal) || !name.EndsWith(FileSuffix, StringComparison.Ordinal))
            {
                continue;
            }

            if (string.Equals(entry, keeping, StringComparison.Ordinal))
            {
                continue;
            }

            try
            {
                File.Delete(entry);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
