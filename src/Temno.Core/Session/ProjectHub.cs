using System;
using System.IO;
using System.Linq;
using Temno.Core.Project;

namespace Temno.Core.Session;

public static class ProjectHub
{
    private static readonly object ExecutionGate = new();
    private static IProjectSessionDispatcher? _dispatcher;
    private static ProjectSession? _activeSession;

    public static event EventHandler? ActiveSessionChanged;

    public static ProjectSession? ActiveSession
    {
        get
        {
            lock (ExecutionGate)
                return _activeSession;
        }
        set
        {
            bool changed;
            lock (ExecutionGate)
            {
                changed = !ReferenceEquals(_activeSession, value);
                _activeSession = value;
            }
            if (changed)
                ActiveSessionChanged?.Invoke(null, EventArgs.Empty);
        }
    }

    public static void ConfigureDispatcher(IProjectSessionDispatcher? dispatcher)
    {
        lock (ExecutionGate)
            _dispatcher = dispatcher;
    }

    public static T RunSerialized<T>(Func<T> operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        IProjectSessionDispatcher? dispatcher;
        lock (ExecutionGate)
            dispatcher = _dispatcher;

        if (dispatcher is not null && !dispatcher.HasThreadAccess)
            return dispatcher.Invoke(() => RunUnderGate(operation));
        return RunUnderGate(operation);
    }

    public static void RunSerialized(Action operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        RunSerialized(() =>
        {
            operation();
            return true;
        });
    }

    /// <summary>
    /// Mutual exclusion only: takes the same gate as <see cref="RunSerialized{T}(Func{T})"/> and
    /// never marshals to the dispatcher. For work that must not interleave with another caller's
    /// view of the session's collections, but has no business on the UI thread.
    /// </summary>
    /// <remarks>
    /// <para><see cref="RunSerialized{T}(Func{T})"/> blocks a background caller on a round-trip to
    /// the UI thread. That is right when the operation's side effects belong there, and wrong inside
    /// a render loop: the cost lands once per nested layer per frame, and a caller that already holds
    /// a lock the UI thread wants — <c>ToolExecutor</c>'s session lock, say — deadlocks against it.
    /// It is also just noise for a mutation that is already running on the UI thread.</para>
    /// <para>Reentrant, so a caller that already holds the gate through
    /// <see cref="RunSerialized{T}(Func{T})"/> may nest this. Keep the operation short: it runs under
    /// a process-wide lock.</para>
    /// </remarks>
    public static T RunUnderGate<T>(Func<T> operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        lock (ExecutionGate)
            return operation();
    }

    public static string ResolvePath(string path)
    {
        if (string.IsNullOrEmpty(path)) return path;

        if (path.StartsWith("~"))
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            path = Path.Combine(home, path.Substring(1).TrimStart('/', '\\'));
        }

        return Path.GetFullPath(path);
    }

    public static string ResolveProjectUrl(string? name, string? id, string? path)
    {
        if (!string.IsNullOrEmpty(path))
        {
            return ResolvePath(path);
        }

        var entries = ProjectRegistry.Load();

        if (!string.IsNullOrEmpty(id))
        {
            var parsedId = Guid.TryParse(id, out var guid) ? guid : Guid.Empty;
            var match = entries.FirstOrDefault(e => e.Id == parsedId);
            if (match == null)
            {
                throw new InvalidOperationException($"No project with id {id}. Call get_projects for valid ids.");
            }
            return match.Url.StartsWith("file:") ? new Uri(match.Url).LocalPath : match.Url;
        }

        if (!string.IsNullOrEmpty(name))
        {
            var matches = entries.Where(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase)).ToList();
            if (matches.Count == 1)
            {
                var match = matches[0];
                return match.Url.StartsWith("file:") ? new Uri(match.Url).LocalPath : match.Url;
            }
            if (matches.Count == 0)
            {
                var known = string.Join(", ", entries.OrderByDescending(e => e.LastOpenedDate).Take(15).Select(e => $"'{e.Name}'"));
                throw new InvalidOperationException($"No project named '{name}'. Known projects: {known}. Call get_projects for the full list.");
            }
            var paths = string.Join(", ", matches.Select(e => e.Url));
            throw new InvalidOperationException($"{matches.Count} projects are named '{name}'. Pick one by path: {paths}");
        }

        throw new InvalidOperationException("Need a name, an id (from get_projects), or a path.");
    }
}
