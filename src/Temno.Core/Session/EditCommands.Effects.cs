using Temno.Core.Models;
using Temno.Core.Editing;

namespace Temno.Core.Session;

public static partial class EditCommands
{
    private static void ApplyEffectFields(Effect effect, EffectEntry entry, EffectDescriptor descriptor)
    {
        if (entry.Enabled.HasValue)
            effect.Enabled = entry.Enabled.Value;
        if (entry.Params is null)
            return;
        effect.Params ??= new Dictionary<string, EffectParam>();
        foreach (var spec in descriptor.Params)
        {
            if (!entry.Params.TryGetValue(spec.Key, out var value))
                continue;
            var clamped = Math.Max(spec.Min, Math.Min(spec.Max, value));
            effect.Params[spec.Key] = new EffectParam { Value = Math.Round(clamped * 1000.0) / 1000.0 };
        }
    }

    public static EditResult ApplyEffect(
        ProjectSession session,
        IReadOnlyList<string> clipIds,
        IReadOnlyList<EffectEntry>? effectsToApply,
        IReadOnlyList<string>? removeTypes)
    {
        return ExecuteWithUndo(session, () =>
        {
            var adds = effectsToApply ?? Array.Empty<EffectEntry>();
            var removes = removeTypes ?? Array.Empty<string>();

            if (adds.Count == 0 && removes.Count == 0)
                return EditResult.Fail("Provide effects to add/update or remove types to delete.");

            // 1. Validate additions upfront
            foreach (var e in adds)
            {
                var descriptor = EffectRegistry.GetDescriptor(e.Type);
                if (descriptor == null)
                {
                    return EditResult.Fail($"Unknown effect '{e.Type}'. See the apply_effect description for available types.");
                }
                if (e.Type.StartsWith("color."))
                {
                    return EditResult.Fail($"'{e.Type}' is a color grade — use apply_color, not apply_effect.");
                }
                if (e.Params != null)
                {
                    var allowed = descriptor.Params.Select(p => p.Key).ToHashSet();
                    var unknown = e.Params.Keys.Where(k => !allowed.Contains(k)).ToList();
                    if (unknown.Count > 0)
                    {
                        unknown.Sort();
                        return EditResult.Fail($"{e.Type}: unknown param(s) '{string.Join("', '", unknown)}'. Allowed: {string.Join(", ", allowed.OrderBy(x => x))}.");
                    }
                }
            }

            var timeline = session.ActiveTimeline;

            // 2. Validate all clips exist and are video/image clips
            var clipsToMutate = new List<Clip>();
            foreach (var clipId in clipIds)
            {
                Clip? foundClip = null;
                Track? ownerTrack = null;
                foreach (var track in timeline.Tracks)
                {
                    foundClip = track.Clips.FirstOrDefault(c => c.Id == clipId);
                    if (foundClip != null)
                    {
                        ownerTrack = track;
                        break;
                    }
                }
                if (foundClip == null)
                {
                    return EditResult.Fail($"Clip not found: {clipId}");
                }
                if (ownerTrack!.Locked)
                {
                    return EditResult.Fail(TrackLockedError);
                }
                if (foundClip.MediaType != ClipType.video && foundClip.MediaType != ClipType.image)
                {
                    return EditResult.Fail($"Clip {clipId} is a {foundClip.MediaType} clip; apply_effect needs a video or image clip.");
                }
                clipsToMutate.Add(foundClip);
            }

            // 3. Apply the mutations
            foreach (var clip in clipsToMutate)
            {
                var stack = clip.Effects ?? new List<Effect>();

                foreach (var type in removes)
                {
                    stack.RemoveAll(x => x.Type == type);
                }

                foreach (var e in adds)
                {
                    var d = EffectRegistry.GetDescriptor(e.Type)!;
                    var existing = stack.Where(x => x.Type == e.Type).ToList();
                    if (existing.Count == 0)
                    {
                        var created = d.MakeEffect();
                        ApplyEffectFields(created, e, d);
                        int idx;
                        if (e.Position is int requested)
                        {
                            if (requested < 0 || requested > stack.Count)
                                return EditResult.Fail($"'{e.Type}' position {requested} is out of range (0..{stack.Count}).");
                            idx = requested;
                        }
                        else
                        {
                            idx = EffectRegistry.GetInsertIndex(stack, e.Type);
                        }

                        stack.Insert(idx, created);
                        continue;
                    }

                    var effect = existing[0];
                    ApplyEffectFields(effect, e, d);
                    for (var i = stack.Count - 1; i >= 0; i--)
                    {
                        if (stack[i].Type == e.Type && !ReferenceEquals(stack[i], effect))
                            stack.RemoveAt(i);
                    }

                    if (e.Position is int destination)
                    {
                        if (destination < 0 || destination >= stack.Count)
                            return EditResult.Fail($"'{e.Type}' position {destination} is out of range (0..{stack.Count - 1}).");
                        var source = stack.IndexOf(effect);
                        if (source != destination)
                        {
                            stack.RemoveAt(source);
                            stack.Insert(destination, effect);
                        }
                    }
                }

                clip.Effects = stack.Count == 0 ? null : stack;
            }

            session.MarkDirty();
            return EditResult.Success();
        });
    }

    public static EditResult ApplyColor(
        ProjectSession session,
        IReadOnlyList<string> clipIds,
        ColorArgs args)
    {
        return ExecuteWithUndo(session, () =>
        {
            if (clipIds == null || clipIds.Count == 0)
                return EditResult.Fail("clipIds is empty.");

            var hasAnyKnob = args.Exposure.HasValue || args.Contrast.HasValue || args.Saturation.HasValue ||
                             args.Vibrance.HasValue || args.Temperature.HasValue || args.Tint.HasValue ||
                             args.Highlights.HasValue || args.Shadows.HasValue || args.Blacks.HasValue ||
                             args.Whites.HasValue || args.ShadowsHue.HasValue || args.ShadowsAmount.HasValue ||
                             args.ShadowsLum.HasValue || args.MidsHue.HasValue || args.MidsAmount.HasValue ||
                             args.MidsGamma.HasValue || args.HighsHue.HasValue || args.HighsAmount.HasValue ||
                             args.HighsGain.HasValue || args.MasterCurve != null || args.RedCurve != null ||
                             args.GreenCurve != null || args.BlueCurve != null ||
                             (args.HueCurves != null && args.HueCurves.Count > 0) || args.Lut != null;

            if (args.Color != null)
            {
                if (hasAnyKnob || args.Reset == true)
                {
                    return EditResult.Fail("'color' pastes a complete grade — don't combine it with individual knobs or reset.");
                }
            }
            else
            {
                if (!hasAnyKnob && args.Reset != true)
                {
                    return EditResult.Fail("No grade parameters provided.");
                }
            }

            var timeline = session.ActiveTimeline;
            var clipsToMutate = new List<Clip>();
            foreach (var clipId in clipIds)
            {
                Clip? foundClip = null;
                Track? ownerTrack = null;
                foreach (var track in timeline.Tracks)
                {
                    foundClip = track.Clips.FirstOrDefault(c => c.Id == clipId);
                    if (foundClip != null)
                    {
                        ownerTrack = track;
                        break;
                    }
                }
                if (foundClip == null)
                {
                    return EditResult.Fail($"Clip not found: {clipId}");
                }
                if (ownerTrack!.Locked)
                {
                    return EditResult.Fail(TrackLockedError);
                }
                if (foundClip.MediaType != ClipType.video && foundClip.MediaType != ClipType.image)
                {
                    return EditResult.Fail($"Clip {clipId} is a {foundClip.MediaType} clip; apply_color needs a video or image clip.");
                }
                clipsToMutate.Add(foundClip);
            }

            List<Effect>? pastedStack = null;
            if (args.Color != null)
            {
                var pasted = new GradeState(args.Color);
                if (pasted.LutIntensity.HasValue) pasted.LutIntensity = Math.Min(1, Math.Max(0, pasted.LutIntensity.Value));
                pastedStack = pasted.BuildStack();
            }

            var reset = args.Reset ?? false;
            foreach (var clip in clipsToMutate)
            {
                var nonColor = (clip.Effects ?? new List<Effect>()).Where(e => !e.Type.StartsWith("color.")).ToList();
                if (pastedStack != null)
                {
                    var newColorEffects = pastedStack.Select(e => new Effect
                    {
                        Type = e.Type,
                        Enabled = e.Enabled,
                        Params = e.Params.ToDictionary(kv => kv.Key, kv => new EffectParam { Value = kv.Value.Value, String = kv.Value.String })
                    }).ToList();
                    nonColor.AddRange(newColorEffects);
                    clip.Effects = nonColor.Count == 0 ? null : nonColor;
                    continue;
                }

                var disabledColor = reset ? new HashSet<string>() :
                    (clip.Effects ?? new List<Effect>()).Where(e => e.Type.StartsWith("color.") && !e.Enabled).Select(e => e.Type).ToHashSet();

                var state = new GradeState(reset ? null : clip.Effects);
                state.Apply(args);
                var colorStack = state.BuildStack();

                foreach (var e in colorStack)
                {
                    if (disabledColor.Contains(e.Type))
                    {
                        e.Enabled = false;
                    }
                }

                nonColor.AddRange(colorStack);
                clip.Effects = nonColor.Count == 0 ? null : nonColor;
            }

            session.MarkDirty();
            return EditResult.Success();
        });
    }
}
