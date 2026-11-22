using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Temno.Core.IO;
using Temno.Core.Project;

namespace Temno.Core.Agent.Skills;

public enum SkillSource
{
    User,
    BuiltIn,
}

/// <summary>What a skill's row says, and whether the agent sees it. Only <see cref="On"/> reaches the agent.</summary>
public enum SkillStatus
{
    On,
    /// <summary>The user turned it off.</summary>
    Off,
    /// <summary>A user skill nobody approved: it appeared in the folder outside Temno (R-1).</summary>
    New,
    /// <summary>Its text changed outside Temno after the user approved it (R-1).</summary>
    Changed,
    /// <summary>It fails the format; <see cref="SkillEntry.Error"/> says why.</summary>
    Invalid,
    /// <summary>A built-in hidden by the user's skill of the same name (D-2).</summary>
    Overridden,
}

/// <summary>How a user skill arrived. A skill with none was added outside Temno.</summary>
public sealed record SkillOrigin(string Kind, string? From, DateTimeOffset At)
{
    public const string Created = "created";
    public const string Imported = "imported";
}

public sealed class SkillEntry
{
    /// <summary>The folder name, which a valid skill's name equals; the agent reads it by this id.</summary>
    public required string Name { get; init; }
    public required SkillSource Source { get; init; }
    public required string Folder { get; init; }
    public required string FilePath { get; init; }
    public required SkillStatus Status { get; init; }
    public SkillDocument? Document { get; init; }
    public string? Error { get; init; }
    public string? Sha256 { get; init; }
    public long Bytes { get; init; }
    public SkillOrigin? Origin { get; init; }

    /// <summary>A user skill whose current text is the text the user approved. Built-ins need no approval.</summary>
    public bool Approved { get; init; }

    /// <summary>A user skill that hides a built-in of the same name.</summary>
    public bool OverridesBuiltIn { get; init; }

    public bool IsOn => Status == SkillStatus.On;
    public string Description => Document?.Description ?? "";
}

public sealed class SkillSnapshot(IReadOnlyList<SkillEntry> all)
{
    /// <summary>Every user and built-in skill, by name, the user's first where both have one.</summary>
    public IReadOnlyList<SkillEntry> All { get; } = all;

    public IEnumerable<SkillEntry> On => All.Where(s => s.IsOn);

    public SkillEntry? Find(string name, SkillSource source) =>
        All.FirstOrDefault(s => s.Source == source && string.Equals(s.Name, name, StringComparison.Ordinal));
}

/// <summary>A skill the agent may follow: on, valid, and for a user skill, the exact text the user approved.</summary>
public sealed record SkillRead(string Name, SkillSource Source, SkillDocument Document);

/// <summary>A skill read from a folder or a .zip and shown to the user before anything is written (D-4).</summary>
public sealed class SkillImport
{
    public required string SourcePath { get; init; }
    public required SkillDocument Document { get; init; }
    public required string Text { get; init; }
    public required byte[] Bytes { get; init; }
    public required string Sha256 { get; init; }

    /// <summary>Files beside SKILL.md that are left behind: this version imports only the instructions.</summary>
    public int IgnoredFiles { get; init; }
    public bool ReplacesUserSkill { get; init; }
    public bool OverridesBuiltIn { get; init; }
}

/// <summary>
/// WP-309: the user's skills in <c>~/.temno/skills/&lt;name&gt;/SKILL.md</c> and the built-ins in a <c>skills</c>
/// folder beside the app (D-2). A user skill is on only while its text is the text the user approved in Temno
/// (R-1), so a skill that another program, or the agent through a file-writing tool, puts in the folder stays
/// off until the user reads it. Approvals, the off list and where each skill came from live in
/// <c>~/.temno/skills.json</c>; a program that forges that file too runs with the user's rights, beyond reach. Every read checks containment as WP-208 does, and nothing here runs a script.
/// Mac analog: <c>Agent/Skills/SkillStore.swift</c>.
/// </summary>
public sealed class SkillStore
{
    /// <summary>How long the agent's index may reuse a scan; a change made through this store is seen at once.</summary>
    public const int RescanMilliseconds = 1000;

    private const int MaxFolders = 1000;
    private const int MaxListedFiles = 10_000;

    private static readonly JsonSerializerOptions StateJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
    };

    private readonly object _gate = new();
    private SkillSnapshot? _snapshot;
    private long _scannedAt;
    private int _version;
    private int _snapshotVersion = -1;

    public SkillStore(string userRoot, string? builtInRoot = null, string? statePath = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userRoot);
        UserRoot = Path.GetFullPath(userRoot);
        BuiltInRoot = string.IsNullOrWhiteSpace(builtInRoot) ? null : Path.GetFullPath(builtInRoot);
        StatePath = Path.GetFullPath(statePath
            ?? Path.Combine(Path.GetDirectoryName(UserRoot.TrimEnd(Path.DirectorySeparatorChar)) ?? UserRoot, "skills.json"));
    }

    public static string DefaultUserRoot => Path.Combine(UserDataRoots.HomeRoot, "skills");

    public static string DefaultBuiltInRoot => Path.Combine(AppContext.BaseDirectory, "skills");

    /// <summary>The signed-in Windows user's skills and this app's built-ins. Temno and Temno-Dev share them (R-4).</summary>
    public static SkillStore ForUser() => new(DefaultUserRoot, DefaultBuiltInRoot);

    public string UserRoot { get; }
    public string? BuiltInRoot { get; }
    public string StatePath { get; }

    public IReadOnlyList<string> Roots => BuiltInRoot is null ? [UserRoot] : [UserRoot, BuiltInRoot];

    /// <summary>A fresh look at both folders: every SKILL.md is read and hashed.</summary>
    public SkillSnapshot Scan()
    {
        lock (_gate)
            return ScanLocked();
    }

    /// <summary>The latest scan when it is under <see cref="RescanMilliseconds"/> old and nothing changed here since.</summary>
    public SkillSnapshot Current()
    {
        lock (_gate)
        {
            if (_snapshot is { } snapshot && _snapshotVersion == _version
                && Environment.TickCount64 - _scannedAt < RescanMilliseconds)
                return snapshot;
            return ScanLocked();
        }
    }

    /// <summary>The <c># Skills</c> section for the agent's instructions, or empty when no skill is on (D-3).</summary>
    public string IndexSection() => SkillIndex.Section(Current().On);

    /// <summary>The section and the MCP pointer (<see cref="SkillIndex.Pointer"/>) from one scan.</summary>
    public (string Section, string Pointer) Index()
    {
        var on = Current().On.ToArray();
        return (SkillIndex.Section(on), SkillIndex.Pointer(on));
    }

    /// <summary>
    /// The skill <paramref name="id"/> for <c>read_skill</c>. The bytes it returns are the bytes it hashed, so a
    /// file changed after the scan is caught here. A user folder with the id hides the built-in, on or not.
    /// </summary>
    public SkillRead ReadForAgent(string id)
    {
        SkillPaths.RequireName(id);
        lock (_gate)
        {
            var state = LoadState();
            var userFolder = Path.Combine(UserRoot, id);
            if (Directory.Exists(userFolder) || File.Exists(userFolder))
                return ReadOneQuietly(SkillSource.User, UserRoot, id, state);
            if (BuiltInRoot is { } builtIn && Directory.Exists(Path.Combine(builtIn, id)))
                return ReadOneQuietly(SkillSource.BuiltIn, builtIn, id, state);
            // An agent that guessed an id, or whose client cut the index short, learns the real ones.
            var on = ScanLocked().On.Select(s => s.Name).ToArray();
            throw new InvalidOperationException(SkillPaths.NotFound(id) + (on.Length == 0
                ? " No skill is on."
                : " Skills that are on: " + string.Join(", ", on.Take(50)) + (on.Length > 50 ? $", and {on.Length - 50} more." : ".")));
        }
    }

    /// <summary>The text of <paramref name="entry"/> as on disk now, with the hash Settings approves when the user turns it on.</summary>
    public (string Text, string Sha256) ReadText(SkillEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var root = entry.Source == SkillSource.User ? UserRoot : BuiltInRoot
            ?? throw new InvalidOperationException("This app has no built-in skills folder.");
        var path = SkillPaths.TryResolve(entry.Name, root)
            ?? throw new InvalidOperationException($"'{SkillDocument.Shown(entry.Name)}' has no SKILL.md.");
        var bytes = ReadCapped(path);
        return (new UTF8Encoding(false, false).GetString(bytes).TrimStart('\uFEFF'), Sha256(bytes));
    }

    /// <summary>Writes a new user skill from <paramref name="text"/>; its name picks the folder. The user wrote it, so it is on.</summary>
    public SkillEntry Create(string text, DateTimeOffset? now = null)
    {
        var (document, bytes) = Prepare(text);
        using (Changing())
        {
            var folder = UserFolder(document.Name);
            if (Directory.Exists(folder) || File.Exists(folder))
                throw new InvalidOperationException($"You already have a skill named '{document.Name}'.");
            Directory.CreateDirectory(folder);
            WriteSkillFile(folder, bytes);
            var state = LoadState();
            Approve(state, document.Name, Sha256(bytes));
            state.Origins[document.Name] = new SkillOrigin(SkillOrigin.Created, null, now ?? DateTimeOffset.UtcNow);
            return Commit(state, document.Name);
        }
    }

    /// <summary>Saves an edit made in Temno. A new name renames the folder. The user wrote it, so it is approved.</summary>
    public SkillEntry Save(string currentName, string text)
    {
        SkillPaths.RequireName(currentName);
        var (document, bytes) = Prepare(text);
        using (Changing())
        {
            var folder = ExistingUserFolder(currentName);
            var state = LoadState();
            if (!string.Equals(document.Name, currentName, StringComparison.Ordinal))
            {
                var target = UserFolder(document.Name);
                if (Directory.Exists(target) || File.Exists(target))
                    throw new InvalidOperationException($"You already have a skill named '{document.Name}'.");
                Directory.Move(folder, target);
                folder = target;
                Rename(state, currentName, document.Name);
            }
            WriteSkillFile(folder, bytes);
            Approve(state, document.Name, Sha256(bytes));
            return Commit(state, document.Name);
        }
    }

    /// <summary>The user read <paramref name="name"/> and turned it on: approves exactly the text they were shown.</summary>
    public SkillEntry Approve(string name, string sha256)
    {
        SkillPaths.RequireName(name);
        if (sha256 is not { Length: 64 } || !sha256.All(char.IsAsciiHexDigitLower))
            throw new ArgumentException("A skill approval is a lowercase SHA-256.", nameof(sha256));
        using (Changing())
        {
            ExistingUserFolder(name);
            var state = LoadState();
            Approve(state, name, sha256);
            return Commit(state, name);
        }
    }

    /// <summary>Turns a skill off, or back on. Turning on an unapproved user skill leaves it waiting for review.</summary>
    public void SetOn(string name, bool on)
    {
        SkillPaths.RequireName(name);
        using (Changing())
        {
            var state = LoadState();
            state.Off.Remove(name);
            if (!on)
                state.Off.Add(name);
            SaveState(state);
            _version++;
        }
    }

    /// <summary>
    /// Removes a user skill's folder; built-ins cannot be removed. A linked folder loses only its link.
    /// <paramref name="deleteFolder"/> lets the app send a real folder to the Recycle Bin.
    /// </summary>
    public void Remove(string name, Action<string>? deleteFolder = null)
    {
        SkillPaths.RequireName(name);
        using (Changing())
        {
            var folder = UserFolder(name);
            if (!Directory.Exists(folder))
                throw new InvalidOperationException($"You have no skill named '{name}'.");
            if (new DirectoryInfo(folder).Attributes.HasFlag(FileAttributes.ReparsePoint))
                Directory.Delete(folder);
            else if (deleteFolder is not null)
                deleteFolder(folder);
            else
                Directory.Delete(folder, recursive: true);
            var state = LoadState();
            Forget(state, name);
            SaveState(state);
            _version++;
        }
    }

    /// <summary>Reads a skill folder, its SKILL.md, or a .zip holding one skill, without writing anything.</summary>
    public SkillImport PrepareImport(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var full = Path.GetFullPath(path);
        (byte[] Bytes, string? Folder, int Others) read;
        if (File.Exists(full) && string.Equals(Path.GetExtension(full), ".zip", StringComparison.OrdinalIgnoreCase))
            read = FromZip(full);
        else if (File.Exists(full) && string.Equals(Path.GetFileName(full), SkillDocument.FileName, StringComparison.OrdinalIgnoreCase))
            read = FromFolder(Path.GetDirectoryName(full)!);
        else if (Directory.Exists(full))
            read = FromFolder(full);
        else
            throw new InvalidOperationException("Choose a skill folder, its SKILL.md, or a .zip file.");

        var parsed = SkillDocument.Parse(read.Bytes, read.Folder);
        if (parsed.Document is not { } document)
            throw new InvalidOperationException(parsed.Error);
        return new SkillImport
        {
            SourcePath = full,
            Document = document,
            Text = Encoding.UTF8.GetString(read.Bytes).TrimStart('\uFEFF'),
            Bytes = read.Bytes,
            Sha256 = Sha256(read.Bytes),
            IgnoredFiles = read.Others,
            ReplacesUserSkill = Directory.Exists(UserFolder(document.Name)),
            OverridesBuiltIn = BuiltInRoot is { } builtIn && Directory.Exists(Path.Combine(builtIn, document.Name)),
        };
    }

    /// <summary>Adds the skill the user was shown, byte for byte, on, with its source recorded (D-4).</summary>
    public SkillEntry CommitImport(SkillImport import, DateTimeOffset? now = null)
    {
        ArgumentNullException.ThrowIfNull(import);
        if (!string.Equals(Sha256(import.Bytes), import.Sha256, StringComparison.Ordinal))
            throw new InvalidOperationException("The skill changed after it was shown. Import it again.");
        var name = import.Document.Name;
        SkillPaths.RequireName(name);
        using (Changing())
        {
            var folder = UserFolder(name);
            if (Directory.Exists(folder))
                ExistingUserFolder(name);
            else
                Directory.CreateDirectory(folder);
            WriteSkillFile(folder, import.Bytes);
            var state = LoadState();
            Approve(state, name, import.Sha256);
            state.Origins[name] = new SkillOrigin(SkillOrigin.Imported, import.SourcePath, now ?? DateTimeOffset.UtcNow);
            return Commit(state, name);
        }
    }

    public static string Sha256(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    // --- scanning ---

    private SkillSnapshot ScanLocked()
    {
        var state = LoadState();
        var entries = new List<SkillEntry>();
        var userNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var folder in Folders(UserRoot))
        {
            userNames.Add(Path.GetFileName(folder));
            entries.Add(ReadEntry(SkillSource.User, UserRoot, folder, state, false));
        }
        var builtIns = new HashSet<string>(StringComparer.Ordinal);
        if (BuiltInRoot is { } builtIn)
        {
            foreach (var folder in Folders(builtIn))
            {
                var name = Path.GetFileName(folder);
                builtIns.Add(name);
                entries.Add(ReadEntry(SkillSource.BuiltIn, builtIn, folder, state, userNames.Contains(name)));
            }
        }
        var all = entries
            .Select(e => e.Source == SkillSource.User && builtIns.Contains(e.Name) ? With(e, overrides: true) : e)
            .OrderBy(e => e.Name, StringComparer.Ordinal)
            .ThenBy(e => e.Source)
            .ToArray();
        _snapshot = new SkillSnapshot(all);
        _scannedAt = Environment.TickCount64;
        _snapshotVersion = _version;
        return _snapshot;
    }

    private static IEnumerable<string> Folders(string root)
    {
        if (!Directory.Exists(root))
            return [];
        try
        {
            return Directory.EnumerateDirectories(root)
                .Where(d => !Path.GetFileName(d).StartsWith('.'))
                .OrderBy(d => d, StringComparer.Ordinal)
                .Take(MaxFolders)
                .ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private SkillEntry ReadEntry(SkillSource source, string root, string folder, State state, bool overridden)
    {
        var name = Path.GetFileName(folder);
        var file = Path.Combine(folder, SkillDocument.FileName);
        string? error = null;
        SkillDocument? document = null;
        string? sha = null;
        long bytes = 0;
        if (SkillDocument.NameProblem(name) is not null)
        {
            error = $"The folder name '{SkillDocument.Shown(name)}' is not a skill name: use lowercase letters a-z, digits and single hyphens.";
        }
        else
        {
            string? path = null;
            try
            {
                path = SkillPaths.TryResolve(name, root);
                if (path is null)
                    error = "This folder has no SKILL.md.";
            }
            catch (InvalidOperationException)
            {
                error = "This skill's folder links outside the skills folder, so Temno does not read it.";
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                error = ex.Message;
            }
            if (path is not null)
            {
                try
                {
                    var read = ReadFile(path, name);
                    (document, error, sha, bytes) = (read.Parse.Document, read.Parse.Error, read.Sha256, read.Length);
                }
                catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
                {
                    error = ex.Message;
                }
            }
        }

        var approved = source == SkillSource.User && sha is not null
            && state.Approved.TryGetValue(name, out var accepted) && accepted == sha;
        var status = (source, error, overridden) switch
        {
            (SkillSource.BuiltIn, _, true) => SkillStatus.Overridden,
            (_, not null, _) => SkillStatus.Invalid,
            _ when state.Off.Contains(name) => SkillStatus.Off,
            (SkillSource.BuiltIn, _, _) => SkillStatus.On,
            _ when approved => SkillStatus.On,
            _ when state.Approved.ContainsKey(name) => SkillStatus.Changed,
            _ => SkillStatus.New,
        };
        return new SkillEntry
        {
            Name = name,
            Source = source,
            Folder = folder,
            FilePath = file,
            Status = status,
            Document = document,
            Error = error,
            Sha256 = sha,
            Bytes = bytes,
            Origin = source == SkillSource.User && state.Origins.TryGetValue(name, out var origin) ? origin : null,
            Approved = approved,
        };
    }

    private static SkillEntry With(SkillEntry e, bool overrides) => new()
    {
        Name = e.Name,
        Source = e.Source,
        Folder = e.Folder,
        FilePath = e.FilePath,
        Status = e.Status,
        Document = e.Document,
        Error = e.Error,
        Sha256 = e.Sha256,
        Bytes = e.Bytes,
        Origin = e.Origin,
        Approved = e.Approved,
        OverridesBuiltIn = overrides,
    };

    // Read and hashed on every scan. A cache keyed on size and write time kept a file rewritten at the same
    // length, with its time put back, on as the text it replaced (T2 round 1, F2).
    private static FileRead ReadFile(string path, string folderName)
    {
        var length = new FileInfo(path).Length;
        if (length > SkillDocument.MaxFileBytes)
            return new FileRead(length, null, SkillParseResult.Fail(SkillDocument.TooLarge(length)));
        var bytes = ReadCapped(path);
        return new FileRead(bytes.Length, Sha256(bytes), SkillDocument.Parse(bytes, folderName));
    }

    // A file-system error names the file, and the file's path holds the Windows user name, which a hosted
    // turn sends to the provider (R-2). The agent gets the id only (T2 round 1, F3).
    private static SkillRead ReadOneQuietly(SkillSource source, string root, string id, State state)
    {
        try
        {
            return ReadOne(source, root, id, state);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException($"Skill '{id}' could not be read just now. Try again, or tell the user.");
        }
    }

    private static SkillRead ReadOne(SkillSource source, string root, string id, State state)
    {
        var path = SkillPaths.TryResolve(id, root)
            ?? throw new InvalidOperationException($"Skill '{id}' has no SKILL.md, so it cannot be read.");
        var bytes = ReadCapped(path);
        var parsed = SkillDocument.Parse(bytes, id);
        if (parsed.Document is not { } document)
            throw new InvalidOperationException($"Skill '{id}' is not valid: {parsed.Error}");
        if (state.Off.Contains(id))
            throw new InvalidOperationException($"Skill '{id}' is off. The user can turn it on in Settings > Skills.");
        if (source == SkillSource.User)
        {
            if (!state.Approved.TryGetValue(id, out var accepted))
                throw new InvalidOperationException(
                    $"Skill '{id}' was added outside Temno and is off until the user reviews it in Settings > Skills.");
            if (accepted != Sha256(bytes))
                throw new InvalidOperationException(
                    $"Skill '{id}' changed outside Temno and is off until the user reviews it in Settings > Skills.");
        }
        return new SkillRead(id, source, document);
    }

    private static byte[] ReadCapped(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (stream.Length > SkillDocument.MaxFileBytes)
            throw new InvalidOperationException(SkillDocument.TooLarge(stream.Length));
        var buffer = new byte[SkillDocument.MaxFileBytes + 1];
        var total = 0;
        int read;
        while (total < buffer.Length && (read = stream.Read(buffer, total, buffer.Length - total)) > 0)
            total += read;
        if (total > SkillDocument.MaxFileBytes)
            throw new InvalidOperationException(SkillDocument.TooLarge(total));
        return buffer[..total];
    }

    // --- import ---

    private static (byte[] Bytes, string? Folder, int Others) FromFolder(string folder)
    {
        var file = Path.Combine(folder, SkillDocument.FileName);
        if (!File.Exists(file))
            throw new InvalidOperationException("This folder has no SKILL.md.");
        var bytes = ReadCapped(file);
        var others = 0;
        try
        {
            others = Directory.EnumerateFiles(folder, "*", new EnumerationOptions
            {
                RecurseSubdirectories = true,
                AttributesToSkip = FileAttributes.ReparsePoint,
                IgnoreInaccessible = true,
            }).Take(MaxListedFiles).Count() - 1;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
        return (bytes, Path.GetFileName(Path.TrimEndingDirectorySeparator(folder)), Math.Max(0, others));
    }

    private static (byte[] Bytes, string? Folder, int Others) FromZip(string path)
    {
        try
        {
            using var zip = ZipFile.OpenRead(path);
            if (zip.Entries.Count > MaxListedFiles)
                throw new InvalidOperationException($"This .zip holds more than {MaxListedFiles:N0} files, too many for one skill.");
            var found = zip.Entries
                .Select(e => (Entry: e, Parts: e.FullName.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries)))
                .Where(x => x.Parts.Length is 1 or 2 && x.Parts[^1] == SkillDocument.FileName && !x.Parts[0].StartsWith("__MACOSX", StringComparison.Ordinal))
                .ToArray();
            if (found.Length == 0)
                throw new InvalidOperationException("This .zip has no SKILL.md at its top level or in a folder at its top level.");
            if (found.Length > 1)
                throw new InvalidOperationException(
                    $"This .zip holds {found.Length} skills ({string.Join(", ", found.Select(f => SkillDocument.Shown(f.Parts[0])).Take(5))}). Import them one at a time.");
            var (entry, parts) = found[0];
            if (entry.Length > SkillDocument.MaxFileBytes)
                throw new InvalidOperationException(SkillDocument.TooLarge(entry.Length));
            using var stream = entry.Open();
            var buffer = new byte[SkillDocument.MaxFileBytes + 1];
            var total = 0;
            int read;
            while (total < buffer.Length && (read = stream.Read(buffer, total, buffer.Length - total)) > 0)
                total += read;
            if (total > SkillDocument.MaxFileBytes)
                throw new InvalidOperationException(SkillDocument.TooLarge(total));
            var others = zip.Entries.Count(e => e.Name.Length > 0) - 1;
            return (buffer[..total], parts.Length == 2 ? parts[0] : null, others);
        }
        catch (InvalidDataException ex)
        {
            throw new InvalidOperationException("This .zip could not be read: " + ex.Message);
        }
    }

    // --- writing ---

    private static (SkillDocument Document, byte[] Bytes) Prepare(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        // WinUI's TextBox hands back CR line endings; skills are written with LF, without a BOM.
        var bytes = new UTF8Encoding(false).GetBytes(text.ReplaceLineEndings("\n"));
        var parsed = SkillDocument.Parse(bytes, folderName: null);
        return parsed.Document is { } document ? (document, bytes) : throw new InvalidOperationException(parsed.Error);
    }

    private string UserFolder(string name)
    {
        var folder = Path.GetFullPath(Path.Combine(UserRoot, name));
        if (!PathContainment.IsLexicalChild(UserRoot, folder))
            throw new InvalidOperationException($"'{SkillDocument.Shown(name)}' is not a skill name.");
        return folder;
    }

    // An existing user folder that a write may use: one that is not a link out of the skills folder.
    private string ExistingUserFolder(string name)
    {
        var folder = UserFolder(name);
        if (!Directory.Exists(folder))
            throw new InvalidOperationException($"You have no skill named '{name}'.");
        if (!PathContainment.IsInside(UserRoot, folder))
            throw new InvalidOperationException($"'{name}' links outside the skills folder, so Temno will not write to it.");
        return folder;
    }

    private static void WriteSkillFile(string folder, byte[] bytes)
    {
        var target = Path.Combine(folder, SkillDocument.FileName);
        var temp = Path.Combine(folder, $".{SkillDocument.FileName}.{Guid.NewGuid():N}.tmp");
        File.WriteAllBytes(temp, bytes);
        File.Move(temp, target, overwrite: true);
    }

    private SkillEntry Commit(State state, string name)
    {
        SaveState(state);
        _version++;
        var snapshot = ScanLocked();
        return snapshot.Find(name, SkillSource.User)
            ?? throw new InvalidOperationException($"'{name}' was written but could not be read back.");
    }

    // --- state ---

    // Temno and Temno-Dev share the state file (R-4), so a change reads, edits and writes it under one lock held
    // across processes as well as threads; otherwise one app's save could undo the other's (T2 round 1, F4).
    private StateLock Changing() => new(this);

    private string MutexName =>
        "Temno-Skills-" + Sha256(Encoding.UTF8.GetBytes(StatePath.ToUpperInvariant()))[..16];

    private sealed class StateLock : IDisposable
    {
        private readonly SkillStore _store;
        private readonly Mutex _mutex;

        // Any failure to take the mutex releases the store's lock and surfaces as InvalidOperationException,
        // which every caller handles (T2 round 2, F4: a name held by another kind of handle threw out of here
        // with the lock still held).
        public StateLock(SkillStore store)
        {
            _store = store;
            Monitor.Enter(store._gate);
            Mutex? mutex = null;
            try
            {
                mutex = new Mutex(false, store.MutexName);
                bool owned;
                try
                {
                    owned = mutex.WaitOne(TimeSpan.FromSeconds(10));
                }
                catch (AbandonedMutexException)
                {
                    owned = true;
                }
                if (!owned)
                    throw new InvalidOperationException("Another Temno window is changing skills. Try again.");
                _mutex = mutex;
            }
            catch (Exception ex)
            {
                mutex?.Dispose();
                Monitor.Exit(store._gate);
                if (ex is InvalidOperationException)
                    throw;
                throw new InvalidOperationException("Skills could not be locked for this change. Try again.", ex);
            }
        }

        public void Dispose()
        {
            _mutex.ReleaseMutex();
            _mutex.Dispose();
            Monitor.Exit(_store._gate);
        }
    }

    private static void Approve(State state, string name, string sha256)
    {
        state.Approved[name] = sha256;
        state.Off.Remove(name);
    }

    private static void Rename(State state, string from, string to)
    {
        if (state.Origins.Remove(from, out var origin))
            state.Origins[to] = origin;
        if (state.Off.Remove(from))
            state.Off.Add(to);
        state.Approved.Remove(from);
    }

    private static void Forget(State state, string name)
    {
        state.Approved.Remove(name);
        state.Off.Remove(name);
        state.Origins.Remove(name);
    }

    // A missing or unreadable state file approves nothing: user skills then wait for review (fail closed).
    private State LoadState()
    {
        try
        {
            if (!File.Exists(StatePath))
                return new State();
            var info = new FileInfo(StatePath);
            if (info.Length > 4 * 1024 * 1024)
                return new State();
            var file = JsonSerializer.Deserialize<StateFile>(File.ReadAllBytes(StatePath), StateJson) ?? new StateFile();
            var state = new State();
            foreach (var (name, sha) in file.Approved ?? [])
                if (SkillDocument.IsValidName(name) && sha is { Length: 64 } && sha.All(char.IsAsciiHexDigitLower))
                    state.Approved[name] = sha;
            foreach (var name in file.Off ?? [])
                if (SkillDocument.IsValidName(name))
                    state.Off.Add(name);
            foreach (var (name, origin) in file.Origins ?? [])
                if (SkillDocument.IsValidName(name) && origin is not null)
                    state.Origins[name] = origin;
            return state;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new State();
        }
    }

    private void SaveState(State state)
    {
        var file = new StateFile
        {
            Approved = state.Approved.OrderBy(p => p.Key, StringComparer.Ordinal).ToDictionary(p => p.Key, p => p.Value),
            Off = [.. state.Off.Order(StringComparer.Ordinal)],
            Origins = state.Origins.OrderBy(p => p.Key, StringComparer.Ordinal).ToDictionary(p => p.Key, p => p.Value),
        };
        Directory.CreateDirectory(Path.GetDirectoryName(StatePath)!);
        var temp = StatePath + $".{Guid.NewGuid():N}.tmp";
        File.WriteAllBytes(temp, JsonSerializer.SerializeToUtf8Bytes(file, StateJson));
        File.Move(temp, StatePath, overwrite: true);
    }

    private sealed class State
    {
        public Dictionary<string, string> Approved { get; } = new(StringComparer.Ordinal);
        public HashSet<string> Off { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, SkillOrigin> Origins { get; } = new(StringComparer.Ordinal);
    }

    private sealed class StateFile
    {
        public int Version { get; set; } = 1;
        public Dictionary<string, string>? Approved { get; set; }
        public List<string>? Off { get; set; }
        public Dictionary<string, SkillOrigin>? Origins { get; set; }
    }

    private sealed record FileRead(long Length, string? Sha256, SkillParseResult Parse);
}

/// <summary>
/// Resolves a skill id to a <c>SKILL.md</c> under a root. Ids that leave a root, including through a reparse
/// point, are refused (WP-208); a root that is itself a link is followed.
/// </summary>
public static class SkillPaths
{
    public static void RequireName(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
            throw new InvalidOperationException("read_skill: id is required");
        if (!SkillDocument.IsValidName(id))
            throw new InvalidOperationException($"read_skill: id '{SkillDocument.Shown(id)}' is not a skill name.");
    }

    /// <summary>The final path of <paramref name="id"/>'s SKILL.md under <paramref name="root"/>, or null when it has none.</summary>
    public static string? TryResolve(string id, string root)
    {
        RequireName(id);
        var rootFull = Path.GetFullPath(root);
        var lexical = Path.GetFullPath(Path.Combine(rootFull, id, SkillDocument.FileName));
        if (!PathContainment.IsLexicalChild(rootFull, lexical))
            throw new InvalidOperationException($"read_skill: id '{id}' escapes its skill root.");
        if (!File.Exists(lexical))
            return null;
        var finalPath = PathContainment.Resolve(lexical);
        if (!PathContainment.IsInside(rootFull, finalPath))
            throw new InvalidOperationException($"read_skill: '{id}' resolves outside its skill root.");
        return finalPath;
    }

    public static string Require(string id, IReadOnlyList<string> roots)
    {
        RequireName(id);
        foreach (var root in roots)
        {
            if (!string.IsNullOrWhiteSpace(root) && TryResolve(id, root) is { } path)
                return path;
        }
        throw new InvalidOperationException(NotFound(id));
    }

    public static string NotFound(string id) =>
        $"Skill '{id}' not found. Skills live in ~/.temno/skills and in the app's skills folder; the user manages them in Settings > Skills.";
}
