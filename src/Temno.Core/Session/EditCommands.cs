using Temno.Core.Models;
using Temno.Core.Editing;
using System.Text.Json;

namespace Temno.Core.Session;

public readonly record struct EditResult(bool Ok, string? Error = null)
{
    public static EditResult Success() => new(true);
    public static EditResult Fail(string error) => new(false, error);
}

/// <summary>One clip's target placement in an atomic multi-clip move.</summary>
public readonly record struct ClipMove(string ClipId, int TrackIndex, int NewStartFrame);

/// <summary>One clip edge adjustment in an atomic multi-clip trim.</summary>
public readonly record struct ClipEdgeTrim(string ClipId, bool LeftEdge, int EdgeFrame);

/// <summary>
/// High-level mutations for UI/MCP (WP-07). Minimal set; expand to full Mac tool surface.
/// </summary>
public static partial class EditCommands
{
    private static EditResult ExecuteWithUndo(ProjectSession session, Func<EditResult> action)
    {
        session.RecordBeforeEdit();
        var result = action();
        if (!result.Ok)
        {
            session.RollbackLastRecord();
        }
        return result;
    }

}
