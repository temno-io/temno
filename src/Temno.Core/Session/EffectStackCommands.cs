using Temno.Core.Editing;
using Temno.Core.Models;

namespace Temno.Core.Session;

/// <summary>
/// Undoable, effect-id-oriented mutations for the editor effect stack.
/// Unlike MCP's type-oriented apply_effect tool, these operations preserve the
/// order of the stack and can safely operate around effects a Windows renderer
/// does not yet understand.
/// </summary>
public static class EffectStackCommands
{
    public static EditResult AddEffect(ProjectSession session, string clipId, string type)
        => ExecuteWithUndo(session, () => AddEffectInPlace(session, clipId, type));

    public static EditResult AddEffectInPlace(ProjectSession session, string clipId, string type)
    {
        ArgumentNullException.ThrowIfNull(session);

        if (string.IsNullOrWhiteSpace(type))
            return EditResult.Fail("Effect type is required.");

        var descriptor = EffectRegistry.GetDescriptor(type);
        if (descriptor is null)
            return EditResult.Fail($"Unknown effect '{type}'.");

        var clipResult = FindEffectCapableClip(session, clipId, out var clip);
        if (!clipResult.Ok) return clipResult;

        var stack = clip!.Effects ?? new List<Effect>();
        stack.Add(descriptor.MakeEffect());
        clip.Effects = stack;
        session.MarkDirty();
        return EditResult.Success();
    }

    public static EditResult RemoveEffect(ProjectSession session, string clipId, string effectId)
        => ExecuteWithUndo(session, () => RemoveEffectInPlace(session, clipId, effectId));

    public static EditResult RemoveEffectInPlace(ProjectSession session, string clipId, string effectId)
    {
        ArgumentNullException.ThrowIfNull(session);

        var effectResult = FindEffect(session, clipId, effectId, out var clip, out _, out var index);
        if (!effectResult.Ok) return effectResult;

        clip!.Effects!.RemoveAt(index);
        if (clip.Effects.Count == 0) clip.Effects = null;
        session.MarkDirty();
        return EditResult.Success();
    }

    public static EditResult SetEffectEnabled(ProjectSession session, string clipId, string effectId, bool enabled)
    {
        ArgumentNullException.ThrowIfNull(session);

        return ExecuteWithUndo(session, () =>
        {
            var effectResult = FindEffect(session, clipId, effectId, out _, out var effect, out _);
            if (!effectResult.Ok) return effectResult;

            effect!.Enabled = enabled;
            session.MarkDirty();
            return EditResult.Success();
        });
    }

    /// <summary>
    /// Moves an effect to its final zero-based position in the stack. The target
    /// position is evaluated before the effect is removed, which makes adjacent
    /// up/down UI actions intuitive.
    /// </summary>
    public static EditResult MoveEffect(ProjectSession session, string clipId, string effectId, int destinationIndex)
    {
        ArgumentNullException.ThrowIfNull(session);

        return ExecuteWithUndo(session, () =>
        {
            var effectResult = FindEffect(session, clipId, effectId, out var clip, out var effect, out var sourceIndex);
            if (!effectResult.Ok) return effectResult;

            var stack = clip!.Effects!;
            if (destinationIndex < 0 || destinationIndex >= stack.Count)
                return EditResult.Fail($"Effect destination index {destinationIndex} is out of range.");
            if (destinationIndex == sourceIndex)
                return EditResult.Fail("Effect is already at that position.");

            stack.RemoveAt(sourceIndex);
            stack.Insert(destinationIndex, effect!);
            session.MarkDirty();
            return EditResult.Success();
        });
    }

    public static EditResult SetEffectNumericParam(
        ProjectSession session,
        string clipId,
        string effectId,
        string parameter,
        double value)
        => SetEffectNumericParams(session, clipId, effectId, new Dictionary<string, double> { [parameter] = value });

    /// <summary>
    /// Applies numeric values for a known effect in one undo entry. Existing
    /// string and keyframe data on edited parameters is retained so a base-value
    /// edit cannot silently discard Mac-compatible data.
    /// </summary>
    public static EditResult SetEffectNumericParams(
        ProjectSession session,
        string clipId,
        string effectId,
        IReadOnlyDictionary<string, double>? values)
    {
        ArgumentNullException.ThrowIfNull(session);

        if (values is null || values.Count == 0)
            return EditResult.Fail("At least one numeric effect parameter is required.");

        return ExecuteWithUndo(session, () => ApplyEffectNumericParams(session, clipId, effectId, values));
    }

    /// <summary>
    /// Same write as <see cref="SetEffectNumericParams"/> without recording undo.
    /// Caller owns the gesture (inspector live scrub via <c>ClipLiveEdit</c>).
    /// </summary>
    public static EditResult SetEffectNumericParamsInPlace(
        ProjectSession session,
        string clipId,
        string effectId,
        IReadOnlyDictionary<string, double>? values)
    {
        ArgumentNullException.ThrowIfNull(session);

        if (values is null || values.Count == 0)
            return EditResult.Fail("At least one numeric effect parameter is required.");

        return ApplyEffectNumericParams(session, clipId, effectId, values);
    }

    /// <summary>
    /// Adds, updates, or removes a catalog effect without recording undo.
    /// Identity values (all params at defaults) skip a missing effect and remove an existing one.
    /// Caller owns the gesture (inspector grade LostFocus).
    /// </summary>
    public static EditResult ApplyNumericEffectInPlace(
        ProjectSession session,
        string clipId,
        string type,
        IReadOnlyDictionary<string, double> values,
        out bool changed)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(values);
        changed = false;

        var descriptor = EffectRegistry.GetDescriptor(type);
        if (descriptor is null)
            return EditResult.Fail($"Unknown effect '{type}'.");

        var clipResult = FindEffectCapableClip(session, clipId, out var clip);
        if (!clipResult.Ok) return clipResult;

        var identity = IsIdentity(descriptor, values);
        var stack = clip!.Effects ?? [];
        var existing = stack.FirstOrDefault(effect => effect.Type == type);

        if (identity)
        {
            if (existing is null)
                return EditResult.Success();
            stack.Remove(existing);
            clip.Effects = stack.Count == 0 ? null : stack;
            session.MarkDirty();
            changed = true;
            return EditResult.Success();
        }

        if (existing is null)
        {
            existing = descriptor.MakeEffect();
            stack.Add(existing);
            clip.Effects = stack;
        }
        else if (!NumericValuesWouldChange(existing, descriptor, values))
        {
            return EditResult.Success();
        }

        var apply = ApplyEffectNumericParams(session, clipId, existing.Id, values);
        if (apply.Ok)
            changed = true;
        return apply;
    }

    private static bool NumericValuesWouldChange(
        Effect effect,
        EffectDescriptor descriptor,
        IReadOnlyDictionary<string, double> values)
    {
        var specs = descriptor.Params.ToDictionary(spec => spec.Key, StringComparer.Ordinal);
        foreach (var (key, value) in values)
        {
            if (!specs.TryGetValue(key, out var spec) || !double.IsFinite(value))
                return true;
            var rounded = Math.Round(Math.Clamp(value, spec.Min, spec.Max) * 1000.0) / 1000.0;
            if (!effect.Params.TryGetValue(key, out var param) || param.Value is not { } current)
                return true;
            if (Math.Abs(current - rounded) > 0.0005)
                return true;
        }

        return false;
    }

    private static bool IsIdentity(EffectDescriptor descriptor, IReadOnlyDictionary<string, double> values)
    {
        if (descriptor.Params.Count == 0)
            return values.Count == 0;
        foreach (var spec in descriptor.Params)
        {
            if (!values.TryGetValue(spec.Key, out var value) || !double.IsFinite(value))
                return false;
            var rounded = Math.Round(Math.Clamp(value, spec.Min, spec.Max) * 1000.0) / 1000.0;
            if (Math.Abs(rounded - spec.DefaultValue) > 0.0005)
                return false;
        }

        return true;
    }

    private static EditResult ApplyEffectNumericParams(
        ProjectSession session,
        string clipId,
        string effectId,
        IReadOnlyDictionary<string, double> values)
    {
        var effectResult = FindEffect(session, clipId, effectId, out _, out var effect, out _);
        if (!effectResult.Ok) return effectResult;

        var descriptor = EffectRegistry.GetDescriptor(effect!.Type);
        if (descriptor is null)
            return EditResult.Fail($"Effect '{effect.Type}' is unsupported; its parameters are preserved but cannot be edited.");

        var specs = descriptor.Params.ToDictionary(spec => spec.Key, StringComparer.Ordinal);
        foreach (var (key, value) in values)
        {
            if (!specs.ContainsKey(key))
                return EditResult.Fail($"{effect.Type}: unknown numeric parameter '{key}'.");
            if (!double.IsFinite(value))
                return EditResult.Fail($"{effect.Type}.{key} must be a finite number.");
        }

        foreach (var (key, value) in values)
        {
            var spec = specs[key];
            var clamped = Math.Clamp(value, spec.Min, spec.Max);
            var rounded = Math.Round(clamped * 1000.0) / 1000.0;
            if (!effect.Params.TryGetValue(key, out var param))
                param = new EffectParam();
            param.Value = rounded;
            effect.Params[key] = param;
        }

        session.MarkDirty();
        return EditResult.Success();
    }

    /// <summary>
    /// Sets a string effect parameter (e.g. <c>color.curves.curve</c>, <c>color.lut.path</c>)
    /// in one undo entry. Preserves any existing numeric Value / keyframes on the same key.
    /// </summary>
    public static EditResult SetEffectStringParam(
        ProjectSession session,
        string clipId,
        string effectId,
        string parameter,
        string? value)
        => ExecuteWithUndo(session, () => SetEffectStringParamInPlace(session, clipId, effectId, parameter, value));

    public static EditResult SetEffectStringParamInPlace(
        ProjectSession session,
        string clipId,
        string effectId,
        string parameter,
        string? value)
    {
        ArgumentNullException.ThrowIfNull(session);

        if (string.IsNullOrWhiteSpace(parameter))
            return EditResult.Fail("Effect parameter name is required.");

        var effectResult = FindEffect(session, clipId, effectId, out _, out var effect, out _);
        if (!effectResult.Ok) return effectResult;

        if (!effect!.Params.TryGetValue(parameter, out var param))
            param = new EffectParam();
        param.String = value;
        effect.Params[parameter] = param;

        session.MarkDirty();
        return EditResult.Success();
    }

    private static EditResult FindEffectCapableClip(ProjectSession session, string clipId, out Clip? clip)
    {
        clip = null;
        if (string.IsNullOrWhiteSpace(clipId))
            return EditResult.Fail("Clip id is required.");

        Track? ownerTrack = null;
        foreach (var track in session.ActiveTimeline.Tracks)
        {
            clip = track.Clips.FirstOrDefault(candidate => candidate.Id == clipId);
            if (clip is not null)
            {
                ownerTrack = track;
                break;
            }
        }

        if (clip is null)
            return EditResult.Fail($"Clip not found: {clipId}");
        if (ownerTrack!.Locked)
            return EditResult.Fail(EditCommands.TrackLockedError);
        if (clip.MediaType is not (ClipType.video or ClipType.image))
            return EditResult.Fail($"Clip {clipId} is a {clip.MediaType} clip; effects require a video or image clip.");

        return EditResult.Success();
    }

    private static EditResult FindEffect(
        ProjectSession session,
        string clipId,
        string effectId,
        out Clip? clip,
        out Effect? effect,
        out int index)
    {
        effect = null;
        index = -1;
        var clipResult = FindEffectCapableClip(session, clipId, out clip);
        if (!clipResult.Ok) return clipResult;
        if (string.IsNullOrWhiteSpace(effectId))
            return EditResult.Fail("Effect id is required.");
        if (clip!.Effects is not { Count: > 0 })
            return EditResult.Fail($"Clip {clipId} has no effects.");

        index = clip.Effects.FindIndex(candidate => candidate.Id == effectId);
        if (index < 0)
            return EditResult.Fail($"Effect not found: {effectId}");

        effect = clip.Effects[index];
        return EditResult.Success();
    }

    private static EditResult ExecuteWithUndo(ProjectSession session, Func<EditResult> action)
    {
        session.RecordBeforeEdit();
        var result = action();
        if (!result.Ok) session.RollbackLastRecord();
        return result;
    }
}
