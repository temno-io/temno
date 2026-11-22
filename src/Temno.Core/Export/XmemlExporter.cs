using System.Globalization;
using System.Net;
using System.Text;
using Temno.Core.IO;
using Temno.Core.Models;

namespace Temno.Core.Export;

/// <summary>
/// Exports a timeline as XMEML 4 (Final Cut Pro 7 XML) for Premiere / Resolve interchange.
/// Mac: <c>Export/XMLExporter.swift</c> (WP-112 traced <c>xml</c> mode).
/// </summary>
/// <remarks>
/// Transports: clip placement/trims, speed (time remap), static volume, static opacity, nested
/// sequences at their own timebase, linked A/V reciprocal links, file pathurl, source timecode.
/// Does not transport: markers, transitions and text clips (counted in the returned
/// <see cref="InterchangeExportReport"/>), and the clip properties in <see cref="NotTransported"/>.
/// </remarks>
public static class XmemlExporter
{
    public const string FormatName = "xmeml4";

    /// <summary>Clip properties XMEML output never carries, whatever the timeline holds.</summary>
    public static readonly IReadOnlyList<string> NotTransported =
    [
        "transform (position, scale, rotation)",
        "crop",
        "flips",
        "fades",
        "keyframe animation",
        "effect stacks",
        "blend modes",
    ];

    public static InterchangeExportReport Export(
        Timeline timeline,
        MediaResolver resolver,
        string outputPath,
        Func<string, Timeline?>? resolveTimeline = null,
        IDictionary<string, SourceTimecode>? startTimecodes = null)
    {
        var (xml, report) = RenderWithReport(timeline, resolver, resolveTimeline, startTimecodes);
        var dir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        File.WriteAllText(outputPath, xml, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return report;
    }

    public static string Render(
        Timeline timeline,
        MediaResolver resolver,
        Func<string, Timeline?>? resolveTimeline = null,
        IDictionary<string, SourceTimecode>? startTimecodes = null)
        => RenderWithReport(timeline, resolver, resolveTimeline, startTimecodes).Xml;

    public static (string Xml, InterchangeExportReport Report) RenderWithReport(
        Timeline timeline,
        MediaResolver resolver,
        Func<string, Timeline?>? resolveTimeline = null,
        IDictionary<string, SourceTimecode>? startTimecodes = null)
    {
        ArgumentNullException.ThrowIfNull(timeline);
        ArgumentNullException.ThrowIfNull(resolver);
        resolveTimeline ??= _ => null;
        startTimecodes ??= new Dictionary<string, SourceTimecode>();
        var builder = new Builder(timeline, resolver, resolveTimeline, startTimecodes);
        var xml = builder.Build();
        return (xml, new InterchangeExportReport
        {
            Format = FormatName,
            Omissions = builder.Omissions.ToOmissions("XMEML"),
            NotTransported = NotTransported,
        });
    }

    /// <summary>Mac: XMLExporter.timecodeTags.</summary>
    public static (int Base, bool Ntsc, int Frame, bool DropFrame, string String) TimecodeTags(
        SourceTimecode? source, int videoTimebase, bool videoNtsc)
    {
        var baseRate = source is { Quanta: > 0 } s ? s.Quanta : videoTimebase;
        var dropFrame = (source?.DropFrame ?? (videoNtsc && videoTimebase % 30 == 0))
            && SupportsDropFrame(baseRate);
        var ntsc = dropFrame || videoNtsc;
        var frame = source?.Frame ?? 0;
        return (baseRate, ntsc, frame, dropFrame, FormatTimecode(frame, baseRate, dropFrame));
    }

    /// <summary>Drop-frame numbering exists only for the 30 family (29.97, 59.94, 119.88).</summary>
    public static bool SupportsDropFrame(int fps) => fps > 0 && fps % 30 == 0;

    /// <summary>
    /// Frame count to SMPTE label. Drop-frame (ST 12-1) skips fps/15 labels at the top of every
    /// minute except each tenth, so a counted minute holds fps*60 - drop frames and ten minutes
    /// fps*600 - 9*drop. A drop request at a rate outside the 30 family formats non-drop.
    /// </summary>
    public static string FormatTimecode(int frame, int fps, bool dropFrame)
    {
        if (fps <= 0) return "00:00:00:00";
        dropFrame &= SupportsDropFrame(fps);
        var f = frame;
        if (dropFrame)
        {
            var drop = fps / 15;
            var framesPerMinute = fps * 60 - drop;
            var framesPerTenMinutes = fps * 600 - drop * 9;
            var tens = f / framesPerTenMinutes;
            var rem = f % framesPerTenMinutes;
            f += drop * 9 * tens + (rem > drop ? drop * ((rem - drop) / framesPerMinute) : 0);
        }
        var sep = dropFrame ? ";" : ":";
        var ff = f % fps;
        var ss = (f / fps) % 60;
        var mm = (f / (fps * 60)) % 60;
        var hh = f / (fps * 3600);
        return string.Create(CultureInfo.InvariantCulture, $"{hh:D2}{sep}{mm:D2}{sep}{ss:D2}{sep}{ff:D2}");
    }

    private sealed class Builder
    {
        private readonly Timeline _timeline;
        private readonly MediaResolver _resolver;
        private readonly Func<string, Timeline?> _resolveTimeline;
        private readonly IDictionary<string, SourceTimecode> _startTimecodes;
        // The sequence being written: a nested sequence keeps its own timebase and frame size,
        // and every clipitem inside it counts in that timebase.
        private int _fps;
        private int _seqWidth;
        private int _seqHeight;
        private readonly HashSet<(string MediaRef, bool IsAudio)> _emittedFiles = [];
        private readonly Dictionary<string, string> _sequenceIds = new(StringComparer.Ordinal);
        private readonly HashSet<string> _emittedSequences = new(StringComparer.Ordinal);
        private readonly Dictionary<string, (int TrackIndex, int ClipIndex, bool IsAudio)> _clipAddresses =
            new(StringComparer.Ordinal);
        private readonly Dictionary<string, List<Clip>> _clipsByLinkGroup =
            new(StringComparer.Ordinal);

        public InterchangeOmissionTally Omissions { get; } = new();

        public Builder(
            Timeline timeline,
            MediaResolver resolver,
            Func<string, Timeline?> resolveTimeline,
            IDictionary<string, SourceTimecode> startTimecodes)
        {
            _timeline = timeline;
            _resolver = resolver;
            _resolveTimeline = resolveTimeline;
            _startTimecodes = startTimecodes;
            _fps = Math.Max(1, timeline.Fps);
            _seqWidth = Math.Max(1, timeline.Width);
            _seqHeight = Math.Max(1, timeline.Height);
        }

        public string Build()
        {
            _sequenceIds[_timeline.Id] = "sequence-1";
            _emittedSequences.Add(_timeline.Id);
            var root = El("xmeml", [("version", "4")], [SequenceNode("sequence-1", _timeline)]);
            return "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<!DOCTYPE xmeml>\n" + RenderXml(root, 0);
        }

        private XmlNode SequenceNode(string id, Timeline timeline)
        {
            var savedAddresses = new Dictionary<string, (int, int, bool)>(_clipAddresses);
            var savedGroups = new Dictionary<string, List<Clip>>(_clipsByLinkGroup);
            var savedFormat = (_fps, _seqWidth, _seqHeight);
            _clipAddresses.Clear();
            _clipsByLinkGroup.Clear();
            _fps = Math.Max(1, timeline.Fps);
            _seqWidth = Math.Max(1, timeline.Width);
            _seqHeight = Math.Max(1, timeline.Height);
            try
            {
                Omissions.TimelineWritten(timeline);

                // Every non-audio track is a picture track (Mac: `type.isVisual`); a clip placed on
                // a text- or sequence-typed track is still a video clip.
                var videoTracks = timeline.Tracks.Where(t => t.Type.IsVisual()).Reverse().ToList();
                var audioTracks = timeline.Tracks.Where(t => t.Type == ClipType.audio).ToList();
                var sortedVideo = videoTracks.Select(t => SortEmittable(timeline, t)).ToList();
                var sortedAudio = audioTracks.Select(t => SortEmittable(timeline, t)).ToList();

                IndexAddresses(sortedVideo, isAudio: false);
                IndexAddresses(sortedAudio, isAudio: true);
                IndexLinkGroups(timeline);

                var videoTrackNodes = videoTracks.Zip(sortedVideo, (t, clips) => TrackNode(t, clips, isAudio: false));
                var audioTrackNodes = audioTracks.Zip(sortedAudio, (t, clips) => TrackNode(t, clips, isAudio: true));

                return El("sequence", [("id", id)],
                [
                    Leaf("name", timeline.Name),
                    Leaf("duration", timeline.TotalFrames),
                    Rate(_fps),
                    TimecodeNode(),
                    El("media",
                    [
                        El("video", [VideoFormatNode(timeline.Width, timeline.Height), .. videoTrackNodes]),
                        El("audio",
                        [
                            Leaf("numOutputChannels", 2),
                            AudioFormatNode(),
                            AudioOutputsNode(),
                            .. audioTrackNodes,
                        ]),
                    ]),
                ]);
            }
            finally
            {
                _clipAddresses.Clear();
                foreach (var kv in savedAddresses) _clipAddresses[kv.Key] = kv.Value;
                _clipsByLinkGroup.Clear();
                foreach (var kv in savedGroups) _clipsByLinkGroup[kv.Key] = kv.Value;
                (_fps, _seqWidth, _seqHeight) = savedFormat;
            }
        }

        private List<Clip> SortEmittable(Timeline timeline, Track track)
        {
            var emittable = new List<Clip>();
            foreach (var c in track.Clips)
            {
                if (SkipReason(c) is { } reason)
                {
                    Omissions.ClipSkipped(timeline, reason);
                    continue;
                }
                emittable.Add(c);
                if (IsNest(c))
                    Omissions.CarrierWritten(timeline, c);
            }
            return emittable.OrderBy(c => c.StartFrame).ToList();
        }

        private static bool IsNest(Clip clip)
            => clip.SourceClipType == ClipType.sequence || clip.MediaType == ClipType.sequence;

        /// <summary>Why the current sequence cannot write this clip, or null when it can.</summary>
        private string? SkipReason(Clip clip)
        {
            if (clip.MediaType == ClipType.text) return "text";
            if (!IsNest(clip)) return null;
            if (_resolveTimeline(clip.MediaRef) is not { } child) return "unresolved nested sequence";
            if (child.TotalFrames <= 0) return "empty nested sequence";
            // Nothing of the child is left to show (FCPXML leaves the same carrier out).
            return clip.TrimStartFrame >= ChildFramesInParent(child) ? "nested sequence trimmed past its end" : null;
        }

        /// <summary>
        /// The child's length in this sequence's frames. A carrier's trim is in parent frames (the
        /// renderer's rule), so this is what it is clamped against — floored, so the carrier never
        /// runs past the child's content.
        /// </summary>
        private int ChildFramesInParent(Timeline child)
            => (int)((long)child.TotalFrames * _fps / Math.Max(1, child.Fps));

        private void IndexAddresses(List<List<Clip>> tracks, bool isAudio)
        {
            for (var ti = 0; ti < tracks.Count; ti++)
            {
                var clips = tracks[ti];
                for (var ci = 0; ci < clips.Count; ci++)
                    _clipAddresses[clips[ci].Id] = (ti + 1, ci + 1, isAudio);
            }
        }

        private void IndexLinkGroups(Timeline timeline)
        {
            foreach (var clip in timeline.Tracks.SelectMany(t => t.Clips))
            {
                if (string.IsNullOrEmpty(clip.LinkGroupId)) continue;
                if (!_clipsByLinkGroup.TryGetValue(clip.LinkGroupId, out var list))
                {
                    list = [];
                    _clipsByLinkGroup[clip.LinkGroupId] = list;
                }
                list.Add(clip);
            }
        }

        private XmlNode TrackNode(Track track, List<Clip> clips, bool isAudio)
        {
            var enabled = isAudio ? !track.Muted : !track.Hidden;
            var children = new List<XmlNode> { Bool("enabled", enabled), Bool("locked", false) };
            foreach (var clip in clips)
                children.Add(ClipItemNode(clip, isAudio));
            return El("track", children);
        }

        private XmlNode ClipItemNode(Clip clip, bool isAudio)
        {
            if (IsNest(clip))
                return NestClipItemNode(clip, isAudio);

            var sourceDuration = SourceDurationFrames(clip.MediaRef) ?? clip.SourceDurationFrames;
            var inPoint = clip.TrimStartFrame;
            var outPoint = clip.TrimStartFrame + clip.SourceFramesConsumed;
            var children = new List<XmlNode>
            {
                Leaf("masterclipid", MasterclipId(clip, isAudio)),
                Leaf("name", DisplayName(clip.MediaRef)),
                Bool("enabled", true),
                Leaf("duration", sourceDuration),
                Rate(_fps),
                Leaf("start", clip.StartFrame),
                Leaf("end", clip.EndFrame),
                Leaf("in", inPoint),
                Leaf("out", outPoint),
                FileNode(clip.MediaRef, isAudio),
            };
            if (TimeRemapFilter(clip.Speed, isAudio) is { } remap)
                children.Add(remap);
            children.AddRange(isAudio ? VolumeFilters(clip) : VideoFilters(clip));
            children.AddRange(LinkNodes(clip));
            return El("clipitem", [("id", $"clipitem-{clip.Id}")], children);
        }

        private XmlNode NestClipItemNode(Clip clip, bool isAudio)
        {
            var child = _resolveTimeline(clip.MediaRef)
                ?? throw new InvalidOperationException($"Nested timeline not found: {clip.MediaRef}");
            if (!_sequenceIds.TryGetValue(clip.MediaRef, out var seqId))
            {
                seqId = $"sequence-{_sequenceIds.Count + 1}";
                _sequenceIds[clip.MediaRef] = seqId;
            }

            XmlNode sequence;
            if (_emittedSequences.Add(clip.MediaRef))
                sequence = SequenceNode(seqId, child);
            else
                sequence = El("sequence", [("id", seqId)]);

            // The carrier counts in this sequence's timebase, like a file clipitem.
            var childFrames = ChildFramesInParent(child);
            var inPoint = clip.TrimStartFrame;
            var outPoint = Math.Min(inPoint + clip.DurationFrames, childFrames);
            var children = new List<XmlNode>
            {
                Leaf("masterclipid", MasterclipId(clip, isAudio)),
                Leaf("name", child.Name),
                Bool("enabled", true),
                Leaf("duration", childFrames),
                Rate(_fps),
                Leaf("start", clip.StartFrame),
                Leaf("end", clip.StartFrame + (outPoint - inPoint)),
                Leaf("in", inPoint),
                Leaf("out", outPoint),
                sequence,
            };
            children.AddRange(isAudio ? VolumeFilters(clip) : VideoFilters(clip));
            children.AddRange(LinkNodes(clip));
            return El("clipitem", [("id", $"clipitem-{clip.Id}")], children);
        }

        private static string MasterclipId(Clip clip, bool isAudio)
        {
            if (!string.IsNullOrEmpty(clip.LinkGroupId))
                return $"masterclip-{clip.LinkGroupId}";
            return $"masterclip-{clip.MediaRef}-{(isAudio ? "audio" : "video")}";
        }

        private XmlNode FileNode(string mediaRef, bool isAudio)
        {
            var fileId = $"file-{mediaRef}-{(isAudio ? "audio" : "video")}";
            var key = (mediaRef, isAudio);
            if (_emittedFiles.Contains(key))
                return El("file", [("id", fileId)]);
            _emittedFiles.Add(key);

            var entry = _resolver.Entry(mediaRef);
            // WP-399: written as text, never read, so a file the session may not use keeps its path.
            var path = _resolver.ReferencePath(mediaRef);
            var fileName = path is not null ? Path.GetFileName(path) : entry?.Name ?? mediaRef;
            var pathUrl = path is not null
                ? "file://localhost//" + path.Replace('\\', '/').TrimStart('/')
                : $"media/{mediaRef}";

            // A file element is shared by every clipitem that names it, so its duration counts in
            // the file's own rate, never the sequence's.
            var (timebase, ntsc) = RateTags(entry?.SourceFps ?? _fps);
            var isImage = entry?.Type == ClipType.image;
            var durationFrames = isImage
                ? 1
                : entry is null
                    ? 0
                    : Math.Max(0, (int)Math.Round(entry.Duration * (ntsc ? timebase * 1000.0 / 1001.0 : timebase)));

            XmlNode media;
            if (isAudio)
            {
                media = El("media",
                [
                    El("audio",
                    [
                        El("samplecharacteristics", [Leaf("samplerate", 48000), Leaf("depth", 16)]),
                        Leaf("channelcount", 2),
                    ]),
                ]);
            }
            else
            {
                var scKids = new List<XmlNode>();
                if (isImage) scKids.Add(Leaf("duration", 1));
                scKids.Add(El("samplecharacteristics",
                [
                    Leaf("width", entry?.SourceWidth ?? _seqWidth),
                    Leaf("height", entry?.SourceHeight ?? _seqHeight),
                    Bool("anamorphic", false),
                    Leaf("pixelaspectratio", "square"),
                    Leaf("fielddominance", "none"),
                    Rate(timebase, ntsc),
                ]));
                media = El("media", [El("video", scKids)]);
            }

            _startTimecodes.TryGetValue(mediaRef, out var sourceTc);
            var tc = TimecodeTags(sourceTc, timebase, ntsc);
            var timecode = El("timecode",
            [
                Rate(tc.Base, tc.Ntsc),
                Leaf("string", tc.String),
                Leaf("frame", tc.Frame),
                Leaf("displayformat", tc.DropFrame ? "DF" : "NDF"),
            ]);

            return El("file", [("id", fileId)],
            [
                Leaf("name", fileName),
                Leaf("pathurl", pathUrl),
                Rate(timebase, ntsc),
                Leaf("duration", durationFrames),
                timecode,
                media,
            ]);
        }

        private int? SourceDurationFrames(string mediaRef)
        {
            var entry = _resolver.Entry(mediaRef);
            if (entry is null) return null;
            if (entry.Type == ClipType.image) return 1;
            return Math.Max(0, (int)Math.Round(entry.Duration * _fps));
        }

        private string DisplayName(string mediaRef)
            => _resolver.Entry(mediaRef)?.Name is { Length: > 0 } n ? n : mediaRef;

        private static XmlNode? TimeRemapFilter(double speed, bool isAudio)
        {
            if (Math.Abs(speed - 1.0) < 1e-9) return null;
            // Premiere Time Remap: parameter is speed percent.
            var pct = speed * 100.0;
            return El("filter",
            [
                El("effect",
                [
                    Leaf("name", "Time Remap"),
                    Leaf("effectid", "timeremap"),
                    Leaf("effecttype", "motion"),
                    Leaf("mediatype", isAudio ? "audio" : "video"),
                    El("parameter",
                    [
                        Leaf("parameterid", "speed"),
                        Leaf("name", "Speed"),
                        Leaf("value", pct.ToString("0.###", CultureInfo.InvariantCulture)),
                    ]),
                ]),
            ]);
        }

        private static List<XmlNode> VolumeFilters(Clip clip)
        {
            var list = new List<XmlNode>();
            // XMEML audio levels: log scale; 0 dB ≈ 1.0 linear for static volume.
            var level = Math.Clamp(clip.Volume, 0, 4);
            list.Add(El("filter",
            [
                El("effect",
                [
                    Leaf("name", "Audio Levels"),
                    Leaf("effectid", "audiolevels"),
                    Leaf("effecttype", "audiolevels"),
                    Leaf("mediatype", "audio"),
                    El("parameter",
                    [
                        Leaf("parameterid", "level"),
                        Leaf("name", "Level"),
                        Leaf("value", level.ToString("0.######", CultureInfo.InvariantCulture)),
                    ]),
                ]),
            ]));
            return list;
        }

        private static List<XmlNode> VideoFilters(Clip clip)
        {
            var list = new List<XmlNode>();
            var opacity = Math.Clamp(clip.Opacity, 0, 1);
            if (opacity < 0.999)
            {
                list.Add(El("filter",
                [
                    El("effect",
                    [
                        Leaf("name", "Opacity"),
                        Leaf("effectid", "opacity"),
                        Leaf("effecttype", "motion"),
                        Leaf("mediatype", "video"),
                        El("parameter",
                        [
                            Leaf("parameterid", "opacity"),
                            Leaf("name", "Opacity"),
                            Leaf("value", (opacity * 100).ToString("0.###", CultureInfo.InvariantCulture)),
                        ]),
                    ]),
                ]));
            }
            return list;
        }

        private List<XmlNode> LinkNodes(Clip clip)
        {
            if (string.IsNullOrEmpty(clip.LinkGroupId)
                || !_clipsByLinkGroup.TryGetValue(clip.LinkGroupId, out var group))
                return [];

            var links = new List<XmlNode>();
            foreach (var other in group)
            {
                if (!_clipAddresses.TryGetValue(other.Id, out var addr)) continue;
                links.Add(El("link",
                [
                    Leaf("linkclipref", $"clipitem-{other.Id}"),
                    Leaf("mediatype", addr.IsAudio ? "audio" : "video"),
                    Leaf("trackindex", addr.TrackIndex),
                    Leaf("clipindex", addr.ClipIndex),
                ]));
            }
            return links;
        }

        private XmlNode TimecodeNode() => El("timecode",
        [
            Rate(_fps),
            Leaf("string", "00:00:00:00"),
            Leaf("frame", 0),
            Leaf("source", "source"),
            Leaf("displayformat", "NDF"),
        ]);

        private XmlNode VideoFormatNode(int width, int height) => El("format",
        [
            El("samplecharacteristics",
            [
                Leaf("width", Math.Max(1, width)),
                Leaf("height", Math.Max(1, height)),
                Bool("anamorphic", false),
                Leaf("pixelaspectratio", "square"),
                Leaf("fielddominance", "none"),
                Rate(_fps),
            ]),
        ]);

        private static XmlNode AudioFormatNode() => El("format",
        [
            El("samplecharacteristics", [Leaf("samplerate", 48000), Leaf("depth", 16)]),
        ]);

        private static XmlNode AudioOutputsNode() => El("outputs",
        [
            El("group",
            [
                Leaf("index", 1),
                Leaf("numchannels", 2),
                Leaf("downmix", 0),
                El("channel", [Leaf("index", 1)]),
                El("channel", [Leaf("index", 2)]),
            ]),
        ]);

        private static (int Timebase, bool Ntsc) RateTags(double fps)
        {
            // Common NTSC families.
            if (Near(fps, 29.97) || Near(fps, 59.94) || Near(fps, 23.976))
                return ((int)Math.Round(fps * 1001.0 / 1000.0), true);
            return (Math.Max(1, (int)Math.Round(fps)), false);
        }

        private static bool Near(double a, double b) => Math.Abs(a - b) < 0.02;

        private static XmlNode Rate(int timebase, bool ntsc = false) => El("rate",
        [
            Leaf("timebase", timebase),
            Bool("ntsc", ntsc),
        ]);
    }

    // --- Minimal XML tree ---

    private sealed class XmlNode
    {
        public required string Name { get; init; }
        public List<(string K, string V)> Attrs { get; init; } = [];
        public string? Text { get; init; }
        public List<XmlNode> Children { get; init; } = [];
    }

    private static XmlNode El(string name, IEnumerable<XmlNode> children)
        => new() { Name = name, Children = children.ToList() };

    private static XmlNode El(string name, IEnumerable<(string, string)> attrs, IEnumerable<XmlNode>? children = null)
        => new() { Name = name, Attrs = attrs.ToList(), Children = children?.ToList() ?? [] };

    private static XmlNode El(string name, IEnumerable<(string, string)> attrs)
        => new() { Name = name, Attrs = attrs.ToList() };

    private static XmlNode Leaf(string name, string value)
        => new() { Name = name, Text = value };

    private static XmlNode Leaf(string name, int value)
        => Leaf(name, value.ToString(CultureInfo.InvariantCulture));

    private static XmlNode Bool(string name, bool value)
        => Leaf(name, value ? "TRUE" : "FALSE");

    private static string RenderXml(XmlNode node, int indent)
    {
        var pad = new string(' ', indent * 2);
        var sb = new StringBuilder();
        sb.Append(pad).Append('<').Append(node.Name);
        foreach (var (k, v) in node.Attrs)
            sb.Append(' ').Append(k).Append("=\"").Append(WebUtility.HtmlEncode(v)).Append('"');

        if (node.Text is not null && node.Children.Count == 0)
        {
            sb.Append('>').Append(WebUtility.HtmlEncode(node.Text)).Append("</").Append(node.Name).Append('>');
            return sb.ToString();
        }

        if (node.Children.Count == 0)
        {
            sb.Append("/>");
            return sb.ToString();
        }

        sb.Append('>');
        foreach (var child in node.Children)
        {
            sb.Append('\n').Append(RenderXml(child, indent + 1));
        }
        sb.Append('\n').Append(pad).Append("</").Append(node.Name).Append('>');
        return sb.ToString();
    }
}
