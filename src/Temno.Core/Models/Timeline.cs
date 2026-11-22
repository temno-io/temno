using System.Text.Json.Serialization;

namespace Temno.Core.Models;

/// <summary>Mac: Models/Timeline.swift</summary>
public sealed class Timeline
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = NewId();

    [JsonPropertyName("name")]
    public string Name { get; set; } = "Timeline 1";

    [JsonPropertyName("fps")]
    public int Fps { get; set; } = 30;

    [JsonPropertyName("width")]
    public int Width { get; set; } = 1920;

    [JsonPropertyName("height")]
    public int Height { get; set; } = 1080;

    [JsonPropertyName("settingsConfigured")]
    public bool SettingsConfigured { get; set; }

    [JsonPropertyName("folderId")]
    public string? FolderId { get; set; }

    [JsonPropertyName("tracks")]
    public List<Track> Tracks { get; set; } = [];

    [JsonPropertyName("markers")]
    public List<Marker> Markers { get; set; } = [];

    [JsonPropertyName("transitions")]
    public List<Transition> Transitions { get; set; } = [];

    [JsonIgnore]
    public int TotalFrames
    {
        get
        {
            var max = 0;
            foreach (var t in Tracks)
                max = Math.Max(max, t.EndFrame);
            return max;
        }
    }

    public (int TrackIndex, Clip ClipA, Clip ClipB)? AdjacentVideoClipPair(string clipIdA, string clipIdB)
    {
        for (int trackIndex = 0; trackIndex < Tracks.Count; trackIndex++)
        {
            var track = Tracks[trackIndex];
            if (track.Type != ClipType.video) continue;

            var clipA = track.Clips.FirstOrDefault(c => c.Id == clipIdA);
            var clipB = track.Clips.FirstOrDefault(c => c.Id == clipIdB);
            if (clipA == null || clipB == null) continue;

            if (clipB.StartFrame != clipA.EndFrame) continue;
            if ((clipA.MediaType == ClipType.video || clipA.MediaType == ClipType.image) &&
                (clipB.MediaType == ClipType.video || clipB.MediaType == ClipType.image))
            {
                return (trackIndex, clipA, clipB);
            }
        }
        return null;
    }

    [JsonIgnore]
    public List<ResolvedTransition> ResolvedTransitions
    {
        get
        {
            var list = new List<ResolvedTransition>();
            foreach (var t in Transitions)
            {
                var pair = AdjacentVideoClipPair(t.ClipIdA, t.ClipIdB);
                if (pair == null) continue;
                var frames = Math.Max(1, Math.Min(t.DurationFrames, Math.Min(pair.Value.ClipA.DurationFrames, pair.Value.ClipB.DurationFrames)));
                list.Add(new ResolvedTransition(t, pair.Value.TrackIndex, pair.Value.ClipA, pair.Value.ClipB, frames));
            }
            return list;
        }
    }

    public static string NewId() => Guid.NewGuid().ToString().ToUpperInvariant();
}

public sealed class Track
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = Timeline.NewId();

    [JsonPropertyName("type")]
    public ClipType Type { get; set; } = ClipType.video;

    [JsonPropertyName("muted")]
    public bool Muted { get; set; }

    [JsonPropertyName("hidden")]
    public bool Hidden { get; set; }

    [JsonPropertyName("syncLocked")]
    public bool SyncLocked { get; set; } = true;

    [JsonPropertyName("locked")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Locked { get; set; }

    [JsonPropertyName("clips")]
    public List<Clip> Clips { get; set; } = [];

    /// <summary>
    /// On-disk and in-memory track row height in pixels. Clamped to the Mac-compatible
    /// 32–200 range (see <see cref="Editing.TimelineGeometry.MinTrackHeight"/> /
    /// <see cref="Editing.TimelineGeometry.MaxTrackHeight"/>) so decode matches Mac
    /// <c>Track.init(from:)</c> behaviour.
    /// </summary>
    [JsonPropertyName("displayHeight")]
    public double DisplayHeight
    {
        get => _displayHeight;
        set => _displayHeight = Editing.TimelineGeometry.ClampTrackHeight(value);
    }

    private double _displayHeight = Editing.TimelineGeometry.DefaultTrackHeight;

    [JsonIgnore]
    public int EndFrame
    {
        get
        {
            var max = 0;
            foreach (var c in Clips)
                max = Math.Max(max, c.EndFrame);
            return max;
        }
    }
}

public sealed class Clip
{
    public const string DenoiseEffectType = "audio.denoise";
    public const double DefaultDenoiseAmount = 0.6;

    [JsonPropertyName("id")]
    public string Id { get; set; } = Timeline.NewId();

    [JsonPropertyName("mediaRef")]
    public string MediaRef { get; set; } = "";

    [JsonPropertyName("mediaType")]
    public ClipType MediaType { get; set; } = ClipType.video;

    [JsonPropertyName("sourceClipType")]
    public ClipType SourceClipType { get; set; } = ClipType.video;

    [JsonPropertyName("startFrame")]
    public int StartFrame { get; set; }

    [JsonPropertyName("durationFrames")]
    public int DurationFrames { get; set; }

    [JsonPropertyName("trimStartFrame")]
    public int TrimStartFrame { get; set; }

    [JsonPropertyName("trimEndFrame")]
    public int TrimEndFrame { get; set; }

    [JsonPropertyName("speed")]
    public double Speed { get; set; } = 1.0;

    [JsonPropertyName("volume")]
    public double Volume { get; set; } = 1.0;

    [JsonPropertyName("fadeInFrames")]
    public int FadeInFrames { get; set; }

    [JsonPropertyName("fadeOutFrames")]
    public int FadeOutFrames { get; set; }

    [JsonPropertyName("fadeInInterpolation")]
    public Interpolation FadeInInterpolation { get; set; } = Interpolation.linear;

    [JsonPropertyName("fadeOutInterpolation")]
    public Interpolation FadeOutInterpolation { get; set; } = Interpolation.linear;

    [JsonPropertyName("opacity")]
    public double Opacity { get; set; } = 1.0;

    [JsonPropertyName("transform")]
    public Transform Transform { get; set; } = new();

    [JsonPropertyName("crop")]
    public Crop Crop { get; set; } = new();

    [JsonPropertyName("linkGroupId")]
    public string? LinkGroupId { get; set; }

    [JsonPropertyName("captionGroupId")]
    public string? CaptionGroupId { get; set; }

    /// <summary>Mac: Clip.multicamGroupId — stamps program/audio fragments of a multicam group.</summary>
    [JsonPropertyName("multicamGroupId")]
    public string? MulticamGroupId { get; set; }

    [JsonPropertyName("textContent")]
    public string? TextContent { get; set; }

    // Complex text models: preserve as raw JSON until full port (WP-42).
    [JsonPropertyName("textStyle")]
    public System.Text.Json.JsonElement? TextStyle { get; set; }

    [JsonPropertyName("textAnimation")]
    public System.Text.Json.JsonElement? TextAnimation { get; set; }

    [JsonPropertyName("wordTimings")]
    public List<WordTiming>? WordTimings { get; set; }

    [JsonPropertyName("opacityTrack")]
    public KeyframeTrack<double>? OpacityTrack { get; set; }

    [JsonPropertyName("positionTrack")]
    public KeyframeTrack<AnimPair>? PositionTrack { get; set; }

    [JsonPropertyName("scaleTrack")]
    public KeyframeTrack<AnimPair>? ScaleTrack { get; set; }

    [JsonPropertyName("rotationTrack")]
    public KeyframeTrack<double>? RotationTrack { get; set; }

    [JsonPropertyName("cropTrack")]
    public KeyframeTrack<Crop>? CropTrack { get; set; }

    [JsonPropertyName("volumeTrack")]
    public KeyframeTrack<double>? VolumeTrack { get; set; }

    [JsonPropertyName("effects")]
    public List<Effect>? Effects { get; set; }

    [JsonPropertyName("blendMode")]
    public string? BlendMode { get; set; }

    [JsonIgnore]
    public int EndFrame => StartFrame + DurationFrames;

    [JsonIgnore]
    public bool SupportsRetiming => SourceClipType != ClipType.sequence;

    /// <summary>Mac: round(durationFrames * speed)</summary>
    [JsonIgnore]
    public int SourceFramesConsumed => (int)Math.Round(DurationFrames * Speed);

    [JsonIgnore]
    public int SourceDurationFrames => SourceFramesConsumed + TrimStartFrame + TrimEndFrame;

    public bool Contains(int timelineFrame)
        => timelineFrame >= StartFrame && timelineFrame < EndFrame;

    /// <summary>
    /// Maps a source-media time (seconds) into a timeline frame on this clip, or null when
    /// outside the visible source window. Mac: <c>Clip.timelineFrame(sourceSeconds:fps:)</c>.
    /// </summary>
    public int? TimelineFrameFromSourceSeconds(double sourceSeconds, int fps)
    {
        if (fps <= 0 || DurationFrames <= 0) return null;
        var sourceFrame = sourceSeconds * fps;
        var offsetFromTrim = sourceFrame - TrimStartFrame;
        if (offsetFromTrim < 0) return null;
        var speed = Math.Max(Speed, 0.0001);
        var frame = (int)Math.Round(StartFrame + offsetFromTrim / speed, MidpointRounding.AwayFromZero);
        if (frame < StartFrame || frame >= EndFrame) return null;
        return frame;
    }

    private int KeyframeOffset(int frame) => frame - StartFrame;

    public double RawOpacityAt(int frame)
        => KeyframeMath.Sample(OpacityTrack, KeyframeOffset(frame), Opacity);

    public double OpacityAt(int frame)
    {
        var bas = RawOpacityAt(frame);
        if (MediaType == ClipType.audio || (FadeInFrames <= 0 && FadeOutFrames <= 0))
            return bas;
        return bas * FadeMultiplier(frame);
    }

    public double RotationAt(int frame)
        => KeyframeMath.Sample(RotationTrack, KeyframeOffset(frame), Transform.Rotation);

    public (double X, double Y) SizeAt(int frame)
    {
        var fallback = new AnimPair(Transform.Width, Transform.Height);
        var s = KeyframeMath.Sample(ScaleTrack, KeyframeOffset(frame), fallback);
        return (s.A, s.B);
    }

    public (double X, double Y) TopLeftAt(int frame)
    {
        if (PositionTrack is { IsActive: true })
        {
            var p = KeyframeMath.Sample(PositionTrack, KeyframeOffset(frame), new AnimPair(0, 0));
            return (p.A, p.B);
        }
        var c = Transform.Center;
        var sz = SizeAt(frame);
        return (c.X - sz.X / 2, c.Y - sz.Y / 2);
    }

    public Transform TransformAt(int frame)
    {
        var tl = TopLeftAt(frame);
        var sz = SizeAt(frame);
        var t = Transform.FromTopLeft(tl.X, tl.Y, sz.X, sz.Y);
        t.Rotation = RotationAt(frame);
        t.FlipHorizontal = Transform.FlipHorizontal;
        t.FlipVertical = Transform.FlipVertical;
        return t;
    }

    public Crop CropAt(int frame)
        => KeyframeMath.Sample(CropTrack, KeyframeOffset(frame), Crop);

    public double FadeMultiplier(int frame)
    {
        var rel = frame - StartFrame;
        if (rel < 0 || rel > DurationFrames) return 0;

        double inMul = 1.0;
        if (FadeInFrames > 0)
        {
            var t = Math.Min(1.0, (double)rel / FadeInFrames);
            inMul = FadeInInterpolation == Interpolation.smooth ? KeyframeMath.Smoothstep(t) : t;
        }

        var outRem = DurationFrames - rel;
        double outMul = 1.0;
        if (FadeOutFrames > 0)
        {
            var t = Math.Min(1.0, (double)outRem / FadeOutFrames);
            outMul = FadeOutInterpolation == Interpolation.smooth ? KeyframeMath.Smoothstep(t) : t;
        }

        return Math.Min(inMul, outMul);
    }

    public double VolumeAt(int frame)
    {
        double kfGain = 1.0;
        if (VolumeTrack is { IsActive: true })
        {
            var dB = KeyframeMath.Sample(VolumeTrack, KeyframeOffset(frame), 0);
            kfGain = VolumeScale.LinearFromDb(dB);
        }
        return Volume * kfGain * FadeMultiplier(frame);
    }

    public void ClampFadesToDuration()
    {
        FadeInFrames = Math.Max(0, Math.Min(FadeInFrames, DurationFrames));
        FadeOutFrames = Math.Max(0, Math.Min(FadeOutFrames, DurationFrames - FadeInFrames));
    }
}

public sealed class WordTiming
{
    [JsonPropertyName("text")]
    public string Text { get; set; } = "";

    [JsonPropertyName("startFrame")]
    public int StartFrame { get; set; }

    [JsonPropertyName("endFrame")]
    public int EndFrame { get; set; }

    public WordTiming Clone() => new()
    {
        Text = Text,
        StartFrame = StartFrame,
        EndFrame = EndFrame,
    };
}

public sealed class Marker
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = Timeline.NewId();

    [JsonPropertyName("frame")]
    public int Frame { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("color")]
    public string? Color { get; set; }

    [JsonPropertyName("note")]
    public string? Note { get; set; }
}

public sealed class Transition
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = Timeline.NewId();

    [JsonPropertyName("type")]
    [JsonConverter(typeof(TransitionTypeJsonConverter))]
    public TransitionType Type { get; set; } = TransitionType.CrossDissolve;

    [JsonPropertyName("durationFrames")]
    public int DurationFrames { get; set; }

    [JsonPropertyName("clipIdA")]
    public string ClipIdA { get; set; } = "";

    [JsonPropertyName("clipIdB")]
    public string ClipIdB { get; set; } = "";
}

public sealed class ResolvedTransition
{
    public Transition Transition { get; }
    public int TrackIndex { get; }
    public Clip ClipA { get; }
    public Clip ClipB { get; }
    public int Frames { get; }

    /// <summary>The edit point: B's first frame, which is A's end frame.</summary>
    public int CutFrame => ClipB.StartFrame;

    /// <summary>
    /// WP-252. The window is centred on the cut: <c>CutFrame − Frames / 2</c> to
    /// <see cref="EndFrame"/>, end exclusive. The renderer, smart render and the timeline
    /// blocks all read these two, so the drawn block is the rendered blend.
    /// </summary>
    public int StartFrame => CutFrame - (Frames / 2);

    public int EndFrame => StartFrame + Frames;

    public bool IsActiveAt(int frame) => frame >= StartFrame && frame < EndFrame;

    public ResolvedTransition(Transition transition, int trackIndex, Clip clipA, Clip clipB, int frames)
    {
        Transition = transition;
        TrackIndex = trackIndex;
        ClipA = clipA;
        ClipB = clipB;
        Frames = frames;
    }
}

public sealed class Effect
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = Timeline.NewId();

    [JsonPropertyName("type")]
    public string Type { get; set; } = "";

    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;

    [JsonPropertyName("params")]
    public Dictionary<string, EffectParam> Params { get; set; } = new();

    public Effect Clone() => new()
    {
        Id = Id,
        Type = Type,
        Enabled = Enabled,
        Params = Params.ToDictionary(
            kv => kv.Key,
            kv => kv.Value.Clone(),
            StringComparer.Ordinal),
    };
}

public sealed class EffectParam
{
    [JsonPropertyName("value")]
    public double? Value { get; set; }

    [JsonPropertyName("string")]
    public string? String { get; set; }

    [JsonPropertyName("track")]
    public KeyframeTrack<double>? Track { get; set; }

    public double Resolved(int offset, double defaultValue)
    {
        if (Track is { IsActive: true })
            return KeyframeMath.Sample(Track, offset, Value ?? defaultValue);
        return Value ?? defaultValue;
    }

    public EffectParam Clone() => new()
    {
        Value = Value,
        String = String,
        Track = Track?.Clone(),
    };
}
