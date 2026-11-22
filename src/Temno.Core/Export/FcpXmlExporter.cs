using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Temno.Core.Editing;
using Temno.Core.Models;
using Temno.Core.IO;

namespace Temno.Core.Export;

/// <remarks>
/// Every time is an exact rational of seconds. A nested sequence is written at its own frame rate
/// and size; its carrier converts placement once, in the parent's frames. Markers, transitions and
/// clips the format cannot hold are counted in the returned <see cref="InterchangeExportReport"/>;
/// <see cref="NotTransported"/> names the clip properties this output never carries.
/// </remarks>
public static class FcpXmlExporter
{
    public const string FormatName = "fcpxml";

    /// <summary>Clip properties FCPXML output never carries, whatever the timeline holds.</summary>
    public static readonly IReadOnlyList<string> NotTransported = new[]
    {
        "fades",
        "effect stacks",
        "blend modes",
        "volume keyframes",
        "crop keyframes",
        "keyframe interpolation other than linear",
        "text animation and word timing",
    };

    public static InterchangeExportReport Export(
        Timeline timeline,
        MediaResolver resolver,
        string outputPath,
        Func<string, Timeline?>? resolveTimeline = null,
        FcpXmlVersion version = FcpXmlVersion.V1_10,
        FcpXmlTarget target = FcpXmlTarget.Resolve,
        IDictionary<string, SourceTimecode>? startTimecodes = null)
    {
        var (xml, report) = RenderWithReport(timeline, resolver, resolveTimeline, version, target, startTimecodes);
        File.WriteAllText(outputPath, xml, Encoding.UTF8);
        return report;
    }

    public static InterchangeExportReport Export(
        ProjectFile projectFile,
        MediaManifest manifest,
        string projectPath,
        string outputPath,
        FcpXmlVersion version = FcpXmlVersion.V1_10,
        FcpXmlTarget target = FcpXmlTarget.Resolve,
        IDictionary<string, SourceTimecode>? startTimecodes = null)
    {
        var (timeline, resolver, resolveTimeline) = ActiveTimeline(projectFile, manifest, projectPath);
        return Export(timeline, resolver, outputPath, resolveTimeline, version, target, startTimecodes);
    }

    public static string Render(
        Timeline timeline,
        MediaResolver resolver,
        Func<string, Timeline?>? resolveTimeline = null,
        FcpXmlVersion version = FcpXmlVersion.V1_10,
        FcpXmlTarget target = FcpXmlTarget.Resolve,
        IDictionary<string, SourceTimecode>? startTimecodes = null)
        => RenderWithReport(timeline, resolver, resolveTimeline, version, target, startTimecodes).Xml;

    public static (string Xml, InterchangeExportReport Report) RenderWithReport(
        Timeline timeline,
        MediaResolver resolver,
        Func<string, Timeline?>? resolveTimeline = null,
        FcpXmlVersion version = FcpXmlVersion.V1_10,
        FcpXmlTarget target = FcpXmlTarget.Resolve,
        IDictionary<string, SourceTimecode>? startTimecodes = null)
    {
        var builder = new Builder(
            timeline,
            resolver,
            resolveTimeline ?? (id => null),
            version,
            target,
            startTimecodes ?? new Dictionary<string, SourceTimecode>()
        );
        var xml = builder.Build();
        return (xml, new InterchangeExportReport
        {
            Format = FormatName,
            Omissions = builder.Omissions.ToOmissions("FCPXML"),
            NotTransported = NotTransported,
        });
    }

    public static string Render(
        ProjectFile projectFile,
        MediaManifest manifest,
        string projectPath,
        FcpXmlVersion version = FcpXmlVersion.V1_10,
        FcpXmlTarget target = FcpXmlTarget.Resolve,
        IDictionary<string, SourceTimecode>? startTimecodes = null)
    {
        var (timeline, resolver, resolveTimeline) = ActiveTimeline(projectFile, manifest, projectPath);
        return Render(timeline, resolver, resolveTimeline, version, target, startTimecodes);
    }

    private static (Timeline Timeline, MediaResolver Resolver, Func<string, Timeline?> ResolveTimeline) ActiveTimeline(
        ProjectFile projectFile,
        MediaManifest manifest,
        string projectPath)
    {
        var activeTimeline = projectFile.Timelines.FirstOrDefault(t => t.Id == projectFile.ActiveTimelineId)
            ?? projectFile.Timelines.FirstOrDefault()
            ?? throw new ArgumentException("Project has no timelines.");

        var resolver = new MediaResolver(() => manifest, () => projectPath);
        Timeline? ResolveTimeline(string id) => projectFile.Timelines.FirstOrDefault(t => t.Id == id);
        return (activeTimeline, resolver, ResolveTimeline);
    }

    /// <summary>An exact, reduced number of seconds, rendered as FCPXML time (<c>n/ds</c>).</summary>
    private readonly record struct Seconds(long N, long D) : IComparable<Seconds>
    {
        public static readonly Seconds Zero = new(0, 1);

        public static Seconds Of(long numerator, long denominator)
        {
            if (denominator < 0) (numerator, denominator) = (-numerator, -denominator);
            var g = Gcd(Math.Abs(numerator), denominator);
            return g == 0 ? Zero : new Seconds(numerator / g, denominator / g);
        }

        public static Seconds Frames(long frames, int fps) => Of(frames, Math.Max(1, fps));

        public static Seconds operator +(Seconds a, Seconds b) => Of(a.N * b.D + b.N * a.D, a.D * b.D);

        /// <summary>This duration multiplied by <paramref name="p"/>/<paramref name="q"/>.</summary>
        public Seconds Times(long p, long q) => Of(N * p, D * q);

        public int CompareTo(Seconds other) => (N * other.D).CompareTo(other.N * D);

        public static Seconds Max(Seconds a, Seconds b) => a.CompareTo(b) >= 0 ? a : b;

        public override string ToString() => N == 0 ? "0s" : D == 1 ? $"{N}s" : $"{N}/{D}s";

        private static long Gcd(long a, long b)
        {
            while (b != 0) (a, b) = (b, a % b);
            return a;
        }
    }

    private sealed class FcpXmlNode
    {
        public string Name { get; }
        public List<(string Name, string Value)> Attributes { get; } = new();
        public string? Text { get; }
        public List<FcpXmlNode> Children { get; } = new();

        public FcpXmlNode(string name, List<(string, string)> attributes, List<FcpXmlNode> children)
        {
            Name = name;
            Attributes.AddRange(attributes);
            Children.AddRange(children);
        }

        public FcpXmlNode(string name, List<(string, string)> attributes, string text)
        {
            Name = name;
            Attributes.AddRange(attributes);
            Text = text;
        }

        public FcpXmlNode(string name, List<(string, string)> attributes)
        {
            Name = name;
            Attributes.AddRange(attributes);
        }

        public FcpXmlNode(string name, List<FcpXmlNode> children)
        {
            Name = name;
            Children.AddRange(children);
        }

        public FcpXmlNode(string name)
        {
            Name = name;
        }
    }

    private static string EscapeFcpXml(string s)
    {
        return s.Replace("&", "&amp;")
                .Replace("<", "&lt;")
                .Replace(">", "&gt;")
                .Replace("\"", "&quot;")
                .Replace("'", "&apos;");
    }

    private static string RenderFcpXml(FcpXmlNode node, int indent)
    {
        var pad = new string(' ', indent);
        var attrs = string.Join("", node.Attributes.Select(attr => $" {attr.Name}=\"{EscapeFcpXml(attr.Value)}\""));
        if (node.Text != null)
        {
            return $"{pad}<{node.Name}{attrs}>{EscapeFcpXml(node.Text)}</{node.Name}>";
        }
        if (node.Children.Count == 0)
        {
            return $"{pad}<{node.Name}{attrs}/>";
        }
        var inner = string.Join("\n", node.Children.Select(child => RenderFcpXml(child, indent + 2)));
        return $"{pad}<{node.Name}{attrs}>\n{inner}\n{pad}</{node.Name}>";
    }

    private sealed class Builder
    {
        private readonly Timeline _timeline;
        private readonly MediaResolver _resolver;
        private readonly Func<string, Timeline?> _resolveTimeline;
        private readonly FcpXmlVersion _version;
        private readonly FcpXmlTarget _target;
        private readonly IDictionary<string, SourceTimecode> _startTimecodes;
        private readonly int _rootFps;
        private readonly int _rootWidth;
        private readonly int _rootHeight;
        // The sequence whose story is being written. A nested sequence keeps its own rate and
        // frame size, so every time, position and crop inside it is computed against those.
        private int _fps;
        private int _seqWidth;
        private int _seqHeight;
        private readonly string _sequenceFormatId = "r1";
        private readonly string _titleEffectId = "titleBasic";
        private readonly Dictionary<string, int> _resourceIndex = new();
        private readonly List<MediaResource> _resources = new();
        private int _nextTextStyleId = 1;
        private readonly Dictionary<string, Clip> _linkedAudioForVideo = new();
        private readonly HashSet<string> _redundantAudioClipIds = new();
        private readonly HashSet<string> _usedCompoundIds = new();
        private readonly List<(string MediaId, Timeline Timeline)> _nests = new();
        private readonly Dictionary<string, string> _nestIndex = new();

        public InterchangeOmissionTally Omissions { get; } = new();

        private sealed class EmittableClip
        {
            public Clip Clip { get; }
            public int Lane { get; }
            public bool Enabled { get; }
            /// <summary>Frame rate of the timeline the clip sits in.</summary>
            public int Fps { get; }

            public EmittableClip(Clip clip, int lane, bool enabled, int fps)
            {
                Clip = clip;
                Lane = lane;
                Enabled = enabled;
                Fps = fps;
            }
        }

        private sealed class MediaResource
        {
            public string MediaRef { get; }
            public string AssetId { get; }
            public string? FormatId { get; }
            public string? CompoundId { get; }
            public MediaManifestEntry Entry { get; }
            public Uri Url { get; }
            /// <summary>Source length, independent of any sequence's frame rate.</summary>
            public Seconds Duration { get; }
            public bool HasVideo { get; }
            public bool HasAudio { get; }
            /// <summary>Embedded start timecode: the asset's own time at its first frame.</summary>
            public Seconds Origin { get; }

            public MediaResource(
                string mediaRef,
                string assetId,
                string? formatId,
                string? compoundId,
                MediaManifestEntry entry,
                Uri url,
                Seconds duration,
                bool hasVideo,
                bool hasAudio,
                Seconds origin)
            {
                MediaRef = mediaRef;
                AssetId = assetId;
                FormatId = formatId;
                CompoundId = compoundId;
                Entry = entry;
                Url = url;
                Duration = duration;
                HasVideo = hasVideo;
                HasAudio = hasAudio;
                Origin = origin;
            }
        }

        public Builder(
            Timeline timeline,
            MediaResolver resolver,
            Func<string, Timeline?> resolveTimeline,
            FcpXmlVersion version,
            FcpXmlTarget target,
            IDictionary<string, SourceTimecode> startTimecodes)
        {
            _timeline = timeline;
            _resolver = resolver;
            _resolveTimeline = resolveTimeline;
            _version = version;
            _target = target;
            _startTimecodes = startTimecodes;
            _rootFps = Math.Max(1, timeline.Fps);
            _rootWidth = timeline.Width;
            _rootHeight = timeline.Height;
            (_fps, _seqWidth, _seqHeight) = (_rootFps, _rootWidth, _rootHeight);
        }

        private T InSequence<T>(Timeline timeline, Func<T> build)
        {
            var saved = (_fps, _seqWidth, _seqHeight);
            (_fps, _seqWidth, _seqHeight) = (Math.Max(1, timeline.Fps), timeline.Width, timeline.Height);
            try
            {
                return build();
            }
            finally
            {
                (_fps, _seqWidth, _seqHeight) = saved;
            }
        }

        public string Build()
        {
            CollectNests();
            var clips = EmittableClips(_timeline);
            var nestedClips = _nests.SelectMany(n => EmittableClips(n.Timeline)).ToList();
            var allClips = clips.Concat(nestedClips).ToList();

            CollectResources(allClips);
            IndexLinkedPairs(allClips);
            MarkUsedCompounds(allClips);

            bool hasTitles = allClips.Any(c => c.Clip.MediaType == ClipType.text);

            var root = new FcpXmlNode("fcpxml",
                new List<(string, string)> { ("version", _version.ToRawValue()) },
                new List<FcpXmlNode>
                {
                    ResourcesNode(hasTitles),
                    LibraryNode(clips)
                });

            return "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<!DOCTYPE fcpxml>\n" + RenderFcpXml(root, 0);
        }

        private void CollectNests()
        {
            var reachable = _timeline.GetReachableTimelines(
                resolve: _resolveTimeline,
                maxDepth: 8,
                include: t => t.TotalFrames > 0
            );
            foreach (var child in reachable)
            {
                var mediaId = $"nest{_nests.Count + 1}";
                _nestIndex[child.Id] = mediaId;
                _nests.Add((mediaId, child));
            }
        }

        private void IndexLinkedPairs(List<EmittableClip> clips)
        {
            var byGroup = new Dictionary<string, (List<EmittableClip> Videos, List<EmittableClip> Audios)>();
            foreach (var item in clips)
            {
                var group = item.Clip.LinkGroupId;
                if (group == null) continue;

                if (!byGroup.TryGetValue(group, out var pair))
                {
                    pair = (new List<EmittableClip>(), new List<EmittableClip>());
                    byGroup[group] = pair;
                }

                switch (item.Clip.MediaType)
                {
                    case ClipType.video:
                    case ClipType.image:
                    case ClipType.sequence:
                        pair.Videos.Add(item);
                        break;
                    case ClipType.audio:
                        pair.Audios.Add(item);
                        break;
                }
            }

            foreach (var pair in byGroup.Values)
            {
                if (pair.Videos.Count == 1 && pair.Audios.Count == 1)
                {
                    var v = pair.Videos[0];
                    var a = pair.Audios[0];
                    if (v.Clip.MediaRef == a.Clip.MediaRef &&
                        v.Enabled == a.Enabled &&
                        v.Clip.StartFrame == a.Clip.StartFrame &&
                        v.Clip.DurationFrames == a.Clip.DurationFrames &&
                        v.Clip.TrimStartFrame == a.Clip.TrimStartFrame &&
                        Math.Abs(v.Clip.Speed - a.Clip.Speed) < 0.0001)
                    {
                        _linkedAudioForVideo[v.Clip.Id] = a.Clip;
                        _redundantAudioClipIds.Add(a.Clip.Id);
                    }
                }
            }
        }

        private void MarkUsedCompounds(List<EmittableClip> clips)
        {
            foreach (var item in clips)
            {
                if (_redundantAudioClipIds.Contains(item.Clip.Id)) continue;
                if (_resourceIndex.TryGetValue(item.Clip.MediaRef, out int i))
                {
                    var res = _resources[i];
                    if (res.CompoundId != null && !_linkedAudioForVideo.ContainsKey(item.Clip.Id))
                    {
                        _usedCompoundIds.Add(res.CompoundId);
                    }
                }
            }
        }

        private FcpXmlNode ResourcesNode(bool hasTitles)
        {
            var children = new List<FcpXmlNode>
            {
                new FcpXmlNode("format", new List<(string, string)>
                {
                    ("id", _sequenceFormatId),
                    ("name", SequenceFormatName(_rootWidth, _rootHeight, _rootFps)),
                    ("frameDuration", FrameDuration(_rootFps)),
                    ("width", _rootWidth.ToString()),
                    ("height", _rootHeight.ToString()),
                    ("colorSpace", "1-1-1 (Rec. 709)"),
                })
            };

            if (hasTitles)
            {
                children.Add(new FcpXmlNode("effect", new List<(string, string)>
                {
                    ("id", _titleEffectId),
                    ("name", "Basic Title"),
                    ("uid", ".../Titles.localized/Bumper:Opener.localized/Basic Title.localized/Basic Title.moti"),
                }));
            }

            foreach (var res in _resources)
            {
                var fNode = FormatNode(res);
                if (fNode != null) children.Add(fNode);
            }

            foreach (var res in _resources)
            {
                children.Add(AssetNode(res));
            }

            foreach (var res in _resources)
            {
                var cNode = CompoundClipNode(res);
                if (cNode != null) children.Add(cNode);
            }

            foreach (var nest in _nests)
            {
                var nfNode = NestFormatNode(nest);
                if (nfNode != null) children.Add(nfNode);
            }

            foreach (var nest in _nests)
            {
                children.Add(NestMediaNode(nest));
            }

            return new FcpXmlNode("resources", children);
        }

        /// <summary>A nest shares the root format only when its size and its frame rate both match.</summary>
        private string NestFormatId((string MediaId, Timeline Timeline) nest)
        {
            return (nest.Timeline.Width == _rootWidth
                    && nest.Timeline.Height == _rootHeight
                    && Math.Max(1, nest.Timeline.Fps) == _rootFps)
                ? _sequenceFormatId
                : $"{nest.MediaId}Format";
        }

        private FcpXmlNode? NestFormatNode((string MediaId, Timeline Timeline) nest)
        {
            var formatId = NestFormatId(nest);
            if (formatId == _sequenceFormatId) return null;
            var nestFps = Math.Max(1, nest.Timeline.Fps);
            return new FcpXmlNode("format", new List<(string, string)>
            {
                ("id", formatId),
                ("name", SequenceFormatName(nest.Timeline.Width, nest.Timeline.Height, nestFps)),
                ("frameDuration", FrameDuration(nestFps)),
                ("width", nest.Timeline.Width.ToString()),
                ("height", nest.Timeline.Height.ToString()),
                ("colorSpace", "1-1-1 (Rec. 709)"),
            });
        }

        private FcpXmlNode NestMediaNode((string MediaId, Timeline Timeline) nest)
        {
            var (duration, story) = InSequence(nest.Timeline, () =>
                (Time(nest.Timeline.TotalFrames), StoryNodes(nest.Timeline, EmittableClips(nest.Timeline))));
            var gap = new FcpXmlNode("gap", new List<(string, string)>
            {
                ("name", "Timeline"),
                ("offset", "0s"),
                ("start", "0s"),
                ("duration", duration),
            }, story);

            var sequence = new FcpXmlNode("sequence", new List<(string, string)>
            {
                ("format", NestFormatId(nest)),
                ("duration", duration),
                ("tcStart", "0s"),
                ("tcFormat", "NDF"),
                ("audioLayout", "stereo"),
                ("audioRate", "48k"),
            }, new List<FcpXmlNode> { new FcpXmlNode("spine", new List<FcpXmlNode> { gap }) });

            return new FcpXmlNode("media", new List<(string, string)>
            {
                ("id", nest.MediaId),
                ("name", nest.Timeline.Name),
            }, new List<FcpXmlNode> { sequence });
        }

        private FcpXmlNode? CompoundClipNode(MediaResource resource)
        {
            if (resource.CompoundId == null || !_usedCompoundIds.Contains(resource.CompoundId)) return null;
            var dur = resource.Duration.ToString();
            var tcStart = resource.Origin.ToString();
            var innerClip = new FcpXmlNode("asset-clip", new List<(string, string)>
            {
                ("ref", resource.AssetId),
                ("name", FileName(resource)),
                ("duration", dur),
                ("start", tcStart),
                ("offset", "0s"),
                ("format", resource.FormatId ?? _sequenceFormatId),
            });

            var sequence = new FcpXmlNode("sequence", new List<(string, string)>
            {
                ("format", resource.FormatId ?? _sequenceFormatId),
                ("duration", dur),
                ("tcStart", "0s"),
                ("tcFormat", "NDF"),
            }, new List<FcpXmlNode> { new FcpXmlNode("spine", new List<FcpXmlNode> { innerClip }) });

            return new FcpXmlNode("media", new List<(string, string)>
            {
                ("id", resource.CompoundId),
                ("name", FileName(resource)),
            }, new List<FcpXmlNode> { sequence });
        }

        private FcpXmlNode LibraryNode(List<EmittableClip> clips)
        {
            return new FcpXmlNode("library", new List<FcpXmlNode>
            {
                new FcpXmlNode("event", new List<(string, string)> { ("name", "Temno Export") }, new List<FcpXmlNode>
                {
                    ProjectNode(clips)
                })
            });
        }

        private FcpXmlNode ProjectNode(List<EmittableClip> clips)
        {
            var duration = Time(_timeline.TotalFrames);
            FcpXmlNode spine;
            if (_timeline.TotalFrames > 0)
            {
                var gap = new FcpXmlNode("gap", new List<(string, string)>
                {
                    ("name", "Timeline"),
                    ("offset", "0s"),
                    ("start", "0s"),
                    ("duration", duration),
                }, StoryNodes(_timeline, clips));
                spine = new FcpXmlNode("spine", new List<FcpXmlNode> { gap });
            }
            else
            {
                // No story to walk, but its markers are still not in the file.
                Omissions.TimelineWritten(_timeline);
                spine = new FcpXmlNode("spine");
            }

            return new FcpXmlNode("project", new List<(string, string)> { ("name", _timeline.Name) }, new List<FcpXmlNode>
            {
                new FcpXmlNode("sequence", new List<(string, string)>
                {
                    ("format", _sequenceFormatId),
                    ("duration", duration),
                    ("tcStart", "0s"),
                    ("tcFormat", "NDF"),
                    ("audioLayout", "stereo"),
                    ("audioRate", "48k"),
                }, new List<FcpXmlNode> { spine })
            });
        }

        private List<FcpXmlNode> StoryNodes(Timeline timeline, List<EmittableClip> clips)
        {
            Omissions.TimelineWritten(timeline);
            foreach (var clip in timeline.Tracks.SelectMany(t => t.Clips))
            {
                if (CarriesContent(clip) && !IsEmittable(clip))
                    Omissions.ClipSkipped(timeline, SkipReason(clip));
            }

            var sorted = clips
                .Where(c => !_redundantAudioClipIds.Contains(c.Clip.Id))
                .OrderBy(c => c.Clip.StartFrame)
                .ThenBy(c => c.Lane)
                .ToList();

            var nodes = new List<FcpXmlNode>();
            foreach (var item in sorted)
            {
                FcpXmlNode? node = null;
                switch (item.Clip.MediaType)
                {
                    case ClipType.text:
                        node = TitleNode(item);
                        break;
                    case ClipType.audio:
                    case ClipType.video:
                    case ClipType.image:
                    case ClipType.sequence:
                        node = AssetClipNode(item);
                        break;
                }
                if (node != null)
                {
                    nodes.Add(node);
                    if (item.Clip.SourceClipType == ClipType.sequence)
                        Omissions.CarrierWritten(timeline, item.Clip);
                }
                else
                {
                    Omissions.ClipSkipped(timeline, SkipReason(item.Clip));
                }
            }
            return nodes;
        }

        /// <summary>False for a clip that shows and sounds like nothing, so leaving it out loses nothing.</summary>
        private static bool CarriesContent(Clip clip)
            => clip.DurationFrames > 0
               && !(clip.MediaType == ClipType.text && string.IsNullOrEmpty(clip.TextContent));

        private string SkipReason(Clip clip)
        {
            if (clip.SourceClipType == ClipType.sequence || clip.MediaType == ClipType.sequence)
            {
                if (_resolveTimeline(clip.MediaRef) is not { } child) return "unresolved nested sequence";
                if (child.TotalFrames <= 0) return "empty nested sequence";
                return _nestIndex.ContainsKey(clip.MediaRef)
                    ? "nested sequence trimmed past its end"
                    : "recursive or more than 8 levels deep nested sequence";
            }
            return clip.MediaType == ClipType.lottie ? "Lottie" : "missing media";
        }

        private FcpXmlNode? AssetClipNode(EmittableClip item)
        {
            var clip = item.Clip;

            if (clip.SourceClipType == ClipType.sequence)
            {
                if (!_nestIndex.TryGetValue(clip.MediaRef, out var mediaId)) return null;
                var child = _resolveTimeline(clip.MediaRef);
                if (child == null) return null;

                // The carrier's trim is in parent frames (the renderer's rule), so the child's
                // length is converted to parent frames, floored to stay inside its content.
                var childFrames = (int)((long)child.TotalFrames * _fps / Math.Max(1, child.Fps));
                var duration = Math.Min(clip.DurationFrames, Math.Max(0, childFrames - clip.TrimStartFrame));
                if (duration <= 0) return null;

                var attrs = new List<(string, string)>
                {
                    ("ref", mediaId),
                    ("name", child.Name),
                    ("lane", item.Lane.ToString()),
                    ("offset", Time(clip.StartFrame)),
                    ("start", Time(clip.TrimStartFrame)),
                    ("duration", Time(duration)),
                    ("enabled", item.Enabled ? "1" : "0"),
                };

                if (clip.MediaType == ClipType.audio)
                {
                    attrs.Add(("srcEnable", "audio"));
                    var vol = VolumeNode(clip);
                    var children = vol != null ? new List<FcpXmlNode> { vol } : new List<FcpXmlNode>();
                    return new FcpXmlNode("ref-clip", attrs, children);
                }

                _linkedAudioForVideo.TryGetValue(clip.Id, out var nestLinkedAudio);
                if (nestLinkedAudio == null)
                {
                    attrs.Add(("srcEnable", "video"));
                }

                var refChildren = new List<FcpXmlNode?>
                {
                    new FcpXmlNode("adjust-conform", new List<(string, string)> { ("type", "fit") }),
                    CropNode(clip),
                    TransformNode(clip),
                    BlendNode(clip),
                    nestLinkedAudio != null ? VolumeNode(nestLinkedAudio) : null
                }.Where(x => x != null).Cast<FcpXmlNode>().ToList();

                return new FcpXmlNode("ref-clip", attrs, refChildren);
            }

            if (!_resourceIndex.TryGetValue(clip.MediaRef, out int i)) return null;
            var resource = _resources[i];
            _linkedAudioForVideo.TryGetValue(clip.Id, out var linkedAudio);

            if (resource.CompoundId != null && linkedAudio == null)
            {
                bool videoOnly = clip.MediaType != ClipType.audio;
                var attrs = new List<(string, string)>
                {
                    ("ref", resource.CompoundId),
                    ("name", FileName(resource)),
                    ("lane", item.Lane.ToString()),
                    ("offset", Time(clip.StartFrame)),
                    ("start", ClipStart(clip)),
                    ("duration", Time(clip.DurationFrames)),
                    ("enabled", item.Enabled ? "1" : "0"),
                    ("srcEnable", videoOnly ? "video" : "audio")
                };

                var children = videoOnly
                    ? new List<FcpXmlNode?>
                      {
                          TimeMapNode(clip, resource.Duration),
                          CropNode(clip),
                          new FcpXmlNode("adjust-conform", new List<(string, string)> { ("type", "fit") }),
                          TransformNode(clip),
                          BlendNode(clip)
                      }
                    : new List<FcpXmlNode?>
                      {
                          TimeMapNode(clip, resource.Duration),
                          VolumeNode(clip)
                      };

                return new FcpXmlNode("ref-clip", attrs, children.Where(x => x != null).Cast<FcpXmlNode>().ToList());
            }

            var origin = resource.Origin;
            bool visual = clip.MediaType != ClipType.audio;
            var flatAttrs = new List<(string, string)>
            {
                ("ref", resource.AssetId),
                ("name", FileName(resource)),
                ("lane", item.Lane.ToString()),
                ("offset", Time(clip.StartFrame)),
                ("start", ClipStart(clip, origin)),
                ("duration", Time(clip.DurationFrames)),
                ("enabled", item.Enabled ? "1" : "0")
            };

            var flatChildren = new List<FcpXmlNode?>
            {
                TimeMapNode(clip, resource.Duration, origin),
                visual ? CropNode(clip) : null,
                visual ? new FcpXmlNode("adjust-conform", new List<(string, string)> { ("type", "fit") }) : null,
                visual ? TransformNode(clip) : null,
                visual ? BlendNode(clip) : null,
                resource.HasAudio ? VolumeNode(linkedAudio ?? clip) : null
            }.Where(x => x != null).Cast<FcpXmlNode>().ToList();

            var nodeName = clip.MediaType == ClipType.image ? "video" : "asset-clip";
            return new FcpXmlNode(nodeName, flatAttrs, flatChildren);
        }

        private FcpXmlNode? TitleNode(EmittableClip item)
        {
            var clip = item.Clip;
            var content = clip.TextContent;
            if (string.IsNullOrEmpty(content)) return null;

            var style = new TextStyle();
            if (clip.TextStyle is JsonElement textStyleEl && textStyleEl.ValueKind != JsonValueKind.Null)
            {
                try
                {
                    style = JsonSerializer.Deserialize<TextStyle>(textStyleEl.GetRawText()) ?? new TextStyle();
                }
                catch
                {
                }
            }

            var styleId = $"textStyle{_nextTextStyleId}";
            _nextTextStyleId++;

            var textNodes = new List<FcpXmlNode>
            {
                new FcpXmlNode("text", new List<FcpXmlNode>
                {
                    new FcpXmlNode("text-style", new List<(string, string)> { ("ref", styleId) }, content)
                }),
                new FcpXmlNode("text-style-def", new List<(string, string)> { ("id", styleId) }, new List<FcpXmlNode>
                {
                    new FcpXmlNode("text-style", TextStyleAttributes(style))
                })
            };

            textNodes.AddRange(TitleTransformNodes(clip.Transform));
            var blend = BlendNode(clip);
            if (blend != null)
            {
                textNodes.Add(blend);
            }

            return new FcpXmlNode("title", new List<(string, string)>
            {
                ("ref", _titleEffectId),
                ("name", content),
                ("lane", item.Lane.ToString()),
                ("offset", Time(clip.StartFrame)),
                ("start", "0s"),
                ("duration", Time(clip.DurationFrames)),
                ("enabled", item.Enabled ? "1" : "0"),
            }, textNodes);
        }

        private FcpXmlNode? BlendNode(Clip clip)
        {
            var frames = clip.GetKeyframeFrames(AnimatableProperty.Opacity);
            if (clip.Opacity >= 0.9995 && frames.Count == 0) return null;

            var children = new List<FcpXmlNode>();
            if (frames.Count > 0)
            {
                children.Add(KeyframeParam("amount", FormatNumber(clip.Opacity), clip,
                    AnimatableProperty.Opacity, frames, f => FormatNumber(clip.RawOpacityAt(f))));
            }

            return new FcpXmlNode("adjust-blend",
                new List<(string, string)> { ("amount", FormatNumber(clip.Opacity)) },
                children);
        }

        private FcpXmlNode? TransformNode(Clip clip)
        {
            var t = clip.Transform;
            var posFrames = clip.GetKeyframeFrames(AnimatableProperty.Position);
            var rotFrames = clip.GetKeyframeFrames(AnimatableProperty.Rotation);
            var scaleFrames = clip.GetKeyframeFrames(AnimatableProperty.Scale);
            var baseScale = ScaleValue(t.Width, t.Height, clip);

            bool moved = Math.Abs(t.CenterX - 0.5) > 0.0005 || Math.Abs(t.CenterY - 0.5) > 0.0005;
            bool rotated = Math.Abs(t.Rotation) > 0.005;
            bool scaled = baseScale != "1 1";

            if (!moved && !rotated && !scaled && posFrames.Count == 0 && rotFrames.Count == 0 && scaleFrames.Count == 0)
                return null;

            var fit = _target == FcpXmlTarget.Resolve ? FitFractions(clip) : (W: 1.0, H: 1.0);
            var attrs = new List<(string, string)>
            {
                ("scale", baseScale)
            };

            if (rotated || rotFrames.Count > 0)
            {
                attrs.Add(("rotation", FormatNumber(-t.Rotation)));
            }

            attrs.Add(("anchor", "0 0"));
            attrs.Add(("position", PositionValue(t, fit)));

            var paramsList = new List<FcpXmlNode>();
            if (scaleFrames.Count > 0)
            {
                paramsList.Add(KeyframeParam("scale", baseScale, clip, AnimatableProperty.Scale, scaleFrames, f =>
                {
                    var s = clip.SizeAt(f);
                    return ScaleValue(s.X, s.Y, clip);
                }));
            }

            if (posFrames.Count > 0)
            {
                paramsList.Add(KeyframeParam("position", PositionValue(t, fit), clip, AnimatableProperty.Position, posFrames, f =>
                {
                    return PositionValue(clip.TransformAt(f), fit);
                }));
            }

            if (rotFrames.Count > 0)
            {
                paramsList.Add(KeyframeParam("rotation", FormatNumber(-t.Rotation), clip, AnimatableProperty.Rotation, rotFrames, f =>
                {
                    return FormatNumber(-clip.RotationAt(f));
                }));
            }

            return new FcpXmlNode("adjust-transform", attrs, paramsList);
        }

        private string ScaleValue(double width, double height, Clip clip)
        {
            // WP-405: the scale of what Temno draws, the picture fitted inside its box, so a box of
            // another shape exports one uniform scale instead of a stretch.
            var entry = _resolver.Entry(clip.MediaRef);
            if (entry?.SourceWidth is int sw && entry.SourceHeight is int sh && sw > 0 && sh > 0)
            {
                var drawn = ClipPlacement.FitInBox(new Transform { Width = width, Height = height }, sw, sh, _seqWidth, _seqHeight);
                width = drawn.Width;
                height = drawn.Height;
            }
            var fit = FitFractions(clip);
            var sx = width / fit.W;
            var sy = height / fit.H;
            if (clip.Transform.FlipHorizontal) sx = -sx;
            if (clip.Transform.FlipVertical) sy = -sy;
            return $"{FormatNumber(sx)} {FormatNumber(sy)}";
        }

        private FcpXmlNode KeyframeParam(
            string name,
            string baseVal,
            Clip clip,
            AnimatableProperty property,
            List<int> frames,
            Func<int, string> valueFunc)
        {
            var keyframes = frames.OrderBy(f => f).Select(f =>
            {
                var attrs = new List<(string, string)>
                {
                    ("time", KeyframeTime(f, clip))
                };
                if (clip.GetInterpolation(property, f) == Interpolation.linear)
                {
                    attrs.Add(("curve", "linear"));
                }
                attrs.Add(("value", valueFunc(f)));
                return new FcpXmlNode("keyframe", attrs);
            }).ToList();

            return new FcpXmlNode("param", new List<(string, string)>
            {
                ("name", name),
                ("value", baseVal)
            }, new List<FcpXmlNode>
            {
                new FcpXmlNode("keyframeAnimation", keyframes)
            });
        }

        private string KeyframeTime(int f, Clip clip)
        {
            if (Math.Abs(clip.Speed - 1.0) <= 0.001)
            {
                return Time(f - clip.StartFrame);
            }
            var (p, q) = RationalSpeed(clip.Speed);
            var num = clip.TrimStartFrame * q + (f - clip.StartFrame) * p;
            return RationalTime(num, _fps * p);
        }

        private FcpXmlNode? CropNode(Clip clip)
        {
            var c = clip.Crop;
            if (c.IsIdentity) return null;

            double lr = 100.0;
            double tb = 100.0;

            if (_target == FcpXmlTarget.Resolve)
            {
                var entry = _resolver.Entry(clip.MediaRef);
                if (entry != null && entry.SourceWidth is int sw && entry.SourceHeight is int sh && sw > 0 && sh > 0)
                {
                    double fit = Math.Min((double)_seqWidth / sw, (double)_seqHeight / sh);
                    lr = sw * 100.0 / _seqHeight;
                    tb = 100.0 / fit;
                }
            }

            return new FcpXmlNode("adjust-crop", new List<(string, string)> { ("mode", "trim") }, new List<FcpXmlNode>
            {
                new FcpXmlNode("trim-rect", new List<(string, string)>
                {
                    ("top", FormatNumber(c.Top * tb)),
                    ("right", FormatNumber(c.Right * lr)),
                    ("bottom", FormatNumber(c.Bottom * tb)),
                    ("left", FormatNumber(c.Left * lr))
                })
            });
        }

        private FcpXmlNode? VolumeNode(Clip clip)
        {
            if (Math.Abs(clip.Volume - 1.0) <= 0.0005) return null;
            return new FcpXmlNode("adjust-volume", new List<(string, string)>
            {
                ("amount", FormatNumber(Decibels(clip.Volume)))
            });
        }

        private double Decibels(double linear)
        {
            return linear > 0 ? 20.0 * Math.Log10(linear) : -96.0;
        }

        /// <summary>
        /// Where the clip starts in its media's time. Unspeeded, that is the asset's timecode
        /// origin plus the trim. A retimed clip's local time is the timeMap's, whose values carry
        /// the origin, so its start is the trim scaled by 1/speed (Mac FCPXMLExporter.clipStart).
        /// </summary>
        private string ClipStart(Clip clip, Seconds? origin = null)
        {
            if (Math.Abs(clip.Speed - 1.0) <= 0.001)
            {
                return ((origin ?? Seconds.Zero) + Seconds.Frames(clip.TrimStartFrame, _fps)).ToString();
            }
            var (p, q) = RationalSpeed(clip.Speed);
            return RationalTime(clip.TrimStartFrame * q, _fps * p);
        }

        private FcpXmlNode? TimeMapNode(Clip clip, Seconds media, Seconds? origin = null)
        {
            if (Math.Abs(clip.Speed - 1.0) <= 0.001 || media.N <= 0) return null;
            var (p, q) = RationalSpeed(clip.Speed);
            var start = origin ?? Seconds.Zero;

            return new FcpXmlNode("timeMap", new List<(string, string)> { ("frameSampling", "floor") }, new List<FcpXmlNode>
            {
                new FcpXmlNode("timept", new List<(string, string)>
                {
                    ("time", "0s"),
                    ("value", start.ToString()),
                    ("interp", "linear")
                }),
                new FcpXmlNode("timept", new List<(string, string)>
                {
                    ("time", media.Times(q, p).ToString()),
                    ("value", (start + media).ToString()),
                    ("interp", "linear")
                })
            });
        }

        private (int P, int Q) RationalSpeed(double speed)
        {
            var best = (P: 1, Q: 1);
            var bestErr = double.PositiveInfinity;
            for (int q = 1; q <= 1000; q++)
            {
                int p = (int)Math.Round(speed * q);
                if (p <= 0) continue;
                double err = Math.Abs(speed - (double)p / q);
                if (err < bestErr)
                {
                    best = (p, q);
                    bestErr = err;
                    if (err == 0) break;
                }
            }
            return best;
        }

        private string RationalTime(int num, int den)
        {
            if (num == 0) return "0s";
            int g = Gcd(Math.Abs(num), Math.Abs(den));
            int n = num / g;
            int d = den / g;
            return d == 1 ? $"{n}s" : $"{n}/{d}s";
        }

        private int Gcd(int a, int b)
        {
            int x = a;
            int y = b;
            while (y != 0)
            {
                int r = x % y;
                x = y;
                y = r;
            }
            return Math.Max(1, x);
        }

        private sealed class Caps
        {
            public List<string> MediaRefs { get; } = new();
            public bool HasVideo { get; set; }
            public bool HasAudio { get; set; }
            public Seconds Duration { get; set; } = Seconds.Zero;
            public MediaManifestEntry Entry { get; }
            public Uri Url { get; }

            public Caps(MediaManifestEntry entry, Uri url)
            {
                Entry = entry;
                Url = url;
            }
        }

        private void CollectResources(List<EmittableClip> clips)
        {
            var order = new List<string>();
            var caps = new Dictionary<string, Caps>();

            foreach (var item in clips)
            {
                var clip = item.Clip;
                if (clip.MediaType == ClipType.text || clip.MediaType == ClipType.lottie) continue;

                var entry = _resolver.Entry(clip.MediaRef);
                if (entry == null) continue;

                // WP-399: the location is written as text and never read, so a file the session may
                // not use still keeps its clip in the interchange file.
                var path = _resolver.ReferencePath(entry);
                if (path == null) continue;

                // Path.GetFullPath + Uri works on Windows drive paths; raw new Uri("/assets/...") fails on Windows.
                var uri = PathToFileUri(path);
                var key = SourceKey(uri);

                var duration = SourceDuration(entry, clip, item.Fps);
                bool isVisual = clip.MediaType != ClipType.audio;
                bool isAudio = clip.MediaType == ClipType.audio || (clip.MediaType == ClipType.video && entry.HasAudio == true);

                if (!caps.TryGetValue(key, out var entryCaps))
                {
                    order.Add(key);
                    entryCaps = new Caps(entry, uri);
                    caps[key] = entryCaps;
                }

                if (!entryCaps.MediaRefs.Contains(clip.MediaRef))
                {
                    entryCaps.MediaRefs.Add(clip.MediaRef);
                }

                entryCaps.HasVideo = entryCaps.HasVideo || isVisual;
                entryCaps.HasAudio = entryCaps.HasAudio || isAudio;
                entryCaps.Duration = Seconds.Max(entryCaps.Duration, duration);
            }

            foreach (var key in order)
            {
                var c = caps[key];
                int id = _resources.Count + 1;

                foreach (var refStr in c.MediaRefs)
                {
                    _resourceIndex[refStr] = _resources.Count;
                }

                var origin = Seconds.Zero;
                foreach (var refStr in c.MediaRefs)
                {
                    if (_startTimecodes.TryGetValue(refStr, out var tc))
                    {
                        // Nominal count / quanta, exact: the same instant in every sequence that uses it.
                        origin = tc.Quanta > 0 ? Seconds.Of(tc.Frame, tc.Quanta) : Seconds.Zero;
                        break;
                    }
                }

                _resources.Add(new MediaResource(
                    mediaRef: c.MediaRefs.FirstOrDefault() ?? c.Entry.Id,
                    assetId: $"asset{id}",
                    formatId: c.HasVideo ? $"r{id + 1}" : null,
                    compoundId: (c.HasVideo && c.HasAudio) ? $"media{id}" : null,
                    entry: c.Entry,
                    url: c.Url,
                    duration: c.Duration,
                    hasVideo: c.HasVideo,
                    hasAudio: c.HasAudio,
                    origin: origin
                ));
            }
        }

        private string SourceKey(Uri url)
        {
            return Path.GetFullPath(url.LocalPath);
        }

        /// <summary>
        /// Convert a filesystem path (Mac absolute, Windows absolute, or relative) to a file URI.
        /// <c>new Uri("/assets/…")</c> works on Unix but throws on Windows — always go through full path.
        /// </summary>
        private static Uri PathToFileUri(string path)
        {
            if (path.Contains("://", StringComparison.Ordinal))
                return new Uri(path);

            return new Uri(Path.GetFullPath(path));
        }

        /// <summary>
        /// Media length as the manifest states it, counted on the frame grid the asset's own
        /// <c>format</c> declares (source rate, else the root's) so it is the same whichever sequence
        /// uses the asset — or as far as this clip reads, whichever is longer.
        /// </summary>
        private Seconds SourceDuration(MediaManifestEntry entry, Clip clip, int fps)
        {
            var frame = FrameLength(entry.SourceFps ?? _rootFps);
            var frames = Math.Max(0L, (long)Math.Round(entry.Duration * frame.D / frame.N));
            var manifest = Seconds.Of(frames * frame.N, frame.D);
            return Seconds.Max(manifest, Seconds.Frames(clip.SourceDurationFrames, fps));
        }

        /// <summary>One frame at <paramref name="rawFps"/>, NTSC-aware: the value <see cref="FrameDuration"/> writes.</summary>
        private static Seconds FrameLength(double rawFps)
        {
            int rounded = Math.Max(1, (int)Math.Round(rawFps));
            double ntscRate = rounded * 1000.0 / 1001.0;
            return Math.Abs(rawFps - ntscRate) < Math.Abs(rawFps - rounded)
                ? Seconds.Of(1001, rounded * 1000L)
                : Seconds.Of(1, rounded);
        }

        private List<EmittableClip> EmittableClips(Timeline timeline)
        {
            int visualTrackCount = timeline.Tracks.Count(t => t.Type.IsVisual());
            int visualOrdinal = 0;
            int audioOrdinal = 0;
            var clips = new List<EmittableClip>();

            foreach (var track in timeline.Tracks)
            {
                int lane;
                bool enabled;

                if (track.Type.IsVisual())
                {
                    lane = visualTrackCount - visualOrdinal;
                    enabled = !track.Hidden;
                    visualOrdinal++;
                }
                else if (track.Type == ClipType.audio)
                {
                    lane = -(audioOrdinal + 1);
                    enabled = !track.Muted;
                    audioOrdinal++;
                }
                else
                {
                    continue;
                }

                var fps = Math.Max(1, timeline.Fps);
                var trackClips = track.Clips
                    .Where(IsEmittable)
                    .OrderBy(c => c.StartFrame)
                    .Select(c => new EmittableClip(c, lane, enabled, fps));

                clips.AddRange(trackClips);
            }
            return clips;
        }

        private bool IsEmittable(Clip clip)
        {
            if (clip.DurationFrames <= 0) return false;
            if (clip.SourceClipType == ClipType.sequence)
            {
                return _nestIndex.ContainsKey(clip.MediaRef);
            }
            switch (clip.MediaType)
            {
                case ClipType.text:
                    return !string.IsNullOrEmpty(clip.TextContent);
                case ClipType.lottie:
                case ClipType.sequence:
                    return false;
                case ClipType.audio:
                case ClipType.video:
                case ClipType.image:
                    return _resolver.ReferencePath(clip.MediaRef) != null;
                default:
                    return false;
            }
        }

        private string Time(int frames)
        {
            if (frames == 0) return "0s";
            int divisor = Gcd(Math.Abs(frames), _fps);
            int numerator = frames / divisor;
            int denominator = _fps / divisor;
            return denominator == 1 ? $"{numerator}s" : $"{numerator}/{denominator}s";
        }

        private string VideoFormatName(int width, int height, double rawFps)
        {
            return RecognizedVideoFormatName(width, height, rawFps)
                ?? $"FFVideoFormat{width}x{height}p{FormatRateSuffix(rawFps)}";
        }

        private string SequenceFormatName(int width, int height, double rawFps)
        {
            return RecognizedVideoFormatName(width, height, rawFps) ?? "FFVideoFormatRateUndefined";
        }

        private string? RecognizedVideoFormatName(int width, int height, double rawFps)
        {
            var rate = FormatRateSuffix(rawFps);
            return (width, height) switch
            {
                (1280, 720) => $"FFVideoFormat720p{rate}",
                (1920, 1080) => $"FFVideoFormat1080p{rate}",
                (3840, 2160) => $"FFVideoFormat3840x2160p{rate}",
                (4096, 2160) => $"FFVideoFormat4096x2160p{rate}",
                _ => null
            };
        }

        private string FormatRateSuffix(double rawFps)
        {
            int rounded = Math.Max(1, (int)Math.Round(rawFps));
            double ntscRate = rounded * 1000.0 / 1001.0;
            if (Math.Abs(rawFps - ntscRate) < Math.Abs(rawFps - rounded))
            {
                int fps100 = (int)Math.Round(ntscRate * 100.0);
                return $"{fps100 / 100}{(fps100 % 100):D2}";
            }
            return rounded.ToString();
        }

        private string FrameDuration(double rawFps)
        {
            int rounded = Math.Max(1, (int)Math.Round(rawFps));
            double ntscRate = rounded * 1000.0 / 1001.0;
            if (Math.Abs(rawFps - ntscRate) < Math.Abs(rawFps - rounded))
            {
                return $"1001/{rounded * 1000}s";
            }
            return $"1/{rounded}s";
        }

        private string ColorString(TextStyle.RGBA color)
        {
            return $"{FormatNumber(color.R)} {FormatNumber(color.G)} {FormatNumber(color.B)} {FormatNumber(color.A)}";
        }

        private List<(string, string)> TextStyleAttributes(TextStyle style)
        {
            var family = FontFamilyFallback(style.FontName);
            var face = GetFontFaceFallback(style.IsBold, style.IsItalic);
            var fontSize = style.FontSize * style.FontScale;
            var attrs = new List<(string, string)>
            {
                ("font", family),
                ("fontFace", face),
                ("fontSize", FormatNumber(fontSize)),
                ("fontColor", ColorString(style.Color)),
                ("alignment", style.Alignment.ToString().ToLowerInvariant()),
            };
            if (style.Border.Enabled)
            {
                attrs.Add(("strokeColor", ColorString(style.Border.Color)));
                attrs.Add(("strokeWidth", FormatNumber(Math.Abs(-4.0) / 100 * fontSize)));
            }
            return attrs;
        }

        private List<FcpXmlNode> TitleTransformNodes(Transform transform)
        {
            return new List<FcpXmlNode>
            {
                new FcpXmlNode("adjust-conform", new List<(string, string)> { ("type", "fit") }),
                new FcpXmlNode("adjust-transform", new List<(string, string)>
                {
                    ("scale", "1 1"),
                    ("anchor", "0 0"),
                    ("position", PositionValue(transform, (1.0, 1.0))),
                })
            };
        }

        private string PositionValue(Transform transform, (double W, double H)? fit = null)
        {
            var f = fit ?? (1.0, 1.0);
            double unit = _seqHeight / 100.0;
            double x = (transform.CenterX - 0.5) * _seqWidth / unit / f.W;
            double y = (0.5 - transform.CenterY) * _seqHeight / unit / f.H;
            return $"{FormatNumber(x)} {FormatNumber(y)}";
        }

        private (double W, double H) FitFractions(Clip clip)
        {
            var entry = _resolver.Entry(clip.MediaRef);
            if (entry == null || entry.SourceWidth is not int sw || entry.SourceHeight is not int sh || sw <= 0 || sh <= 0)
            {
                return (1.0, 1.0);
            }
            double sourceAspect = (double)sw / sh;
            double frameAspect = (double)_seqWidth / _seqHeight;
            return sourceAspect >= frameAspect
                ? (1.0, frameAspect / sourceAspect)
                : (sourceAspect / frameAspect, 1.0);
        }

        private string GetFontFaceFallback(bool isBold, bool isItalic)
        {
            return (isBold, isItalic) switch
            {
                (true, true) => "Bold Italic",
                (true, false) => "Bold",
                (false, true) => "Italic",
                (false, false) => "Regular"
            };
        }

        private string FontFamilyFallback(string fontName)
        {
            var idx = fontName.IndexOf('-');
            return idx >= 0 ? fontName.Substring(0, idx) : fontName;
        }

        private string FileName(MediaResource resource)
        {
            return Path.GetFileName(resource.Url.LocalPath);
        }

        private string MediaSrc(MediaResource resource)
        {
            var absoluteUri = resource.Url.AbsoluteUri;
            var sb = new StringBuilder();
            foreach (var ch in absoluteUri)
            {
                if ("'!$&()*+,;=".Contains(ch))
                {
                    sb.Append($"%{(int)ch:X2}");
                }
                else
                {
                    sb.Append(ch);
                }
            }
            return sb.ToString();
        }

        private FcpXmlNode? FormatNode(MediaResource resource)
        {
            if (resource.FormatId == null) return null;
            var width = resource.Entry.SourceWidth ?? _rootWidth;
            var height = resource.Entry.SourceHeight ?? _rootHeight;
            var rawFPS = resource.Entry.SourceFps ?? _rootFps;
            return new FcpXmlNode("format", new List<(string, string)>
            {
                ("id", resource.FormatId),
                ("name", VideoFormatName(width, height, rawFPS)),
                ("frameDuration", FrameDuration(rawFPS)),
                ("width", width.ToString()),
                ("height", height.ToString()),
                ("colorSpace", "1-1-1 (Rec. 709)"),
            });
        }

        private FcpXmlNode AssetNode(MediaResource resource)
        {
            var attrs = new List<(string, string)>
            {
                ("id", resource.AssetId),
                ("name", FileName(resource)),
                ("start", resource.Origin.ToString()),
                ("duration", resource.Duration.ToString()),
            };
            if (resource.HasVideo)
            {
                attrs.Add(("hasVideo", "1"));
                attrs.Add(("videoSources", "1"));
                if (resource.FormatId != null)
                {
                    attrs.Add(("format", resource.FormatId));
                }
            }
            if (resource.HasAudio)
            {
                attrs.Add(("hasAudio", "1"));
                attrs.Add(("audioSources", "1"));
                attrs.Add(("audioChannels", "2"));
                attrs.Add(("audioRate", "48000"));
            }
            return new FcpXmlNode("asset", attrs, new List<FcpXmlNode>
            {
                new FcpXmlNode("media-rep", new List<(string, string)>
                {
                    ("kind", "original-media"),
                    ("src", MediaSrc(resource)),
                })
            });
        }

        private string FormatNumber(double value)
        {
            double rounded = Math.Round(value, 4);
            if (rounded == Math.Truncate(rounded))
            {
                return ((int)rounded).ToString();
            }
            var s = rounded.ToString("F4", System.Globalization.CultureInfo.InvariantCulture);
            while (s.Length > 0 && s[^1] == '0')
            {
                s = s.Substring(0, s.Length - 1);
            }
            if (s.Length > 0 && s[^1] == '.')
            {
                s = s.Substring(0, s.Length - 1);
            }
            return s;
        }
    }
}
