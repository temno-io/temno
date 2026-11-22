namespace Temno.Core.Editing;

/// <summary>What holds keyboard focus when an undo or redo key arrives.</summary>
public enum EditorKeySurface
{
    /// <summary>Nothing that types: the timeline, the preview, a button.</summary>
    None,

    /// <summary>A control with no text undo of its own: a closed, non-editable drop-down or a slider.</summary>
    NoTextUndo,

    /// <summary>A number field whose text is the value already committed to the project.</summary>
    CommittedField,

    /// <summary>A number field holding typing that has not been committed.</summary>
    PendingField,

    /// <summary>A surface with its own text undo: chat, captions, search, a plain text box, a dialog.</summary>
    FreeText,
}

public enum EditorHistoryCommand
{
    None,
    Undo,
    Redo,
}

/// <summary>
/// WP-253: who takes Ctrl+Z, Ctrl+Y and Ctrl+Shift+Z in the editor. Typing that has not been
/// committed keeps its text undo; everywhere else the keys are the project's undo and redo, as in
/// Premiere, Resolve and Figma. WinUI-free so the rule is pinned in Core.Tests.
/// </summary>
public static class EditorUndoRouting
{
    /// <summary>The history command a key names: Ctrl+Z undoes, Ctrl+Y and Ctrl+Shift+Z redo.</summary>
    public static EditorHistoryCommand CommandFor(char key, bool ctrl, bool shift)
    {
        if (!ctrl) return EditorHistoryCommand.None;
        return char.ToUpperInvariant(key) switch
        {
            'Z' => shift ? EditorHistoryCommand.Redo : EditorHistoryCommand.Undo,
            'Y' => EditorHistoryCommand.Redo,
            _ => EditorHistoryCommand.None,
        };
    }

    /// <summary>
    /// The project command to run for a key from <paramref name="surface"/>, or
    /// <see cref="EditorHistoryCommand.None"/> to leave the key with the focused control.
    /// An open drop-down keeps every key.
    /// </summary>
    public static EditorHistoryCommand Route(EditorKeySurface surface, bool dropDownOpen, char key, bool ctrl, bool shift)
    {
        var command = CommandFor(key, ctrl, shift);
        if (command == EditorHistoryCommand.None || dropDownOpen) return EditorHistoryCommand.None;
        return surface is EditorKeySurface.None or EditorKeySurface.NoTextUndo or EditorKeySurface.CommittedField
            ? command
            : EditorHistoryCommand.None;
    }
}
