using System.Text.Json;
using Temno.Core.Models;

namespace Temno.Core.Session;

/// <summary>One text clip to create. Mirrors Mac PartialTextSpec (ToolExecutor+Texts.swift).</summary>
public sealed record TextClipSpec(
    int? TrackIndex,
    int StartFrame,
    int EndFrame,
    string Content,
    TextStyle Style,
    Transform? Transform,
    TextAnimation? Animation);

/// <summary>Optional per-field style edits; null means "leave unchanged".</summary>
public sealed class TextStylePatch
{
    public string? FontName { get; set; }
    public double? FontSize { get; set; }
    public bool? IsBold { get; set; }
    public bool? IsItalic { get; set; }
    public TextStyle.RGBA? Color { get; set; }
    public TextAlignment? Alignment { get; set; }
    public TextStyle.RGBA? BorderColor { get; set; }
    public TextStyle.RGBA? BackgroundColor { get; set; }
    public bool? BackgroundEnabled { get; set; }
    public bool? BorderEnabled { get; set; }
    public bool? ShadowEnabled { get; set; }
    public double? ShadowBlur { get; set; }
    public double? ShadowOffsetX { get; set; }
    public double? ShadowOffsetY { get; set; }

    public bool HasAnyField =>
        FontName != null || FontSize != null || IsBold != null || IsItalic != null
        || Color != null || Alignment != null || BorderColor != null || BackgroundColor != null
        || BackgroundEnabled != null || BorderEnabled != null || ShadowEnabled != null
        || ShadowBlur != null || ShadowOffsetX != null || ShadowOffsetY != null;

    /// <summary>Changes that alter measured text size (Mac ParsedTextStylePatch.affectsLayout).</summary>
    public bool AffectsLayout => FontName != null || FontSize != null || IsBold != null || IsItalic != null;

    public void ApplyTo(TextStyle style)
    {
        if (FontName != null) style.FontName = FontName;
        if (FontSize != null) style.FontSize = FontSize.Value;
        if (IsBold != null) style.IsBold = IsBold.Value;
        if (IsItalic != null) style.IsItalic = IsItalic.Value;
        if (Color != null) style.Color = Color;
        if (Alignment != null) style.Alignment = Alignment.Value;
        if (BorderColor != null)
        {
            style.Border.Color = BorderColor;
            style.Border.Enabled = true;
        }
        if (BackgroundColor != null)
        {
            style.Background.Color = BackgroundColor;
            style.Background.Enabled = true;
        }
        if (BackgroundEnabled != null) style.Background.Enabled = BackgroundEnabled.Value;
        if (BorderEnabled != null) style.Border.Enabled = BorderEnabled.Value;
        if (ShadowEnabled != null) style.Shadow.Enabled = ShadowEnabled.Value;
        if (ShadowBlur != null) style.Shadow.Blur = ShadowBlur.Value;
        if (ShadowOffsetX != null) style.Shadow.OffsetX = ShadowOffsetX.Value;
        if (ShadowOffsetY != null) style.Shadow.OffsetY = ShadowOffsetY.Value;
    }
}

/// <summary>Edits for existing text clips. Mirrors Mac update_text argument handling.</summary>
public sealed class TextPatch
{
    public bool HasContent { get; set; }
    public string? Content { get; set; }
    public TextStylePatch Style { get; set; } = new();
    public double? CenterX { get; set; }
    public double? CenterY { get; set; }
    public double? Width { get; set; }
    public double? Height { get; set; }
    /// <summary>True when the caller supplied 'animation' (even "off", which clears it).</summary>
    public bool SetAnimation { get; set; }
    public TextAnimation? Animation { get; set; }
    /// <summary>highlightColor supplied without an animation preset.</summary>
    public TextStyle.RGBA? HighlightOnly { get; set; }

    public bool HasTransform => CenterX != null || CenterY != null || Width != null || Height != null;

    public bool HasAny =>
        HasContent || Style.HasAnyField || HasTransform || SetAnimation || HighlightOnly != null;
}

/// <summary>
/// Creation and editing of text clips (titles, lower-thirds, captions authored by the agent).
/// Mac: ToolExecutor+Texts.swift + EditorViewModel.placeTextClips.
/// </summary>
public static class TextClipCommands
{
    public static EditResult AddTexts(
        ProjectSession session,
        IReadOnlyList<TextClipSpec> specs,
        out List<string> createdClipIds)
    {
        ArgumentNullException.ThrowIfNull(session);
        createdClipIds = [];

        if (specs is not { Count: > 0 })
            return EditResult.Fail("Missing or empty 'entries' array");

        var timeline = session.ActiveTimeline;
        for (var i = 0; i < specs.Count; i++)
        {
            var s = specs[i];
            if (s.StartFrame < 0)
                return EditResult.Fail($"entries[{i}]: startFrame must be >= 0 (got {s.StartFrame})");
            if (s.EndFrame <= s.StartFrame)
                return EditResult.Fail($"entries[{i}]: endFrame ({s.EndFrame}) must be greater than startFrame ({s.StartFrame})");
            if (s.TrackIndex is { } ti)
            {
                if (ti < 0 || ti >= timeline.Tracks.Count)
                    return EditResult.Fail($"entries[{i}]: track index {ti} out of range (0..{timeline.Tracks.Count - 1})");
                if (timeline.Tracks[ti].Type != ClipType.video)
                    return EditResult.Fail($"entries[{i}]: track {ti} is an audio track; text requires a video track");
                if (timeline.Tracks[ti].Locked)
                    return EditResult.Fail(EditCommands.TrackLockedError);
            }
        }

        // All-or-none: a new track at index 0 would shift any explicit indices (Mac rule).
        var omitted = specs.Count(s => s.TrackIndex is null);
        if (omitted != 0 && omitted != specs.Count)
            return EditResult.Fail(
                $"Mixed trackIndex: {omitted} of {specs.Count} entries omitted trackIndex. Either set it on every entry or omit it on every entry (to auto-create a shared new track).");

        session.RecordBeforeEdit();
        try
        {
            var autoCreate = omitted == specs.Count;
            if (autoCreate)
                timeline.Tracks.Insert(0, new Track { Type = ClipType.video });

            // Overlap check per target track: against existing clips and among the batch.
            var byTrack = specs
                .Select((s, i) => (Spec: s, Index: i, Track: autoCreate ? 0 : s.TrackIndex!.Value))
                .GroupBy(x => x.Track);
            foreach (var group in byTrack)
            {
                var track = timeline.Tracks[group.Key];
                var spans = group
                    .Select(x => (x.Index, Start: x.Spec.StartFrame, End: x.Spec.EndFrame))
                    .OrderBy(x => x.Start)
                    .ToList();
                for (var i = 1; i < spans.Count; i++)
                {
                    if (spans[i].Start < spans[i - 1].End)
                    {
                        session.RollbackLastRecord();
                        return EditResult.Fail($"entries[{spans[i].Index}]: overlaps entries[{spans[i - 1].Index}] on track {group.Key}");
                    }
                }
                foreach (var span in spans)
                {
                    if (track.Clips.Any(c => c.StartFrame < span.End && span.Start < c.EndFrame))
                    {
                        session.RollbackLastRecord();
                        return EditResult.Fail($"entries[{span.Index}]: overlaps an existing clip on track {group.Key}");
                    }
                }
            }

            var canvasW = Math.Max(1, timeline.Width);
            var canvasH = Math.Max(1, timeline.Height);
            foreach (var (spec, _, trackIndex) in byTrack.SelectMany(g => g))
            {
                var clip = new Clip
                {
                    MediaRef = "",
                    MediaType = ClipType.text,
                    SourceClipType = ClipType.text,
                    StartFrame = spec.StartFrame,
                    DurationFrames = spec.EndFrame - spec.StartFrame,
                    TextContent = spec.Content,
                    TextStyle = JsonSerializer.SerializeToElement(spec.Style, JsonOptions.ForProject),
                    Transform = spec.Transform
                        ?? FitTransform(spec.Content, spec.Style, canvasW, canvasH, centerX: 0.5, centerY: 0.5),
                };
                if (spec.Animation is not null)
                    clip.TextAnimation = JsonSerializer.SerializeToElement(spec.Animation, JsonOptions.ForProject);

                var track = timeline.Tracks[trackIndex];
                track.Clips.Add(clip);
                track.Clips.Sort((a, b) => a.StartFrame.CompareTo(b.StartFrame));
                createdClipIds.Add(clip.Id);
            }

            session.MarkDirty();
            return EditResult.Success();
        }
        catch
        {
            session.RollbackLastRecord();
            throw;
        }
    }

    public static EditResult UpdateText(
        ProjectSession session,
        IReadOnlyList<string> clipIds,
        string? captionGroupId,
        TextPatch patch,
        out List<string> touchedClipIds,
        out List<string> notes)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(patch);
        touchedClipIds = [];
        notes = [];

        var timeline = session.ActiveTimeline;
        var ids = new List<string>(clipIds ?? []);
        if (!string.IsNullOrWhiteSpace(captionGroupId))
        {
            var groupIds = timeline.Tracks
                .SelectMany(t => t.Clips)
                .Where(c => c.CaptionGroupId == captionGroupId)
                .Select(c => c.Id)
                .ToList();
            if (groupIds.Count == 0)
                return EditResult.Fail($"No caption clips found for captionGroupId: {captionGroupId}");
            var seen = new HashSet<string>(ids);
            ids.AddRange(groupIds.Where(id => seen.Add(id)));
        }
        if (ids.Count == 0)
            return EditResult.Fail("Provide a non-empty 'clipIds' array or a 'captionGroupId'");
        if (!patch.HasAny)
            return EditResult.Fail("update_text needs at least one text property to apply");

        var clips = new List<Clip>(ids.Count);
        foreach (var id in ids)
        {
            Track? ownerTrack = null;
            Clip? clip = null;
            foreach (var t in timeline.Tracks)
            {
                clip = t.Clips.FirstOrDefault(c => c.Id == id);
                if (clip is not null)
                {
                    ownerTrack = t;
                    break;
                }
            }
            if (clip is null)
                return EditResult.Fail($"Clip not found: {id}");
            if (ownerTrack!.Locked)
                return EditResult.Fail(EditCommands.TrackLockedError);
            if (clip.MediaType != ClipType.text)
                return EditResult.Fail($"update_text only applies to text clips: {id} is {clip.MediaType}");
            clips.Add(clip);
        }

        if (patch.HasContent)
        {
            var cleared = clips.Count(c => c.WordTimings != null && c.TextContent != patch.Content);
            if (cleared > 0)
                notes.Add($"Content change cleared word timings on {cleared} clip{(cleared == 1 ? "" : "s")} — karaoke highlighting falls back to plain text there.");
        }

        var fitToContent = !patch.HasTransform && (patch.HasContent || patch.Style.AffectsLayout);
        var canvasW = Math.Max(1, timeline.Width);
        var canvasH = Math.Max(1, timeline.Height);

        session.RecordBeforeEdit();
        try
        {
            foreach (var clip in clips)
            {
                if (patch.HasContent)
                {
                    if (clip.TextContent != patch.Content)
                        clip.WordTimings = null;
                    clip.TextContent = patch.Content;
                }

                TextStyle? style = null;
                if (patch.Style.HasAnyField || fitToContent)
                {
                    style = DeserializeStyle(clip) ?? new TextStyle();
                    if (patch.Style.HasAnyField)
                    {
                        patch.Style.ApplyTo(style);
                        clip.TextStyle = JsonSerializer.SerializeToElement(style, JsonOptions.ForProject);
                    }
                }

                if (patch.HasTransform)
                {
                    var current = clip.Transform;
                    clip.Transform = new Transform
                    {
                        CenterX = patch.CenterX ?? current.CenterX,
                        CenterY = patch.CenterY ?? current.CenterY,
                        Width = patch.Width ?? current.Width,
                        Height = patch.Height ?? current.Height,
                        Rotation = current.Rotation,
                        FlipHorizontal = current.FlipHorizontal,
                        FlipVertical = current.FlipVertical,
                    };
                }

                if (patch.SetAnimation)
                {
                    if (patch.Animation is { } animation)
                    {
                        var current = DeserializeAnimation(clip) ?? new TextAnimation();
                        current.Preset = animation.Preset;
                        if (animation.PerWordFrames > 0)
                            current.PerWordFrames = animation.PerWordFrames;
                        if (animation.Highlight is not null)
                            current.Highlight = animation.Highlight;
                        clip.TextAnimation = JsonSerializer.SerializeToElement(current, JsonOptions.ForProject);
                    }
                    else
                    {
                        clip.TextAnimation = null;
                    }
                }

                if (patch.HighlightOnly is not null)
                {
                    var current = DeserializeAnimation(clip) ?? new TextAnimation();
                    current.Highlight = patch.HighlightOnly;
                    clip.TextAnimation = JsonSerializer.SerializeToElement(current, JsonOptions.ForProject);
                }

                if (fitToContent)
                {
                    var oldWidth = clip.Transform.Width;
                    var oldHeight = clip.Transform.Height;
                    // Capture visual centers of any PositionTrack keys before refitting box / ScaleTrack
                    // so editing text copy or font size keeps keyframed positions visually anchored.
                    List<(int Frame, double Cx, double Cy)>? positionCenters = null;
                    if (clip.PositionTrack is { IsActive: true } posTrackBefore)
                    {
                        positionCenters = new List<(int, double, double)>(posTrackBefore.Keyframes.Count);
                        foreach (var kf in posTrackBefore.Keyframes)
                        {
                            var sz = clip.SizeAt(clip.StartFrame + kf.Frame);
                            positionCenters.Add((kf.Frame, kf.Value.A + sz.X / 2.0, kf.Value.B + sz.Y / 2.0));
                        }
                    }

                    var fitted = FitTransform(
                        clip.TextContent ?? "", style ?? new TextStyle(),
                        canvasW, canvasH,
                        clip.Transform.CenterX, clip.Transform.CenterY);
                    fitted.Rotation = clip.Transform.Rotation;
                    fitted.FlipHorizontal = clip.Transform.FlipHorizontal;
                    fitted.FlipVertical = clip.Transform.FlipVertical;
                    clip.Transform = fitted;

                    // Preserve relative ScaleTrack ratios when the base text box refits to new copy/font.
                    if (clip.ScaleTrack is { IsActive: true } && oldWidth > 1e-6 && oldHeight > 1e-6)
                    {
                        var rx = fitted.Width / oldWidth;
                        var ry = fitted.Height / oldHeight;
                        foreach (var kf in clip.ScaleTrack.Keyframes)
                            kf.Value = new AnimPair(kf.Value.A * rx, kf.Value.B * ry);
                    }

                    if (positionCenters is not null && clip.PositionTrack is { IsActive: true } posTrackAfter)
                    {
                        for (var i = 0; i < posTrackAfter.Keyframes.Count && i < positionCenters.Count; i++)
                        {
                            var kf = posTrackAfter.Keyframes[i];
                            var (_, cx, cy) = positionCenters[i];
                            var newSz = clip.SizeAt(clip.StartFrame + kf.Frame);
                            kf.Value = new AnimPair(cx - newSz.X / 2.0, cy - newSz.Y / 2.0);
                        }
                    }
                }

                touchedClipIds.Add(clip.Id);
            }

            session.MarkDirty();
            return EditResult.Success();
        }
        catch
        {
            session.RollbackLastRecord();
            throw;
        }
    }

    /// <summary>Parses #RGB, #RRGGBB, or #RRGGBBAA (leading '#' optional) into RGBA (0-1 channels).</summary>
    public static TextStyle.RGBA? ParseHexColor(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex)) return null;
        var s = hex.Trim().TrimStart('#');
        if (s.Length == 3)
            s = string.Concat(s.Select(c => new string(c, 2)));
        if (s.Length is not (6 or 8) || !s.All(Uri.IsHexDigit))
            throw new FormatException($"'{hex}' is not a valid hex color (#RGB, #RRGGBB, or #RRGGBBAA)");

        double Channel(int offset) => Convert.ToInt32(s.Substring(offset, 2), 16) / 255.0;
        return new TextStyle.RGBA
        {
            R = Channel(0),
            G = Channel(2),
            B = Channel(4),
            A = s.Length == 8 ? Channel(6) : 1.0,
        };
    }

    /// <summary>
    /// Exact inverse of <see cref="ParseHexColor"/>: <c>#RRGGBB</c> when the colour is opaque,
    /// <c>#RRGGBBAA</c> when it is not, so a value round-trips to the same string it came from.
    /// </summary>
    /// <remarks>
    /// Uppercase hex, leading '#'. Channels are clamped and rounded, so a colour that arrived as
    /// <c>#3F7ACC</c> encodes back to <c>#3F7ACC</c> and the stored JSON is byte-identical when the
    /// user has not changed anything (WP-127 <c>ColorWell</c>).
    /// </remarks>
    public static string ToHexColor(TextStyle.RGBA? color)
    {
        if (color is null) return "";
        static int Channel(double v) => Math.Clamp((int)Math.Round(v * 255, MidpointRounding.AwayFromZero), 0, 255);
        var rgb = $"#{Channel(color.R):X2}{Channel(color.G):X2}{Channel(color.B):X2}";
        return Channel(color.A) == 255 ? rgb : rgb + $"{Channel(color.A):X2}";
    }

    /// <summary>
    /// Approximates Mac TextLayout.naturalSize (AppKit text measurement) with average-advance
    /// estimates so agent-authored text boxes get sane geometry. Replace with a real Windows text
    /// rasterizer measurement when one lands (see IMPLEMENTATION_PLAN WP-94).
    /// </summary>
    public static Transform FitTransform(
        string content, TextStyle style, double canvasW, double canvasH, double centerX, double centerY)
    {
        var scaleY = canvasH > 0 ? canvasH / 1080.0 : 1.0;
        var fontSize = Math.Max(1, style.FontSize * (style.FontScale <= 0 ? 1 : style.FontScale) * scaleY);
        var advance = fontSize * (style.IsBold ? 0.56 : 0.52);
        var maxWidthPx = canvasW * 0.9;
        var maxCharsPerLine = Math.Max(1, (int)(maxWidthPx / advance));

        var lines = new List<string>();
        foreach (var raw in (content ?? "").Split('\n'))
        {
            if (raw.Length <= maxCharsPerLine) { lines.Add(raw); continue; }
            // Greedy word wrap at the estimated advance width.
            var line = "";
            foreach (var word in raw.Split(' '))
            {
                var candidate = line.Length == 0 ? word : line + " " + word;
                if (candidate.Length > maxCharsPerLine && line.Length > 0)
                {
                    lines.Add(line);
                    line = word;
                }
                else
                {
                    line = candidate;
                }
            }
            lines.Add(line);
        }
        if (lines.Count == 0) lines.Add("");

        var widthPx = Math.Min(maxWidthPx, Math.Max(advance, lines.Max(l => l.Length) * advance));
        var heightPx = Math.Max(fontSize * 1.25, lines.Count * fontSize * 1.25);

        return new Transform
        {
            CenterX = centerX,
            CenterY = centerY,
            Width = Math.Clamp(widthPx / canvasW, 0.01, 1.0),
            Height = Math.Clamp(heightPx / canvasH, 0.01, 1.0),
        };
    }

    private static TextStyle? DeserializeStyle(Clip clip)
    {
        if (clip.TextStyle is not { } el || el.ValueKind != JsonValueKind.Object) return null;
        try { return el.Deserialize<TextStyle>(JsonOptions.ForProject); }
        catch (JsonException) { return null; }
    }

    private static TextAnimation? DeserializeAnimation(Clip clip)
    {
        if (clip.TextAnimation is not { } el || el.ValueKind != JsonValueKind.Object) return null;
        try { return el.Deserialize<TextAnimation>(JsonOptions.ForProject); }
        catch (JsonException) { return null; }
    }
}
