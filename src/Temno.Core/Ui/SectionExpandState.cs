namespace Temno.Core.Ui;

/// <summary>
/// Remembers whether a collapsible editor section is expanded, keyed by a stable section id.
/// </summary>
/// <remarks>
/// <para>
/// In-memory by default, so expand state survives tab switches and panel rebuilds within a session.
/// Durable persistence is a host concern: assign <see cref="Store"/> once at startup to back it with
/// <c>user-preferences.json</c> (WP-113). WP-128 is expected to do that when it recomposes the
/// Captions tab — until then "persists per section id" means per session, and this file is the
/// single place that changes.
/// </para>
/// <para>WinUI-free so the semantics are testable. See WP-127.</para>
/// </remarks>
public static class SectionExpandState
{
    /// <summary>Backing store. Replace at startup to persist across restarts.</summary>
    public interface IStore
    {
        bool? Get(string sectionId);
        void Set(string sectionId, bool expanded);
    }

    private sealed class MemoryStore : IStore
    {
        private readonly Dictionary<string, bool> _values = new(StringComparer.Ordinal);

        public bool? Get(string sectionId)
            => _values.TryGetValue(sectionId, out var v) ? v : null;

        public void Set(string sectionId, bool expanded) => _values[sectionId] = expanded;
    }

    private static IStore _store = new MemoryStore();

    public static IStore Store
    {
        get => _store;
        set => _store = value ?? throw new ArgumentNullException(nameof(value));
    }

    /// <summary>
    /// Expanded state for <paramref name="sectionId"/>, or <paramref name="fallback"/> when the
    /// user has never touched it. A blank id is not remembered — it returns the fallback and
    /// writes nothing, so an unnamed section cannot collide with a named one.
    /// </summary>
    public static bool IsExpanded(string? sectionId, bool fallback = true)
        => string.IsNullOrWhiteSpace(sectionId) ? fallback : _store.Get(sectionId!) ?? fallback;

    public static void SetExpanded(string? sectionId, bool expanded)
    {
        if (string.IsNullOrWhiteSpace(sectionId)) return;
        _store.Set(sectionId!, expanded);
    }

    /// <summary>Drops every remembered value. Test hook; also used when switching projects.</summary>
    public static void Reset() => _store = new MemoryStore();
}
