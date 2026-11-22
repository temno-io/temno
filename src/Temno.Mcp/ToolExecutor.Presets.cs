using System.Text.Json;
using System.Text.Json.Nodes;
using Temno.Core.Agent.Skills;
using Temno.Core.Audio;
using Temno.Core.IO;
using Temno.Core.Models;
using Temno.Core.Project;
using Temno.Core.Session;
using Temno.Media.Audio;

namespace Temno.Mcp;

/// <summary>WP-96: presets, validate, duck/denoise, crop, feedback, skills, organize.</summary>
public static partial class ToolExecutor
{
    // --- validate_timeline ---

    private static object ValidateTimeline(JsonObject args)
    {
        var session = GetSession();
        var timeline = session.ActiveTimeline;
        var issues = new List<object>();
        var warnings = new List<object>();

        if (timeline.Tracks.Count == 0)
            issues.Add(Issue("error", "timeline", "Timeline has no tracks."));

        var anyClip = timeline.Tracks.SelectMany(t => t.Clips).Any();
        if (!anyClip)
            warnings.Add(Issue("warning", "timeline", "Timeline has no clips."));

        var entries = session.Manifest.Entries.ToDictionary(e => e.Id, StringComparer.Ordinal);
        foreach (var track in timeline.Tracks)
        {
            foreach (var clip in track.Clips)
            {
                if (clip.DurationFrames <= 0)
                    issues.Add(Issue("error", clip.Id, "Clip has non-positive duration."));

                if (clip.MediaType == ClipType.text)
                {
                    if (string.IsNullOrWhiteSpace(clip.TextContent))
                        warnings.Add(Issue("warning", clip.Id, "Text clip has empty content."));
                    continue;
                }

                if (string.IsNullOrEmpty(clip.MediaRef))
                {
                    issues.Add(Issue("error", clip.Id, "Clip has empty mediaRef."));
                    continue;
                }

                if (!entries.TryGetValue(clip.MediaRef, out var entry))
                {
                    issues.Add(Issue("error", clip.Id, $"Media asset not found: {clip.MediaRef}"));
                    continue;
                }

                var path = ResolveAssetPath(session, entry);
                if (path is null || !File.Exists(path))
                    issues.Add(Issue("error", clip.Id, $"Offline media: {entry.Name}"));
            }
        }

        if (args["exportPresetId"]?.GetValue<string>() is { Length: > 0 } presetId)
        {
            var preset = PresetStore.Shared.GetExportPreset(presetId);
            if (preset is null)
                warnings.Add(Issue("warning", "exportPreset", $"Export preset not found: {presetId}"));
        }

        var ok = issues.Count == 0;
        return new
        {
            status = ok ? "ok" : "failed",
            issueCount = issues.Count,
            warningCount = warnings.Count,
            issues,
            warnings,
            totalFrames = timeline.TotalFrames,
            fps = timeline.Fps,
            width = timeline.Width,
            height = timeline.Height,
        };
    }

    private static object Issue(string severity, string subject, string message)
        => new { severity, subject, message };

    // --- suggest_crop ---

    private static object SuggestCrop(JsonObject args)
    {
        var session = GetSession();
        var clipId = args["clipId"]?.GetValue<string>()
            ?? throw new InvalidOperationException("suggest_crop: clipId is required");
        var clip = FindClip(session, clipId)
            ?? throw new InvalidOperationException($"Clip not found: {clipId}");

        var targetAspect = args["targetAspect"]?.GetValue<double>()
            ?? (session.ActiveTimeline.Height > 0
                ? session.ActiveTimeline.Width / (double)session.ActiveTimeline.Height
                : 16.0 / 9.0);
        if (targetAspect <= 0)
            throw new InvalidOperationException("suggest_crop: targetAspect must be > 0");

        var entry = session.Manifest.Entries.FirstOrDefault(e => e.Id == clip.MediaRef);
        double sourceAspect = 16.0 / 9.0;
        if (entry?.SourceWidth is > 0 && entry.SourceHeight is > 0)
            sourceAspect = entry.SourceWidth.Value / (double)entry.SourceHeight.Value;

        // Center crop: crop the longer dimension so remaining rectangle matches targetAspect.
        var crop = new Crop();
        if (sourceAspect > targetAspect)
        {
            // Source is wider — crop left/right.
            var keep = targetAspect / sourceAspect;
            var side = (1.0 - keep) / 2.0;
            crop.Left = side;
            crop.Right = side;
        }
        else if (sourceAspect < targetAspect)
        {
            var keep = sourceAspect / targetAspect;
            var side = (1.0 - keep) / 2.0;
            crop.Top = side;
            crop.Bottom = side;
        }

        var apply = args["apply"]?.GetValue<bool>() == true;
        if (apply)
        {
            var snapshot = TakeTimelineSnapshot(session.ActiveTimeline);
            var result = EditCommands.SetClipProperties(
                session, [clipId], null, null, null, null, null, null, null, null, crop: crop);
            if (!result.Ok) throw new InvalidOperationException(result.Error ?? "suggest_crop apply failed");
            return GetMutationResult(session, snapshot, touched: [clipId], extra: new Dictionary<string, object>
            {
                ["crop"] = new { left = crop.Left, top = crop.Top, right = crop.Right, bottom = crop.Bottom },
                ["targetAspect"] = targetAspect,
                ["applied"] = true,
            });
        }

        return new
        {
            status = "success",
            clipId,
            targetAspect,
            applied = false,
            crop = new { left = crop.Left, top = crop.Top, right = crop.Right, bottom = crop.Bottom },
            note = "Pass apply:true to write crop on the clip.",
        };
    }

    // --- denoise_audio ---

    private static object DenoiseAudio(JsonObject args)
    {
        var session = GetSession();
        var clipIds = ParseStringArray(args["clipIds"]);
        if (clipIds.Count == 0)
            throw new InvalidOperationException("denoise_audio: clipIds is required");

        var enabled = args["enabled"]?.GetValue<bool>() ?? true;
        var strength = args["strength"]?.GetValue<double>() ?? Clip.DefaultDenoiseAmount;
        strength = Math.Clamp(strength, 0, 1);

        var snapshot = TakeTimelineSnapshot(session.ActiveTimeline);
        var touched = new List<string>();
        var notes = enabled
            ? new List<string>
            {
                "Denoise is applied by the shared Windows decoded-audio path for preview and export. " +
                "It uses deterministic 48 kHz PCM noise-floor reduction, not the optional macOS speech model.",
            }
            : new List<string> { "Denoise is disabled; preview and export use dry decoded audio." };

        session.RecordBeforeEdit();
        try
        {
            foreach (var id in clipIds)
            {
                var clip = FindClip(session, id)
                    ?? throw new InvalidOperationException($"Clip not found: {id}");
                if (clip.MediaType != ClipType.audio)
                    throw new InvalidOperationException(
                        $"Clip {id} is a {clip.MediaType} clip; denoise_audio needs an audio clip.");

                var stack = clip.Effects ?? [];
                var existing = stack.FirstOrDefault(e => e.Type == Clip.DenoiseEffectType);
                if (enabled)
                {
                    if (existing is null)
                    {
                        stack.Add(new Effect
                        {
                            Type = Clip.DenoiseEffectType,
                            Enabled = true,
                            Params = new Dictionary<string, EffectParam>
                            {
                                ["amount"] = new EffectParam { Value = strength },
                            },
                        });
                        clip.Effects = stack;
                    }
                    else
                    {
                        existing.Enabled = true;
                        existing.Params["amount"] = new EffectParam { Value = strength };
                    }
                }
                else if (existing is not null)
                {
                    existing.Enabled = false;
                }

                touched.Add(id);
            }

            session.MarkDirty();
        }
        catch
        {
            session.RollbackLastRecord();
            throw;
        }

        return GetMutationResult(session, snapshot, touched: touched, notes: notes, extra: new Dictionary<string, object>
        {
            ["enabled"] = enabled,
            ["strength"] = strength,
            ["clips"] = touched.Count,
        });
    }

    // --- duck_audio ---

    private static object DuckAudio(JsonObject args)
    {
        var session = GetSession();
        var timeline = session.ActiveTimeline;
        var voiceTrack = args["voiceTrack"]?.GetValue<int>()
            ?? throw new InvalidOperationException("duck_audio: voiceTrack is required");
        var bgTrack = args["bgTrack"]?.GetValue<int>()
            ?? throw new InvalidOperationException("duck_audio: bgTrack is required");
        var reductionDb = Num(args["reductionDb"])
            ?? throw new InvalidOperationException("duck_audio: reductionDb is required");
        var attack = Num(args["attack"]) ?? AudioDuckEnvelope.DefaultAttackSeconds;
        var release = Num(args["release"]) ?? AudioDuckEnvelope.DefaultReleaseSeconds;
        var requestedSource = args["source"]?.GetValue<string>() ?? "auto";

        if (voiceTrack < 0 || voiceTrack >= timeline.Tracks.Count)
            throw new InvalidOperationException($"duck_audio: voiceTrack {voiceTrack} out of range");
        if (bgTrack < 0 || bgTrack >= timeline.Tracks.Count)
            throw new InvalidOperationException($"duck_audio: bgTrack {bgTrack} out of range");
        if (voiceTrack == bgTrack)
            throw new InvalidOperationException("duck_audio: voiceTrack and bgTrack must differ");

        if (!string.Equals(requestedSource, "auto", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"duck_audio: source '{requestedSource}' is unavailable on Windows; only 'auto' " +
                "(audible voice-clip coverage) is supported.");

        AudioDuckPlan plan;
        try
        {
            plan = AudioDuckEnvelope.CreatePlan(timeline, voiceTrack, bgTrack, reductionDb, attack, release);
        }
        catch (Exception ex) when (ex is ArgumentException or ArgumentOutOfRangeException or InvalidOperationException)
        {
            throw new InvalidOperationException($"duck_audio: {ex.Message}");
        }

        var bgClips = timeline.Tracks[bgTrack].Clips.Where(clip => clip.DurationFrames > 0).ToList();
        if (bgClips.Count == 0)
            throw new InvalidOperationException("duck_audio: background track has no clips");

        var snapshot = TakeTimelineSnapshot(timeline);
        var ids = new List<string>();
        session.RecordBeforeEdit();
        try
        {
            foreach (var clip in bgClips)
            {
                if (AudioDuckEnvelope.ApplyToClip(clip, plan))
                    ids.Add(clip.Id);
            }

            if (ids.Count == 0)
                throw new InvalidOperationException("duck_audio: no background clip overlaps the voice coverage.");

            session.MarkDirty();
        }
        catch
        {
            session.RollbackLastRecord();
            throw;
        }

        var notes = new List<string>
        {
            "Ducking wrote Mac-compatible volumeTrack dB keyframes from audible voice-clip coverage.",
            $"reductionDb={plan.ReductionDb:0.###}, attack={attack:0.###}s, release={release:0.###}s",
        };
        return GetMutationResult(session, snapshot, touched: ids, notes: notes, extra: new Dictionary<string, object>
        {
            ["voiceTrack"] = voiceTrack,
            ["bgTrack"] = bgTrack,
            ["reductionDb"] = plan.ReductionDb,
            ["attack"] = attack,
            ["release"] = release,
            ["source"] = "clip_coverage",
            ["voiceIntervals"] = plan.Intervals.Count,
            ["clipsDucked"] = ids.Count,
        });
    }

    // --- organize_media ---

    private static object OrganizeMedia(JsonObject args)
    {
        var session = GetSession();
        var snapshot = TakeTimelineSnapshot(session.ActiveTimeline);
        session.RecordBeforeEdit();
        try
        {
            var created = new List<string>();
            var moved = 0;
            var renamed = 0;
            var deleted = 0;

            if (args["createFolders"] is JsonArray folders)
            {
                foreach (var n in folders)
                {
                    var name = n?.GetValue<string>();
                    if (string.IsNullOrWhiteSpace(name)) continue;
                    var folder = new MediaFolder { Name = name.Trim() };
                    session.Manifest.Folders.Add(folder);
                    created.Add(folder.Id);
                }
            }

            if (args["moves"] is JsonArray moves)
            {
                foreach (var node in moves.OfType<JsonObject>())
                {
                    var into = node["into"]?.GetValue<string>();
                    string? folderId = null;
                    if (!string.IsNullOrWhiteSpace(into))
                    {
                        var folder = session.Manifest.Folders.FirstOrDefault(f =>
                            f.Id == into || string.Equals(f.Name, into, StringComparison.OrdinalIgnoreCase));
                        if (folder is null)
                        {
                            folder = new MediaFolder { Name = into.Trim() };
                            session.Manifest.Folders.Add(folder);
                            created.Add(folder.Id);
                        }
                        folderId = folder.Id;
                    }

                    foreach (var item in ParseStringArray(node["items"]))
                    {
                        var entry = session.Manifest.Entries.FirstOrDefault(e => e.Id == item);
                        if (entry is null) continue;
                        entry.FolderId = folderId;
                        moved++;
                    }
                }
            }

            if (args["renames"] is JsonArray renames)
            {
                foreach (var node in renames.OfType<JsonObject>())
                {
                    var item = node["item"]?.GetValue<string>();
                    var name = node["name"]?.GetValue<string>();
                    if (string.IsNullOrWhiteSpace(item) || string.IsNullOrWhiteSpace(name)) continue;
                    var entry = session.Manifest.Entries.FirstOrDefault(e => e.Id == item);
                    if (entry is not null)
                    {
                        entry.Name = name.Trim();
                        renamed++;
                        continue;
                    }
                    var folder = session.Manifest.Folders.FirstOrDefault(f => f.Id == item);
                    if (folder is not null)
                    {
                        folder.Name = name.Trim();
                        renamed++;
                    }
                }
            }

            if (args["deletes"] is JsonArray deletes)
            {
                foreach (var n in deletes)
                {
                    var id = n?.GetValue<string>();
                    if (string.IsNullOrWhiteSpace(id)) continue;
                    var removed = session.Manifest.Entries.RemoveAll(e => e.Id == id);
                    if (removed > 0) { deleted += removed; continue; }
                    deleted += session.Manifest.Folders.RemoveAll(f => f.Id == id);
                }
            }

            session.MarkDirty();
            return GetMutationResult(session, snapshot, extra: new Dictionary<string, object>
            {
                ["foldersCreated"] = created.Count,
                ["itemsMoved"] = moved,
                ["itemsRenamed"] = renamed,
                ["itemsDeleted"] = deleted,
            });
        }
        catch
        {
            session.RollbackLastRecord();
            throw;
        }
    }

    // --- text style presets ---

    private static object ListTextStyles(JsonObject args)
    {
        PresetStore.Shared.LoadAll();
        return new
        {
            styles = PresetStore.Shared.TextStyles.Select(s => new
            {
                id = s.Id,
                name = s.Name,
                fontName = s.Style.FontName,
                fontSize = s.Style.FontSize,
            }).ToList(),
        };
    }

    private static object SaveTextStyle(JsonObject args)
    {
        var name = args["name"]?.GetValue<string>()
            ?? throw new InvalidOperationException("save_text_style: name is required");

        TextStyle style;
        var clipIds = ParseStringArray(args["clipIds"]);
        var captionGroupId = args["captionGroupId"]?.GetValue<string>();
        if (clipIds.Count > 0 || !string.IsNullOrWhiteSpace(captionGroupId))
        {
            var session = GetSession();
            var clip = ResolveStyleSourceClip(session, clipIds, captionGroupId);
            style = ReadClipTextStyle(clip) ?? new TextStyle();
            ParseTextStylePatch(args, "save_text_style").ApplyTo(style);
        }
        else
        {
            style = new TextStyle();
            ParseTextStylePatch(args, "save_text_style").ApplyTo(style);
        }

        var preset = PresetStore.Shared.SaveTextStyle(name, style);
        return new { status = "success", id = preset.Id, name = preset.Name };
    }

    private static object RemoveTextStyle(JsonObject args)
    {
        var styleId = args["styleId"]?.GetValue<string>()
            ?? throw new InvalidOperationException("remove_text_style: styleId is required");
        var ok = PresetStore.Shared.RemoveTextStyle(styleId);
        if (!ok) throw new InvalidOperationException($"Text style not found: {styleId}");
        return new { status = "success", removed = styleId };
    }

    private static object ApplyStyle(JsonObject args)
    {
        var session = GetSession();
        var styleId = args["styleId"]?.GetValue<string>()
            ?? throw new InvalidOperationException("apply_style: styleId is required");
        var preset = PresetStore.Shared.GetTextStyle(styleId)
            ?? throw new InvalidOperationException($"Text style not found: {styleId}");

        var patch = new TextPatch
        {
            Style = new TextStylePatch
            {
                FontName = preset.Style.FontName,
                FontSize = preset.Style.FontSize,
                IsBold = preset.Style.IsBold,
                IsItalic = preset.Style.IsItalic,
                Color = preset.Style.Color,
                Alignment = preset.Style.Alignment,
                BorderColor = preset.Style.Border.Enabled ? preset.Style.Border.Color : null,
                BackgroundColor = preset.Style.Background.Enabled ? preset.Style.Background.Color : null,
                BorderEnabled = preset.Style.Border.Enabled,
                BackgroundEnabled = preset.Style.Background.Enabled,
                ShadowEnabled = preset.Style.Shadow.Enabled,
                ShadowBlur = preset.Style.Shadow.Blur,
                ShadowOffsetX = preset.Style.Shadow.OffsetX,
                ShadowOffsetY = preset.Style.Shadow.OffsetY,
            },
        };

        var snapshot = TakeTimelineSnapshot(session.ActiveTimeline);
        var result = TextClipCommands.UpdateText(
            session,
            ParseStringArray(args["clipIds"]),
            args["captionGroupId"]?.GetValue<string>(),
            patch,
            out var touched,
            out var notes);
        if (!result.Ok) throw new InvalidOperationException(result.Error ?? "apply_style failed");
        return GetMutationResult(session, snapshot, touched: touched, notes: notes, extra: new Dictionary<string, object>
        {
            ["styleId"] = preset.Id,
            ["styleName"] = preset.Name,
        });
    }

    // --- export presets ---

    private static object ListExportPresets(JsonObject args)
    {
        PresetStore.Shared.LoadAll();
        return new
        {
            presets = PresetStore.Shared.ExportPresets.Select(p => new
            {
                id = p.Id,
                name = p.Name,
                codec = p.Codec.ToString(),
                resolution = p.Resolution.ToString(),
                bitrateMbps = p.BitrateMbps,
                maxRenderQuality = p.MaxRenderQuality,
                maxBitDepth = p.MaxBitDepth,
            }).ToList(),
        };
    }

    private static object SaveExportPreset(JsonObject args)
    {
        var name = args["name"]?.GetValue<string>()
            ?? throw new InvalidOperationException("save_export_preset: name is required");
        var codec = ParseVideoCodec(args["codec"]?.GetValue<string>());
        var resolution = ParseExportResolution(args["resolution"]?.GetValue<string>());
        var bitrate = args["bitrateMbps"]?.GetValue<double>();
        var maxQ = args["maxRenderQuality"]?.GetValue<bool>() ?? false;
        var maxBit = args["maxBitDepth"]?.GetValue<bool>() ?? false;
        var preset = PresetStore.Shared.SaveExportPreset(name, codec, resolution, bitrate, maxQ, maxBit);
        return new { status = "success", id = preset.Id, name = preset.Name };
    }

    private static object RemoveExportPreset(JsonObject args)
    {
        var presetId = args["presetId"]?.GetValue<string>()
            ?? throw new InvalidOperationException("remove_export_preset: presetId is required");
        if (!PresetStore.Shared.RemoveExportPreset(presetId))
            throw new InvalidOperationException($"Export preset not found: {presetId}");
        return new { status = "success", removed = presetId };
    }

    // --- project templates ---

    private static object ListProjectTemplates(JsonObject args)
    {
        return new
        {
            templates = ProjectTemplateStore.Shared.List().Select(t => new
            {
                name = t.Name,
                path = t.Path,
                modifiedUtc = t.ModifiedUtc,
            }).ToList(),
        };
    }

    private static object SaveProjectTemplate(JsonObject args)
    {
        var session = GetSession();
        var name = args["name"]?.GetValue<string>()
            ?? throw new InvalidOperationException("save_project_template: name is required");
        // Deep-clone via encode/decode so the template freezes current state.
        var pf = ProjectFile.Decode(session.ProjectFile.Encode());
        var mf = MediaManifest.Decode(session.Manifest.Encode());
        var info = ProjectTemplateStore.Shared.Save(name, pf, mf);
        return new { status = "success", name = info.Name, path = info.Path };
    }

    private static object RemoveProjectTemplate(JsonObject args)
    {
        var name = args["name"]?.GetValue<string>()
            ?? throw new InvalidOperationException("remove_project_template: name is required");
        if (!ProjectTemplateStore.Shared.Remove(name))
            throw new InvalidOperationException($"Project template not found: {name}");
        return new { status = "success", removed = name };
    }

    // --- generate_google (alias / honest routing) ---

    private static object GenerateGoogle(JsonObject args)
    {
        var type = args["type"]?.GetValue<string>()?.ToLowerInvariant()
            ?? throw new InvalidOperationException("generate_google: type is required");
        // Reuse generation clients when configured; otherwise clear error.
        return type switch
        {
            "image" => GenerateImage(args),
            "video" => GenerateVideo(args),
            _ => throw new InvalidOperationException("generate_google: type must be image or video"),
        };
    }

    // --- feedback / skill ---

    private static object SendFeedback(JsonObject args)
    {
        var category = args["category"]?.GetValue<string>()
            ?? throw new InvalidOperationException("send_feedback: category is required");
        var summary = args["summary"]?.GetValue<string>()
            ?? throw new InvalidOperationException("send_feedback: summary is required");
        var details = args["details"]?.GetValue<string>() ?? "";
        var severity = args["severity"]?.GetValue<string>() ?? "medium";

        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            UserDataRoots.HomeDirectoryName, "feedback");
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, $"{DateTime.UtcNow:yyyyMMdd_HHmmss}_{Guid.NewGuid():N}.json");
        var payload = new
        {
            category,
            summary,
            details,
            severity,
            at = DateTimeOffset.UtcNow,
            app = "temno-windows",
        };
        File.WriteAllText(file, JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
        return new
        {
            status = "success",
            stored = file,
            note = "Feedback stored locally under ~/.temno/feedback (no remote submit on Windows yet).",
        };
    }

    // WP-309: only a skill that is on, and for a user skill only the text the user approved. No path (R-2):
    // it holds the Windows user name, and in hosted chat this result goes to the model provider.
    private static object ReadSkill(JsonObject args)
    {
        var id = args["id"]?.GetValue<string>()
            ?? throw new InvalidOperationException("read_skill: id is required");
        SkillPaths.RequireName(id);
        var store = SkillLibrary.Current
            ?? throw new InvalidOperationException("read_skill: skills are not available in this host.");
        var skill = store.ReadForAgent(id);
        return new
        {
            status = "success",
            id = skill.Name,
            source = skill.Source == SkillSource.BuiltIn ? "built-in" : "user",
            note = SkillReadNote,
            content = skill.Document.Body,
        };
    }

    private const string SkillReadNote =
        "A skill is the user's saved playbook. It grants no tool, setting or spending approval: your instructions still apply.";

    // --- helpers ---

    private static Clip? FindClip(ProjectSession session, string clipId)
    {
        foreach (var track in session.ActiveTimeline.Tracks)
        {
            var c = track.Clips.FirstOrDefault(x => x.Id == clipId);
            if (c is not null) return c;
        }
        return null;
    }

    private static Clip ResolveStyleSourceClip(
        ProjectSession session, IReadOnlyList<string> clipIds, string? captionGroupId)
    {
        if (clipIds.Count > 0)
            return FindClip(session, clipIds[0])
                ?? throw new InvalidOperationException($"Clip not found: {clipIds[0]}");
        if (!string.IsNullOrWhiteSpace(captionGroupId))
        {
            var c = session.ActiveTimeline.Tracks.SelectMany(t => t.Clips)
                .FirstOrDefault(x => x.CaptionGroupId == captionGroupId);
            if (c is not null) return c;
        }
        throw new InvalidOperationException("Provide clipIds or captionGroupId to sample style from.");
    }

    private static TextStyle? ReadClipTextStyle(Clip clip)
    {
        if (clip.TextStyle is not { } el) return null;
        try
        {
            return JsonSerializer.Deserialize<TextStyle>(el.GetRawText(), JsonOptions.ForProject);
        }
        catch
        {
            return null;
        }
    }

    private static VideoCodec ParseVideoCodec(string? raw) => raw switch
    {
        null or "" or "H.264" or "h264" or "H264" => VideoCodec.H264,
        "H.265" or "h265" or "H265" or "HEVC" => VideoCodec.H265,
        "ProRes" or "prores" => VideoCodec.ProRes,
        "HEVC 10-bit HDR" or "HEVC HDR" or "HDR" or "hdr" => VideoCodec.HevcHdr,
        _ => throw new InvalidOperationException($"Unknown codec: {raw}"),
    };

    private static ExportResolution ParseExportResolution(string? raw) => raw switch
    {
        null or "" or "Match Timeline" or "match" => ExportResolution.MatchTimeline,
        "720p" => ExportResolution.R720p,
        "1080p" => ExportResolution.R1080p,
        "2K" or "1440p" => ExportResolution.R1440p,
        "4K" or "2160p" => ExportResolution.R4k,
        _ => throw new InvalidOperationException($"Unknown resolution: {raw}"),
    };
}

/// <summary>
/// Resolves a skill id to a <c>SKILL.md</c> under one of the skill roots (WP-309: <c>~/.temno/skills</c>, then the
/// app's <c>skills</c> folder). Identifiers that leave a root, including through a reparse point, are rejected
/// (WP-208). The rules live in <see cref="SkillPaths"/>; this keeps the MCP layer's name for them.
/// </summary>
public static class SkillPathResolver
{
    public static IReadOnlyList<string> DefaultRoots() =>
        SkillLibrary.Current?.Roots ?? [SkillStore.DefaultUserRoot, SkillStore.DefaultBuiltInRoot];

    public static string Require(string id, IReadOnlyList<string> roots) => SkillPaths.Require(id, roots);
}
