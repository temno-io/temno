namespace Temno.Core.Session;

/// <summary>
/// One undo-scoped live gesture for inspector scrub: RecordBeforeEdit once, mutate in place,
/// commit (keep undo) or rollback if unchanged. Mac: applyClipProperty / commitClipProperty.
/// </summary>
public sealed class ClipLiveEdit
{
    private ProjectSession? _session;
    private bool _active;
    private bool _mutated;

    public bool IsActive => _active;

    public void Begin(ProjectSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (_active)
            throw new InvalidOperationException("ClipLiveEdit is already active.");

        session.RecordBeforeEdit();
        _session = session;
        _active = true;
        _mutated = false;
    }

    /// <summary>Mutate the open project without recording additional undo steps.</summary>
    public void Mutate(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (!_active || _session is null)
            throw new InvalidOperationException("ClipLiveEdit.Begin must be called first.");

        action();
        // WP-153: dirty, but no signal until the gesture ends. A scrub calls this on every value
        // change; signalling each one would have the editor redraw mid-drag, and its redraw commits
        // the very gesture still in progress.
        _session.MarkDirtyWithinGesture();
        _mutated = true;
    }

    /// <summary>
    /// Ends the gesture. If <paramref name="keep"/> is false or no mutation occurred,
    /// rolls back the pre-gesture snapshot. Otherwise leaves one undo step.
    /// </summary>
    public void End(bool keep = true)
    {
        if (!_active || _session is null) return;

        if (!keep || !_mutated)
            _session.RollbackLastRecord();   // signals: the content went back
        else
            _session.MarkDirty();            // signals once, for the whole gesture

        _session = null;
        _active = false;
        _mutated = false;
    }
}
