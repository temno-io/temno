using System.Text.Json;
using Temno.Core.Project;

namespace Temno.Core.Agent;

public static class ChatSessionStore
{
    public static string DirectoryName => ProjectConstants.ChatDirectoryName;

    public static string DirectoryPath(string packageDirectory) =>
        Path.Combine(packageDirectory, DirectoryName);

    public static string FileName(ChatSession session) => FileName(session.Id);

    public static string FileName(Guid id) => id.ToString("D") + ".json";

    public static byte[] Encode(ChatSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        lock (session.SyncRoot)
            return JsonSerializer.SerializeToUtf8Bytes(session, ChatJson.Options);
    }

    public static ChatSession Snapshot(ChatSession session) => Decode(Encode(session));

    public static ChatSession Decode(ReadOnlySpan<byte> utf8Json)
    {
        var session = JsonSerializer.Deserialize<ChatSession>(utf8Json, ChatJson.Options);
        if (session is null)
            throw new JsonException("Chat session JSON was empty.");
        return session;
    }

    public static ChatSession Decode(string json) =>
        Decode(System.Text.Encoding.UTF8.GetBytes(json));

    public static IReadOnlyList<ChatSession> Load(string? packageDirectory)
    {
        if (string.IsNullOrWhiteSpace(packageDirectory))
            return [];

        var dir = DirectoryPath(packageDirectory);
        if (!Directory.Exists(dir))
            return [];

        var sessions = new List<ChatSession>();
        foreach (var file in Directory.EnumerateFiles(dir, "*.json"))
        {
            try
            {
                sessions.Add(Decode(File.ReadAllBytes(file)));
            }
            catch (JsonException)
            {
                // Skip unreadable session files the same way Mac compactMaps decode failures.
            }
        }

        sessions.Sort((a, b) => b.UpdatedAt.CompareTo(a.UpdatedAt));
        return sessions;
    }

    public static void Save(string packageDirectory, ChatSession session)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageDirectory);
        ArgumentNullException.ThrowIfNull(session);

        lock (session.SyncRoot)
        {
            var dir = DirectoryPath(packageDirectory);
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, FileName(session));
            var bytes = Encode(session);
            var tmp = path + ".tmp";
            File.WriteAllBytes(tmp, bytes);
            File.Move(tmp, path, overwrite: true);
        }
    }

    /// <summary>Removes every file in <c>chat/</c> that holds the chat <paramref name="id"/>.</summary>
    public static void Delete(string packageDirectory, Guid id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageDirectory);

        var dir = DirectoryPath(packageDirectory);
        if (!Directory.Exists(dir))
            return;

        foreach (var file in Directory.EnumerateFiles(dir, "*.json").ToList())
        {
            if (StoredId(file) == id)
                File.Delete(file);
        }
    }

    // Mac names a file by its uppercase uuidString and Windows by a lowercase id; any other name is read.
    private static Guid? StoredId(string file)
    {
        if (Guid.TryParse(Path.GetFileNameWithoutExtension(file), out var named))
            return named;
        try
        {
            return Decode(File.ReadAllBytes(file)).Id;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static void SaveAll(string packageDirectory, IEnumerable<ChatSession> sessions)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageDirectory);
        ArgumentNullException.ThrowIfNull(sessions);

        var dir = DirectoryPath(packageDirectory);
        if (Directory.Exists(dir))
            Directory.Delete(dir, recursive: true);
        Directory.CreateDirectory(dir);

        foreach (var session in sessions)
            Save(packageDirectory, session);
    }
}
