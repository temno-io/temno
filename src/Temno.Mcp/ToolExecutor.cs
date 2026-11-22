using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Temno.Core.Account;
using Temno.Core.Captions;
using Temno.Core.Editing;
using Temno.Core.Export;
using Temno.Core.Generation;
using Temno.Core.IO;
using Temno.Core.Models;
using Temno.Core.Project;
using Temno.Core.Session;
using Temno.Core.Transcription;
using Temno.Generation;
using Temno.Media;
using Temno.Media.Audio;
using Temno.Media.Compositing;
using Temno.Media.Effects;
using Temno.Media.Export;
using Temno.Media.Hardware;
using Temno.Media.Playback;
using Temno.Media.Search;
using Temno.Media.Transcription;

namespace Temno.Mcp;

public static partial class ToolExecutor
{
    private const long MaxLottieJsonBytes = 100L * 1024 * 1024;
    private static readonly object _sessionLock = new();
    private static readonly object _genLock = new();
    private static readonly object _visualSearchLock = new();
    private static readonly ExportJobQueue _exportQueue = new();

    /// <summary>The shared background export queue (Mac: ExportQueue.shared). Exposed for the app's
    /// title-bar activity indicator (WP-74).</summary>
    public static ExportJobQueue ExportQueue => _exportQueue;
    private static GenerationService? _generationService;
    private static string? _generationFingerprint;
    private static readonly Dictionary<string, CancellationTokenSource> _generationCancellations = new(StringComparer.Ordinal);
    private static IVisualSearchService? _visualSearchService;

    public const string GenerationUnconfiguredMessage =
        "Set TEMNO_API_BASE_URL, and TEMNO_API_KEY if your endpoint needs one, in Settings or the environment (or TEMNO_USE_FAKE_GENERATION=true for demos). Or sign in and set TEMNO_GATEWAY_URL for hosted jobs.";

    public const string GenerationEndpointMissingMessage =
        "A generation API key is set, but no endpoint URL. Add the endpoint in Settings → Generation or TEMNO_API_BASE_URL, or clear the key.";

    public const string FakeGenerationNote =
        "FakeGenerationClient (TEMNO_USE_FAKE_GENERATION=true); not production media.";

    public const string DemoGenerationStatus = "demo";

    public static GenerationService? GenerationServiceOverride { get; set; }
    public static IVisualSearchService? VisualSearchServiceOverride { get; set; }
    public static ITranscriptionProvider? TranscriptionProviderOverride { get; set; }

    /// <summary>Clears the cached generation client so tests can change env vars between calls.</summary>
    public static void ResetGenerationService()
    {
        lock (_genLock)
        {
            CancelAllGenerationsLocked();
            _generationService = null;
            _generationFingerprint = null;
        }
        ResetImageCatalog();
        ResetImageQuotes();
        ImageClientOverride = null;
        ResetMediaCatalog();
        ResetMediaQuotes();
        MediaClientOverride = null;
        ResetHostedGenerationsForTests();
    }

    private static ITranscriptionProvider GetTranscriptionProvider()
    {
        if (TranscriptionProviderOverride is not null)
            return TranscriptionProviderOverride;
        return TranscriptionServices.Resolve();
    }

    /// <summary>
    /// The tools <see cref="Execute"/> actually handles. tools/list is gated to this set so the agent
    /// never discovers advertised-but-unimplemented tools that would error mid-task. Add a name here
    /// only when its switch case is implemented (not a stub).
    /// </summary>
    public static readonly IReadOnlySet<string> ImplementedTools = new HashSet<string>(StringComparer.Ordinal)
    {
        // Projects
        "get_projects", "open_project", "new_project", "close_project", "manage_project",
        // Timelines
        "get_timeline", "create_timeline", "set_active_timeline", "set_project_settings", "export_project",
        "manage_exports", "inspect_timeline",
        // Media
        "get_media", "inspect_media", "import_media", "search_media",
        // Clips / tracks
        "manage_tracks", "add_clips", "insert_clips", "move_clips", "remove_clips", "split_clips",
        "restore_linked_audio",
        "ripple_delete_ranges", "set_clip_properties", "undo", "redo",
        // Markers
        "add_marker", "list_markers", "update_marker", "remove_marker",
        // Color / effects / keyframes / transitions
        "apply_color", "apply_effect", "set_keyframes",
        "add_transition", "list_transitions", "remove_transition",
        // Text
        "add_texts", "update_text",
        // Transcript
        "get_transcript", "remove_words", "add_captions",
        // Layout / color scopes
        "apply_layout", "inspect_color",
        // Audio analysis
        "detect_beats", "remove_silence", "sync_clips",
        // Generation
        "list_models", "generate_video", "generate_image", "generate_audio", "upscale_media",
        "generate_google", "quote_image", "edit_image", "quote_media", "resume_generation",
        // WP-96 presets / housekeeping
        "validate_timeline", "suggest_crop", "denoise_audio", "duck_audio", "organize_media",
        "apply_style", "save_text_style", "list_text_styles", "remove_text_style",
        "save_export_preset", "list_export_presets", "remove_export_preset",
        "save_project_template", "list_project_templates", "remove_project_template",
        "send_feedback", "read_skill",
        // Multicam (WP-93)
        "manage_multicam", "change_cam", "get_multicam",
    };

    private static bool IsFakeGenerationEnabled()
        => string.Equals(
            EnvCompat.Get("TEMNO_USE_FAKE_GENERATION"),
            "true",
            StringComparison.OrdinalIgnoreCase);

    // A key with no endpoint is refused: never sent to a default host, never swapped for hosted jobs.
    private static bool IsEndpointMissing(string apiKey, string baseUrl)
        => !IsFakeGenerationEnabled() && string.IsNullOrWhiteSpace(baseUrl) && !string.IsNullOrWhiteSpace(apiKey);

    private static (bool UseFake, bool UseHttp, bool UseHosted, string Fingerprint) ReadGenerationConfig()
    {
        var prefs = UserPreferences.Load();
        var apiKey = prefs.ResolveGenerationApiKey();
        var baseUrl = prefs.ResolveGenerationBaseUrl();
        var gateway = HostedEndpoints.ResolveGatewayUrl();
        var useFake = IsFakeGenerationEnabled();
        var useHttp = !useFake && !string.IsNullOrWhiteSpace(baseUrl);
        var endpointMissing = IsEndpointMissing(apiKey, baseUrl);
        var useHosted = !useFake && !useHttp && !endpointMissing
            && HostedRuntime.IsSignedIn && !string.IsNullOrWhiteSpace(gateway);
        var mode = useFake ? "fake" : useHttp ? "http" : endpointMissing ? "no-endpoint" : useHosted ? "hosted" : "none";
        var user = HostedRuntime.Session?.UserId ?? "";
        return (useFake, useHttp, useHosted, $"{mode}\u001f{apiKey}\u001f{baseUrl}\u001f{gateway}\u001f{user}");
    }

    private static GenerationService? TryGetGenerationService()
    {
        if (GenerationServiceOverride != null)
            return GenerationServiceOverride;

        lock (_genLock)
        {
            var (useFake, useHttp, useHosted, fingerprint) = ReadGenerationConfig();
            if (_generationFingerprint != fingerprint)
            {
                _generationService = null;
                _generationFingerprint = fingerprint;
            }

            if (_generationService != null)
                return _generationService;

            if (useFake)
            {
                var httpClient = new HttpClient(new FakeHttpMessageHandler());
                _generationService = new GenerationService(
                    new FakeGenerationClient(),
                    httpClient,
                    hostResolver: (_, _) => Task.FromResult(
                        new[] { System.Net.IPAddress.Parse("93.184.216.34") }));
                return _generationService;
            }

            if (useHttp)
            {
                var prefs = UserPreferences.Load();
                var apiKey = prefs.ResolveGenerationApiKey();
                var baseUrl = prefs.ResolveGenerationBaseUrl();
                _generationService = new GenerationService(new HttpGenerationClient(
                    baseUrl,
                    string.IsNullOrWhiteSpace(apiKey) ? null : apiKey));
                return _generationService;
            }

            if (useHosted)
            {
                var gateway = HostedEndpoints.ResolveGatewayUrl();
                _generationService = new GenerationService(
                    new HostedGenerationClient(gateway, () => HostedRuntime.AccessToken));
                return _generationService;
            }

            return null;
        }
    }

    private static GenerationService GetGenerationService()
        => TryGetGenerationService()
            ?? throw new InvalidOperationException(GenerationUnavailableMessage());

    public static bool IsGenerationConfigured() => TryGetGenerationService() is not null;

    /// <summary>True when a generation API key is set with no endpoint URL, which generation refuses.</summary>
    public static bool IsGenerationEndpointMissing()
    {
        var prefs = UserPreferences.Load();
        return IsEndpointMissing(prefs.ResolveGenerationApiKey(), prefs.ResolveGenerationBaseUrl());
    }

    /// <summary>Why generation is unavailable: the missing endpoint when a key is set alone, otherwise
    /// the general setup message.</summary>
    public static string GenerationUnavailableMessage()
        => IsGenerationEndpointMissing() ? GenerationEndpointMissingMessage : GenerationUnconfiguredMessage;

    public static bool IsFakeGenerationActive() => IsFakeGenerationEnabled();

    public static GenerationService? TryResolveGenerationService() => TryGetGenerationService();

    public static string? CompletedGenerationStatus()
        => IsFakeGenerationEnabled() ? DemoGenerationStatus : null;

    public static bool CancelInFlightGeneration(string mediaRef)
    {
        if (string.IsNullOrWhiteSpace(mediaRef))
            return false;
        HostedGenerationClient? hosted = null;
        CancellationTokenSource? cancellation = null;
        string? jobId = null;
        lock (_genLock)
        {
            if (_generationCancellations.TryGetValue(mediaRef, out var cts))
            {
                cancellation = cts;
                hosted = _generationService?.Client as HostedGenerationClient;
            }
        }

        var entry = ProjectHub.ActiveSession?.Manifest.Entries
            .FirstOrDefault(e => string.Equals(e.Id, mediaRef, StringComparison.Ordinal));
        jobId = entry?.GenerationInput?.BackendJobId;
        var pending = PendingGenerations.FindByPlaceholder(mediaRef);
        if (entry is not null && (IsImagesJob(entry) || IsMediaJob(entry)) || pending is not null)
        {
            if (entry is not null)
            {
                ProjectHub.RunSerialized(() =>
                {
                    lock (_sessionLock)
                    {
                        if (entry.GenerationInput is { } input) input.CancelRequested = true;
                        ProjectHub.ActiveSession?.MarkDirty();
                        ProjectHub.ActiveSession?.Save();
                    }
                });
            }
            PendingGenerations.Update(mediaRef, job => job.CancelRequested = true);
            WakeHostedWatcher();
            if (cancellation is not null)
            {
                try { cancellation.Cancel(); } catch (ObjectDisposedException) { }
                return true;
            }
            if (ProjectHub.ActiveSession is { } session)
                _ = FinishHostedCancelAsync(session, mediaRef);
            return true;
        }
        if (cancellation is null)
            return false;
        try { cancellation.Cancel(); } catch (ObjectDisposedException) { return false; }
        if (string.IsNullOrWhiteSpace(jobId))
            return true;
        if (hosted is not null)
            _ = hosted.CancelJobAsync(jobId);

        return true;
    }

    public static void ApplyGenerationPlaceholderStatus(
        MediaManifestEntry entry,
        string status,
        string? jobId,
        IReadOnlyList<string>? resultUrls = null)
    {
        ArgumentNullException.ThrowIfNull(entry);
        entry.GenerationStatus = status;
        GenerationEntryGuard.Touch(entry);
        if (string.IsNullOrEmpty(jobId) && resultUrls is not { Count: > 0 })
            return;
        entry.GenerationInput ??= new GenerationInput();
        if (!string.IsNullOrEmpty(jobId))
            entry.GenerationInput.BackendJobId = jobId;
        if (resultUrls is { Count: > 0 })
            entry.GenerationInput.ResultUrls = resultUrls.ToList();
    }

    public static bool RetryGeneration(string mediaRef)
    {
        var session = GetSession();
        var entry = session.Manifest.Entries.FirstOrDefault(e => e.Id == mediaRef)
            ?? throw new InvalidOperationException($"Media asset not found: {mediaRef}");
        var packagePath = session.PackagePath
            ?? throw new InvalidOperationException("Open project has no package path.");
        if (IsImagesJob(entry))
            return TryRecoverImagesJob(session, entry, packagePath);
        if (IsMediaJob(entry))
            return TryRecoverMediaJob(session, entry, packagePath);
        var urls = entry.GenerationInput?.ResultUrls;
        var jobId = entry.GenerationInput?.BackendJobId;
        if (urls is not { Count: > 0 } && string.IsNullOrEmpty(jobId))
            return false;

        RunGenerationBackground(session, mediaRef, async token =>
        {
            var service = GetGenerationService();
            if (urls is { Count: > 0 })
            {
                await LandFromUrlAsync(session, mediaRef, packagePath, urls[0], jobId, urls, token, service)
                    .ConfigureAwait(false);
                return;
            }

            await PollDownloadAndLandAsync(
                    session,
                    mediaRef,
                    packagePath,
                    jobId!,
                    TimeSpan.FromMinutes(10),
                    token,
                    service)
                .ConfigureAwait(false);
        });
        return true;
    }

    public static void ResumePendingGenerations(ProjectSession? session = null)
    {
        session ??= ProjectHub.ActiveSession;
        if (session is null)
            return;
        var packagePath = session.PackagePath;
        if (string.IsNullOrEmpty(packagePath))
            return;

        // Every session bind comes through here, which is where the Mac settles interrupted imports.
        // The account watcher also resumes jobs whose project is not the one just opened.
        ResolveInterruptedImports(session);
        StartHostedWatcher();

        List<MediaManifestEntry> pending;
        lock (_sessionLock)
        {
            pending = session.Manifest.Entries.Where(ShouldResumeGeneration).ToList();
        }

        foreach (var entry in pending)
        {
            lock (_genLock)
            {
                if (_generationCancellations.ContainsKey(entry.Id))
                    continue;
            }

            if (IsImagesJob(entry))
            {
                // Unconfigured sign-in leaves the entry pending for the next resume.
                if (TryGetImageClient() is not null)
                    TryRecoverImagesJob(session, entry, packagePath);
                continue;
            }

            if (IsMediaJob(entry))
            {
                // Unconfigured sign-in leaves the entry pending for the next resume.
                if (TryGetMediaClient() is not null)
                    TryRecoverMediaJob(session, entry, packagePath);
                continue;
            }

            var id = entry.Id;
            var jobId = entry.GenerationInput?.BackendJobId;
            var urls = entry.GenerationInput?.ResultUrls;
            RunGenerationBackground(session, id, async token =>
            {
                var service = GetGenerationService();
                if (urls is { Count: > 0 })
                {
                    await LandFromUrlAsync(session, id, packagePath, urls[0], jobId, urls, token, service)
                        .ConfigureAwait(false);
                    return;
                }

                if (string.IsNullOrEmpty(jobId))
                    throw new InvalidOperationException("No backend job to resume.");
                await PollDownloadAndLandAsync(
                        session,
                        id,
                        packagePath,
                        jobId,
                        TimeSpan.FromMinutes(10),
                        token,
                        service)
                    .ConfigureAwait(false);
            });
        }
    }

    private static IVisualSearchService GetVisualSearchService()
    {
        lock (_visualSearchLock)
        {
            if (VisualSearchServiceOverride is not null)
                return VisualSearchServiceOverride;

            _visualSearchService ??= new VisualSearchService(new FfmpegFrameSource());
            return _visualSearchService;
        }
    }

    private class FakeHttpMessageHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.CompletedTask;
            if (request.RequestUri != null && request.RequestUri.Host == "fake.temno.io")
            {
                return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(System.Text.Encoding.UTF8.GetBytes("dummy media data"))
                };
            }
            return new HttpResponseMessage(System.Net.HttpStatusCode.NotFound);
        }
    }

    /// <summary>
    /// Tools whose handlers copy files or run FFmpeg. Execute does not marshal the whole
    /// call onto the UI dispatcher; each handler serializes only a short snapshot/commit.
    /// </summary>
    private static readonly HashSet<string> OffDispatcherTools = new(StringComparer.Ordinal)
    {
        "import_media",
        "search_media",
        "remove_silence",
        "inspect_media",
        "detect_beats",
        // A synchronous video export renders every frame through ffmpeg. Marshalling that onto the
        // WinUI dispatcher froze the entire app for the duration of the render.
        "export_project",
        // Both decode through ffmpeg (inspect_timeline also shells out per frame to encode a PNG).
        // Run on the dispatcher they blocked the UI thread for as long as ffmpeg took, and before
        // Temno.Media was made ConfigureAwait-clean they deadlocked it outright.
        "inspect_timeline",
        "inspect_color",
        // Read-only, but transcription is a minutes-long Whisper or cloud call.
        "get_transcript",
        // Pure network call against the generation backend; touches no project state.
        "list_models",
        "quote_image",
        "quote_media",
    };

    /// <summary>
    /// Tools that do NOT get marshalled onto the WinUI dispatcher. Exposed so
    /// <c>McpDispatcherDisciplineTests</c> can assert that every handler which blocks on async work
    /// is registered here.
    /// </summary>
    public static IReadOnlyCollection<string> ToolsThatRunOffDispatcher => OffDispatcherTools;

    public static ToolCallResult Execute(string name, JsonObject? args)
        => OffDispatcherTools.Contains(name)
            ? ExecuteCore(name, args)
            : ProjectHub.RunSerialized(() => ExecuteCore(name, args));

    private static ToolCallResult ExecuteCore(string name, JsonObject? args)
    {
        args ??= [];
        try
        {
            RejectUnknownArguments(name, args);
            var beforeIds = CollectIdUniverse();
            args = ToolIdShortener.Expand(args, beforeIds);
            AlsoKnownIds.Value = beforeIds;

            switch (name)
            {
                // --- Projects ---
                case "get_projects":
                    return Success(GetProjects());

                case "open_project":
                    return Success(OpenProject(args));

                case "new_project":
                    return Success(NewProject(args));

                case "close_project":
                    return Success(CloseProject(args));

                case "manage_project":
                    return Success(ManageProject(args));

                // --- Timelines ---
                case "get_timeline":
                    return Success(GetTimeline(args));

                case "create_timeline":
                    return Success(CreateTimeline(args));

                case "set_active_timeline":
                    return Success(SetActiveTimeline(args));

                case "set_project_settings":
                    return Success(SetProjectSettings(args));

                // --- Media ---
                case "get_media":
                    return Success(GetMedia(args));

                case "inspect_media":
                    return ShortenOutgoing(InspectMedia(args));

                case "import_media":
                    return Success(ImportMedia(args));

                case "search_media":
                    return Success(SearchMedia(args));

                // --- Clips ---
                case "manage_tracks":
                    return Success(ManageTracks(args));

                case "add_clips":
                    return Success(AddClips(args));

                case "insert_clips":
                    return Success(InsertClips(args));

                case "move_clips":
                    return Success(MoveClips(args));

                case "remove_clips":
                    return Success(RemoveClips(args));

                case "restore_linked_audio":
                    return Success(RestoreLinkedAudioTool(args));

                case "split_clips":
                    return Success(SplitClips(args));

                case "ripple_delete_ranges":
                    return Success(RippleDeleteRanges(args));

                case "set_clip_properties":
                    return Success(SetClipProperties(args));

                case "undo":
                    return Success(Undo());

                case "redo":
                    return Success(Redo());

                // --- Color / effects / keyframes / transitions ---
                case "apply_color":
                    return Success(ApplyColorTool(args));

                case "apply_effect":
                    return Success(ApplyEffectTool(args));

                case "set_keyframes":
                    return Success(SetKeyframesTool(args));

                case "add_transition":
                    return Success(AddTransitionTool(args));

                case "list_transitions":
                    return Success(ListTransitionsTool(args));

                case "remove_transition":
                    return Success(RemoveTransitionTool(args));

                // --- Markers ---
                case "add_marker":
                    return Success(AddMarker(args));

                case "list_markers":
                    return Success(ListMarkers(args));

                case "update_marker":
                    return Success(UpdateMarker(args));

                case "remove_marker":
                    return Success(RemoveMarker(args));

                // --- Text ---
                case "add_texts":
                    return Success(AddTextsTool(args));

                case "update_text":
                    return Success(UpdateTextTool(args));

                // --- Transcript ---
                case "get_transcript":
                    return Success(GetTranscript(args));

                case "remove_words":
                    return Success(RemoveWords(args));

                case "add_captions":
                    return Success(AddCaptions(args));

                // --- Inspection ---
                case "inspect_timeline":
                    return ShortenOutgoing(InspectTimeline(args));

                case "apply_layout":
                    return Success(ApplyLayout(args));

                case "inspect_color":
                    return Success(InspectColor(args));

                // --- Audio analysis ---
                case "detect_beats":
                    return Success(DetectBeats(args));

                case "remove_silence":
                    return Success(RemoveSilence(new SilenceDetectionOptions()));

                case "sync_clips":
                    return Success(SyncClips(args));

                // --- Generation ---
                case "list_models":
                    return Success(ListModels(args));

                case "upscale_media":
                    return Success(UpscaleMedia(args));

                case "generate_video":
                    return Success(GenerateVideo(args));

                case "generate_image":
                    return Success(GenerateImage(args));

                case "quote_image":
                    return Success(QuoteImage(args));

                case "edit_image":
                    return Success(SubmitImageJob(args, "edit_image"));

                case "quote_media":
                    return Success(QuoteMedia(args));

                case "resume_generation":
                    return Success(ResumeHostedGeneration(args["mediaRef"]?.GetValue<string>()));

                case "generate_audio":
                    return Success(GenerateAudio(args));

                case "generate_google":
                    return Success(GenerateGoogle(args));

                case "export_project":
                    return Success(ExportProject(args));

                case "manage_exports":
                    return Success(ManageExports(args));

                // --- WP-96 presets / validation / audio housekeep ---
                case "validate_timeline":
                    return Success(ValidateTimeline(args));
                case "suggest_crop":
                    return Success(SuggestCrop(args));
                case "denoise_audio":
                    return Success(DenoiseAudio(args));
                case "duck_audio":
                    return Success(DuckAudio(args));
                case "organize_media":
                    return Success(OrganizeMedia(args));
                case "apply_style":
                    return Success(ApplyStyle(args));
                case "save_text_style":
                    return Success(SaveTextStyle(args));
                case "list_text_styles":
                    return Success(ListTextStyles(args));
                case "remove_text_style":
                    return Success(RemoveTextStyle(args));
                case "save_export_preset":
                    return Success(SaveExportPreset(args));
                case "list_export_presets":
                    return Success(ListExportPresets(args));
                case "remove_export_preset":
                    return Success(RemoveExportPreset(args));
                case "save_project_template":
                    return Success(SaveProjectTemplate(args));
                case "list_project_templates":
                    return Success(ListProjectTemplates(args));
                case "remove_project_template":
                    return Success(RemoveProjectTemplate(args));
                case "send_feedback":
                    return Success(SendFeedback(args));
                case "read_skill":
                    return Success(ReadSkill(args));

                case "manage_multicam":
                    return Success(ManageMulticam(args));
                case "change_cam":
                    return Success(ChangeCam(args));
                case "get_multicam":
                    return Success(GetMulticam(args));

                default:
                    return new ToolCallResult
                    {
                        IsError = true,
                        Content =
                        [
                            new() {
                                Type = "text",
                                Text = $"Error: Tool '{name}' is not implemented yet on Windows."
                            }
                        ]
                    };
            }
        }
        catch (Exception ex)
        {
            return new ToolCallResult
            {
                IsError = true,
                Content =
                [
                    new() {
                        Type = "text",
                        Text = $"Error: {ex.Message}"
                    }
                ]
            };
        }
        finally
        {
            AlsoKnownIds.Value = null;
        }
    }

    public static ToolCallResult ImportMediaFile(
        ProjectSession session,
        string sourcePath,
        string? targetName = null,
        string? folderId = null,
        bool referenceInPlace = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        try
        {
            return Success(ImportMediaFileEntry(
                session, sourcePath, targetName, folderId, referenceInPlace, cancellationToken));
        }
        catch (Exception ex)
        {
            return Error(ex.Message);
        }
    }

    /// <summary>
    /// Runs the same one-undo silence-removal path as the MCP tool with Audio-tab timing values.
    /// These options are intentionally not MCP arguments: the Mac remove_silence schema takes no
    /// arguments, so the wire contract remains source-compatible.
    /// </summary>
    public static ToolCallResult RemoveSilenceForEditor(SilenceDetectionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        try
        {
            options.Validate();
            return Success(RemoveSilence(options));
        }
        catch (Exception ex)
        {
            return Error(ex.Message);
        }
    }

    private static readonly AsyncLocal<HashSet<string>?> AlsoKnownIds = new();

    private static ToolCallResult Success(object obj)
    {
        var json = JsonSerializer.Serialize(obj, new JsonSerializerOptions
        {
            WriteIndented = false
        });
        return ShortenOutgoing(new ToolCallResult
        {
            IsError = false,
            Content = [new TextContent { Type = "text", Text = json }]
        });
    }

    private static ToolCallResult ShortenOutgoing(ToolCallResult result)
    {
        if (result.IsError) return result;
        var universe = CollectIdUniverse();
        if (AlsoKnownIds.Value is { Count: > 0 } extra)
            universe.UnionWith(extra);
        return ToolIdShortener.Shorten(result, universe);
    }

    private static ToolCallResult Error(string message)
    {
        return new ToolCallResult
        {
            IsError = true,
            Content = [new TextContent { Type = "text", Text = $"Error: {message}" }]
        };
    }

    private static ProjectSession GetSession()
    {
        return ProjectHub.ActiveSession ?? throw new InvalidOperationException("No active project session. Open or create a project first.");
    }

    /// <summary>
    /// WP-435: the editor's playhead on <paramref name="timeline"/>, as <c>TimelineControl.PersistPlayhead</c>
    /// last saved it into the project's view state, clamped the way the timeline clamps it. 0 when the
    /// timeline has no view state yet. Mirrors the Mac's <c>editor.currentFrame</c>.
    /// </summary>
    internal static int PlayheadFrame(ProjectSession session, Timeline timeline)
    {
        var frame = session.ProjectFile.ViewStates is { } states && states.TryGetValue(timeline.Id, out var vs)
            ? vs.PlayheadFrame
            : 0;
        return Math.Clamp(frame, 0, Math.Max(0, timeline.TotalFrames));
    }

    /// <summary>WP-435: a track's label as the timeline header shows it (WP-188), e.g. V2, V1, A1, A2.</summary>
    private static string TrackLabel(Timeline timeline, int index)
        => Temno.Core.Ui.TrackLabels.LabelFor(timeline.Tracks.Select(t => t.Type).ToList(), index);

    // --- Projects implementations ---

    private static object GetProjects()
    {
        var entries = ProjectRegistry.Load();
        var session = ProjectHub.ActiveSession;
        var projectsList = entries.Select(e => {
            var path = e.Url.StartsWith("file:") ? new Uri(e.Url).LocalPath : e.Url;
            var isOpen = session != null && PathEquals(session.PackagePath, path);
            return new {
                id = e.Id.ToString().ToUpperInvariant(),
                name = e.Name,
                path = path,
                isOpen = isOpen,
                isActive = isOpen,
                isAccessible = e.IsAccessible
            };
        }).ToList();

        object? activeObj = null;
        if (session != null)
        {
            var packagePath = session.PackagePath ?? "";
            var name = string.IsNullOrEmpty(packagePath) ? "Untitled Project" : Path.GetFileNameWithoutExtension(packagePath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            activeObj = new { name = name, path = packagePath };
        }

        return new
        {
            openCount = session != null ? 1 : 0,
            projects = projectsList,
            active = activeObj
        };
    }

    private static object OpenProject(JsonObject args)
    {
        var name = args["name"]?.GetValue<string>();
        var id = args["id"]?.GetValue<string>();
        var path = args["path"]?.GetValue<string>();

        var resolvedPath = ProjectHub.ResolveProjectUrl(name, id, path);
        if (!Directory.Exists(resolvedPath))
        {
            throw new InvalidOperationException($"No project at {resolvedPath}.");
        }

        var current = ProjectHub.ActiveSession;
        if (current is not null && PathEquals(current.PackagePath, resolvedPath))
            return GetProjectSnapshot("active");

        if (current is not null && current.IsDirty)
        {
            throw new InvalidOperationException(
                "Close the current project first (close_project). Unsaved changes would be discarded.");
        }

        ProjectHub.ActiveSession = ProjectSession.Open(resolvedPath);
        ProjectRegistry.Register(resolvedPath);
        // The headless host binds no editor, so open_project settles interrupted imports itself.
        ResolveInterruptedImports(ProjectHub.ActiveSession);

        return GetProjectSnapshot("active");
    }

    private static object NewProject(JsonObject args)
    {
        var name = args["name"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(name))
            name = ProjectConstants.DefaultProjectName;
        var aspectRatio = args["aspectRatio"]?.GetValue<string>();
        var quality = args["quality"]?.GetValue<string>();
        var templateName = args["template"] is JsonValue templateValue && templateValue.TryGetValue<string>(out var templateText)
            ? templateText
            : null;

        var projectsDirEnv = EnvCompat.Get("TEMNO_PROJECTS_DIR");
        var parentDir = !string.IsNullOrEmpty(projectsDirEnv)
            ? projectsDirEnv
            : Path.Combine(UserDataRoots.DocumentsRoot, "Projects");
        var packagePath = ProjectConstants.ResolveNewPackagePath(parentDir, name);
        if (Directory.Exists(packagePath) || File.Exists(packagePath))
            throw new InvalidOperationException($"Project at {packagePath} already exists.");

        if (ProjectHub.ActiveSession is { IsDirty: true })
        {
            throw new InvalidOperationException(
                "Close the current project first (close_project). Unsaved changes would be discarded.");
        }

        ProjectTemplateContents? template = null;
        if (!string.IsNullOrWhiteSpace(templateName))
        {
            template = ProjectTemplateStore.Shared.TryLoad(templateName)
                ?? throw new InvalidOperationException($"Project template not found: {templateName}");
        }

        ProjectSession session;
        if (template is not null)
        {
            // WP-399: a template is a file under the user's data folder that may have come from anywhere.
            session = ProjectSession.FromTemplate(template.Project, template.Manifest ?? new MediaManifest());
            if (args["fps"] is not null)
            {
                var templateFps = args["fps"]!.GetValue<int>();
                foreach (var tl in session.ProjectFile.Timelines)
                {
                    tl.Fps = templateFps;
                    tl.SettingsConfigured = true;
                }
            }
            if (aspectRatio is not null || quality is not null)
            {
                var (width, height) = ResolveCanvas(aspectRatio, quality, session.ActiveTimeline.Width, session.ActiveTimeline.Height);
                var timelines = session.ProjectFile.Timelines;
                // WP-405: a template's clips keep their shape on the new canvas, as set_project_settings does.
                foreach (var tl in timelines)
                {
                    if (tl.Width != width || tl.Height != height)
                    {
                        ClipRefit.ForCanvasChange(tl, width, height,
                            clip => ClipRefit.SourceSize(session.Manifest, timelines, clip));
                    }
                }
                foreach (var tl in timelines)
                {
                    tl.Width = width;
                    tl.Height = height;
                    tl.SettingsConfigured = true;
                }
            }
        }
        else
        {
            var fps = args["fps"]?.GetValue<int>() ?? 30;
            var (width, height) = ResolveCanvas(aspectRatio, quality, 1920, 1080);
            session = ProjectSession.CreateNew(name: "Timeline 1", fps: fps, width: width, height: height);
        }

        Directory.CreateDirectory(parentDir);
        Directory.CreateDirectory(packagePath);
        try
        {
            Directory.CreateDirectory(Path.Combine(packagePath, ProjectConstants.MediaDirectoryName));
            session.Save(packagePath);
        }
        catch
        {
            try { Directory.Delete(packagePath, recursive: true); }
            catch { /* The failed package is left in place rather than deleting a path we did not create. */ }
            throw;
        }

        ProjectHub.ActiveSession = session;
        ProjectRegistry.Register(packagePath);

        return GetProjectSnapshot("created");
    }

    private static (int Width, int Height) ResolveCanvas(string? aspectRatio, string? quality, int width, int height)
    {
        if (aspectRatio != null)
        {
            switch (aspectRatio)
            {
                case "16:9": width = 1920; height = 1080; break;
                case "9:14": width = 1080; height = 1680; break;
                case "9:16": width = 1080; height = 1920; break;
                case "1:1": width = 1080; height = 1080; break;
                case "4:3": width = 1440; height = 1080; break;
                case "2.4:1": width = 2560; height = 1080; break;
            }
        }
        if (quality != null)
        {
            int shortEdge = quality switch
            {
                "720p" => 720,
                "1080p" => 1080,
                "2K" => 1440,
                "4K" => 2160,
                _ => 1080
            };
            if (width <= height)
            {
                height = (int)Math.Round((double)shortEdge * height / width);
                width = shortEdge;
            }
            else
            {
                width = (int)Math.Round((double)shortEdge * width / height);
                height = shortEdge;
            }
        }

        return (width, height);
    }

    private static object CloseProject(JsonObject args)
    {
        var name = args["name"]?.GetValue<string>();
        var id = args["id"]?.GetValue<string>();
        var path = args["path"]?.GetValue<string>();

        ProjectSession target;
        if (name != null || id != null || path != null)
        {
            var resolvedPath = ProjectHub.ResolveProjectUrl(name, id, path);
            if (ProjectHub.ActiveSession != null && PathEquals(ProjectHub.ActiveSession.PackagePath, resolvedPath))
            {
                target = ProjectHub.ActiveSession;
            }
            else
            {
                throw new InvalidOperationException($"Project at {resolvedPath} isn't open.");
            }
        }
        else if (ProjectHub.ActiveSession != null)
        {
            target = ProjectHub.ActiveSession;
        }
        else
        {
            throw new InvalidOperationException("No project is open.");
        }

        var targetName = Path.GetFileNameWithoutExtension(target.PackagePath?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)) ?? "Untitled Project";
        target.Save();
        ProjectHub.ActiveSession = null;

        return new
        {
            status = "closed",
            name = targetName,
            openCount = 0
        };
    }

    // --- Timelines implementations ---

    private struct ClipPlacement
    {
        public string TrackId { get; set; }
        public int Index { get; set; }
        public int Start { get; set; }
        public int Duration { get; set; }

        public readonly bool SamePlace(ClipPlacement other)
        {
            return TrackId == other.TrackId && Start == other.Start && Duration == other.Duration;
        }
    }

    private struct TimelineSnapshot
    {
        public Dictionary<string, ClipPlacement> Placements { get; set; }
        public List<string> TrackIds { get; set; }
    }

    private static TimelineSnapshot TakeTimelineSnapshot(Timeline timeline)
    {
        var placements = new Dictionary<string, ClipPlacement>();
        var trackIds = new List<string>();
        for (int i = 0; i < timeline.Tracks.Count; i++)
        {
            var track = timeline.Tracks[i];
            trackIds.Add(track.Id);
            foreach (var clip in track.Clips)
            {
                placements[clip.Id] = new ClipPlacement
                {
                    TrackId = track.Id,
                    Index = i,
                    Start = clip.StartFrame,
                    Duration = clip.DurationFrames
                };
            }
        }
        return new TimelineSnapshot { Placements = placements, TrackIds = trackIds };
    }

    private struct LinkFold
    {
        public Dictionary<string, (Clip Clip, int TrackIndex)> PartnerByVisualId { get; } = new();
        public HashSet<string> FoldedAudioIds { get; } = new();
        public Dictionary<int, int> FoldedCountByTrack { get; } = new();

        public LinkFold() {}
    }

    private static LinkFold BuildLinkFoldPlan(Timeline timeline)
    {
        var groups = new Dictionary<string, List<(Clip Clip, int TrackIndex)>>();
        for (int ti = 0; ti < timeline.Tracks.Count; ti++)
        {
            var track = timeline.Tracks[ti];
            foreach (var clip in track.Clips)
            {
                if (!string.IsNullOrEmpty(clip.LinkGroupId))
                {
                    if (!groups.TryGetValue(clip.LinkGroupId, out var list))
                    {
                        list = [];
                        groups[clip.LinkGroupId] = list;
                    }
                    list.Add((clip, ti));
                }
            }
        }

        var plan = new LinkFold();
        foreach (var members in groups.Values)
        {
            if (members.Count == 2)
            {
                var audioIdx = members.FindIndex(m => m.Clip.MediaType == ClipType.audio);
                var visualIdx = members.FindIndex(m => m.Clip.MediaType != ClipType.audio);
                if (audioIdx >= 0 && visualIdx >= 0)
                {
                    var audio = members[audioIdx];
                    var visual = members[visualIdx];
                    plan.PartnerByVisualId[visual.Clip.Id] = (audio.Clip, audio.TrackIndex);
                    plan.FoldedAudioIds.Add(audio.Clip.Id);
                    plan.FoldedCountByTrack[audio.TrackIndex] = plan.FoldedCountByTrack.GetValueOrDefault(audio.TrackIndex) + 1;
                }
            }
        }
        return plan;
    }

    private static Dictionary<string, object> GetAudioSummary(Clip partner, int trackIndex, Clip visual)
    {
        var outDict = new Dictionary<string, object>
        {
            ["id"] = partner.Id,
            ["track"] = trackIndex
        };

        if (partner.StartFrame != visual.StartFrame || partner.DurationFrames != visual.DurationFrames)
        {
            outDict["frames"] = new[] { partner.StartFrame, partner.StartFrame + partner.DurationFrames };
        }

        if (partner.TrimStartFrame != visual.TrimStartFrame) outDict["trimStartFrame"] = partner.TrimStartFrame;
        if (partner.TrimEndFrame != visual.TrimEndFrame) outDict["trimEndFrame"] = partner.TrimEndFrame;
        if (Math.Abs(partner.Speed - visual.Speed) > 0.0001) outDict["speed"] = Math.Round(partner.Speed, 3);

        // Volume and fade properties (only include if they deviate from defaults)
        if (Math.Abs(partner.Volume - 1.0) > 0.0001) outDict["volume"] = Math.Round(partner.Volume, 3);
        if (partner.FadeInFrames != 0) outDict["fadeInFrames"] = partner.FadeInFrames;
        if (partner.FadeOutFrames != 0) outDict["fadeOutFrames"] = partner.FadeOutFrames;
        if (partner.FadeInInterpolation != Interpolation.linear) outDict["fadeInInterpolation"] = partner.FadeInInterpolation.ToString();
        if (partner.FadeOutInterpolation != Interpolation.linear) outDict["fadeOutInterpolation"] = partner.FadeOutInterpolation.ToString();

        // Keyframes compaction
        var kfs = GetCompactKeyframes(partner);
        if (kfs != null && kfs.Count > 0)
        {
            outDict["keyframes"] = kfs;
        }

        // Effects compaction
        var compactedEffects = GetCompactEffects(partner.Effects);
        if (compactedEffects != null && compactedEffects.Count > 0)
        {
            outDict["effects"] = compactedEffects;
        }

        return outDict;
    }

    private static Dictionary<string, List<object>>? GetCompactKeyframes(Clip c)
    {
        var keyframes = new Dictionary<string, List<object>>();

        // volume
        // Volume-track values are dB, not the Clip.Volume linear scalar. This method only shapes
        // a response; it must never collapse an animated value back into the live clip.
        ProcessTrack(c.VolumeTrack, "volume", 0.0, kf => [kf.Value]);
        // opacity
        ProcessTrack(c.OpacityTrack, "opacity", 1.0, kf => [kf.Value]);
        // rotation
        ProcessTrack(c.RotationTrack, "rotation", 0.0, kf => [kf.Value]);
        // position
        ProcessTrack(c.PositionTrack, "position", null, kf => [kf.Value.A, kf.Value.B]);
        // scale
        ProcessTrack(c.ScaleTrack, "scale", null, kf => [kf.Value.A, kf.Value.B]);
        // crop
        ProcessTrack(c.CropTrack, "crop", null, kf => [kf.Value.Top, kf.Value.Right, kf.Value.Bottom, kf.Value.Left]);

        void ProcessTrack<T>(KeyframeTrack<T>? track, string key, double? scalarDefault, Func<Keyframe<T>, List<double>> valuesFunc)
        {
            if (track == null || !track.IsActive) return;

            // Check if all keyframes are nearly identical
            var firstValues = valuesFunc(track.Keyframes[0]);
            bool allNearlyEqual = true;
            foreach (var kf in track.Keyframes)
            {
                var currentValues = valuesFunc(kf);
                if (firstValues.Count != currentValues.Count) { allNearlyEqual = false; break; }
                for (int i = 0; i < firstValues.Count; i++)
                {
                    if (Math.Abs(firstValues[i] - currentValues[i]) >= 0.0005)
                    {
                        allNearlyEqual = false;
                        break;
                    }
                }
                if (!allNearlyEqual) break;
            }

            if (allNearlyEqual)
            {
                if (scalarDefault is not null && firstValues.Count == 1
                    && Math.Abs(firstValues[0] - scalarDefault.Value) < 0.0005)
                    return;
            }

            // Otherwise, serialize list of keyframes
            var list = new List<object>();
            foreach (var kf in track.Keyframes)
            {
                var row = new List<object> { kf.Frame };
                var vals = valuesFunc(kf);
                foreach (var v in vals)
                {
                    row.Add(Math.Round(v, 3));
                }
                if (kf.InterpolationOut != Interpolation.smooth)
                {
                    row.Add(kf.InterpolationOut.ToString());
                }
                list.Add(row);
            }
            keyframes[key] = list;
        }

        return keyframes.Count > 0 ? keyframes : null;
    }

    private static List<Dictionary<string, object>>? GetCompactEffects(List<Effect>? effects)
    {
        if (effects == null) return null;
        var outList = new List<Dictionary<string, object>>();
        foreach (var e in effects)
        {
            if (e.Type.StartsWith("color.")) continue;
            var outEffect = new Dictionary<string, object>
            {
                ["type"] = e.Type
            };
            if (e.Params != null && e.Params.Count > 0)
            {
                var flatParams = new Dictionary<string, object>();
                foreach (var kvp in e.Params)
                {
                    var param = kvp.Value;
                    if (param.Track != null && param.Track.IsActive)
                    {
                        flatParams[kvp.Key] = "animated";
                    }
                    else if (param.String != null)
                    {
                        flatParams[kvp.Key] = param.String;
                    }
                    else if (param.Value != null)
                    {
                        flatParams[kvp.Key] = Math.Round(param.Value.Value, 3);
                    }
                }
                if (flatParams.Count > 0)
                {
                    outEffect["params"] = flatParams;
                }
            }
            if (!e.Enabled)
            {
                outEffect["enabled"] = false;
            }
            outList.Add(outEffect);
        }
        return outList.Count > 0 ? outList : null;
    }

    private static Dictionary<string, object>? GetColorObject(List<Effect>? effects)
    {
        if (effects == null || !effects.Any(e => e.Type.StartsWith("color.")))
            return null;

        var colorObj = new Dictionary<string, object>();
        double? exposure = null, temperature = null, tint = null, contrast = null;
        double? highlights = null, shadows = null, blacks = null, whites = null;
        double? vibrance = null, saturation = null;
        string? lutPath = null;
        double? lutIntensity = null;
        double? shadowsHue = null, shadowsAmount = null, shadowsLum = null;
        double? midsHue = null, midsAmount = null, midsGamma = null;
        double? highsHue = null, highsAmount = null, highsGain = null;

        List<List<double>>? masterCurve = null, redCurve = null, greenCurve = null, blueCurve = null;
        Dictionary<string, List<List<double>>>? hueCurvesRaw = null;

        foreach (var e in effects)
        {
            switch (e.Type)
            {
                case "color.exposure":
                    if (e.Params.TryGetValue("ev", out var pEv)) exposure = pEv.Value;
                    break;
                case "color.temperature":
                    if (e.Params.TryGetValue("temperature", out var pTemp)) temperature = pTemp.Value;
                    if (e.Params.TryGetValue("tint", out var pTint)) tint = pTint.Value;
                    break;
                case "color.contrast":
                    if (e.Params.TryGetValue("amount", out var pCont)) contrast = pCont.Value;
                    break;
                case "color.highlightsShadows":
                    if (e.Params.TryGetValue("highlights", out var pHigh)) highlights = pHigh.Value;
                    if (e.Params.TryGetValue("shadows", out var pShad)) shadows = pShad.Value;
                    break;
                case "color.blacksWhites":
                    if (e.Params.TryGetValue("blacks", out var pBlk)) blacks = pBlk.Value;
                    if (e.Params.TryGetValue("whites", out var pWht)) whites = pWht.Value;
                    break;
                case "color.vibrance":
                    if (e.Params.TryGetValue("amount", out var pVib)) vibrance = pVib.Value;
                    break;
                case "color.saturation":
                    if (e.Params.TryGetValue("amount", out var pSat)) saturation = pSat.Value;
                    break;
                case "color.lut":
                    if (e.Params.TryGetValue("path", out var pLut)) lutPath = pLut.String;
                    if (e.Params.TryGetValue("intensity", out var pLutInt)) lutIntensity = pLutInt.Value;
                    break;
                case "color.wheels":
                    if (e.Params.TryGetValue("shadowsHue", out var pShHue)) shadowsHue = pShHue.Value;
                    if (e.Params.TryGetValue("shadowsAmount", out var pShAmt)) shadowsAmount = pShAmt.Value;
                    if (e.Params.TryGetValue("shadowsLum", out var pShLum)) shadowsLum = pShLum.Value;
                    if (e.Params.TryGetValue("midsHue", out var pMiHue)) midsHue = pMiHue.Value;
                    if (e.Params.TryGetValue("midsAmount", out var pMiAmt)) midsAmount = pMiAmt.Value;
                    if (e.Params.TryGetValue("midsGamma", out var pMiGam)) midsGamma = pMiGam.Value;
                    if (e.Params.TryGetValue("highsHue", out var pHiHue)) highsHue = pHiHue.Value;
                    if (e.Params.TryGetValue("highsAmount", out var pHiAmt)) highsAmount = pHiAmt.Value;
                    if (e.Params.TryGetValue("highsGain", out var pHiGain)) highsGain = pHiGain.Value;
                    break;
                case "color.curves":
                    if (e.Params.TryGetValue("curve", out var pCurve) && !string.IsNullOrEmpty(pCurve.String))
                    {
                        try
                        {
                            using var doc = JsonDocument.Parse(pCurve.String);
                            var root = doc.RootElement;
                            masterCurve = ParseCurvePoints(root, "master");
                            redCurve = ParseCurvePoints(root, "red");
                            greenCurve = ParseCurvePoints(root, "green");
                            blueCurve = ParseCurvePoints(root, "blue");
                        }
                        catch {}
                    }
                    break;
                case "color.hueCurves":
                    if (e.Params.TryGetValue("curves", out var pHueCurves) && !string.IsNullOrEmpty(pHueCurves.String))
                    {
                        try
                        {
                            using var doc = JsonDocument.Parse(pHueCurves.String);
                            var root = doc.RootElement;
                            var rawCurves = new Dictionary<string, List<List<double>>>();
                            var hvh = ParseCurvePoints(root, "hueVsHue");
                            if (hvh != null) rawCurves["hueVsHue"] = hvh;
                            var hvs = ParseCurvePoints(root, "hueVsSat");
                            if (hvs != null) rawCurves["hueVsSat"] = hvs;
                            var hvl = ParseCurvePoints(root, "hueVsLum");
                            if (hvl != null) rawCurves["hueVsLum"] = hvl;
                            if (rawCurves.Any()) hueCurvesRaw = rawCurves;
                        }
                        catch {}
                    }
                    break;
            }
        }

        void AddIfValid(string key, double? val, double? neutral = null)
        {
            if (val != null && (neutral == null || Math.Abs(val.Value - neutral.Value) > 0.0001))
            {
                colorObj[key] = Math.Round(val.Value, 3);
            }
        }

        AddIfValid("exposure", exposure);
        AddIfValid("contrast", contrast);
        AddIfValid("saturation", saturation);
        AddIfValid("vibrance", vibrance);
        AddIfValid("temperature", temperature);
        AddIfValid("tint", tint);
        AddIfValid("highlights", highlights);
        AddIfValid("shadows", shadows);
        AddIfValid("blacks", blacks);
        AddIfValid("whites", whites);

        AddIfValid("shadowsHue", shadowsHue);
        AddIfValid("shadowsAmount", shadowsAmount, 0.0);
        AddIfValid("shadowsLum", shadowsLum, 0.0);
        AddIfValid("midsHue", midsHue);
        AddIfValid("midsAmount", midsAmount, 0.0);
        AddIfValid("midsGamma", midsGamma, 1.0);
        AddIfValid("highsHue", highsHue);
        AddIfValid("highsAmount", highsAmount, 0.0);
        AddIfValid("highsGain", highsGain, 1.0);

        if (!colorObj.ContainsKey("shadowsAmount")) colorObj.Remove("shadowsHue");
        if (!colorObj.ContainsKey("midsAmount")) colorObj.Remove("midsHue");
        if (!colorObj.ContainsKey("highsAmount")) colorObj.Remove("highsHue");

        if (masterCurve != null) colorObj["masterCurve"] = masterCurve;
        if (redCurve != null) colorObj["redCurve"] = redCurve;
        if (greenCurve != null) colorObj["greenCurve"] = greenCurve;
        if (blueCurve != null) colorObj["blueCurve"] = blueCurve;
        if (hueCurvesRaw != null) colorObj["hueCurvesRaw"] = hueCurvesRaw;

        if (!string.IsNullOrEmpty(lutPath))
        {
            colorObj["lut"] = new Dictionary<string, object>
            {
                ["path"] = lutPath,
                ["strength"] = Math.Round(lutIntensity ?? 1.0, 3)
            };
        }

        return colorObj.Count > 0 ? colorObj : null;
    }

    private static List<List<double>>? ParseCurvePoints(JsonElement root, string key)
    {
        if (root.TryGetProperty(key, out var prop) && prop.ValueKind == JsonValueKind.Array)
        {
            var list = new List<List<double>>();
            foreach (var pt in prop.EnumerateArray())
            {
                if (pt.ValueKind == JsonValueKind.Object && pt.TryGetProperty("x", out var xProp) && pt.TryGetProperty("y", out var yProp))
                {
                    list.Add([Math.Round(xProp.GetDouble(), 3), Math.Round(yProp.GetDouble(), 3)]);
                }
                else if (pt.ValueKind == JsonValueKind.Array && pt.GetArrayLength() >= 2)
                {
                    list.Add([Math.Round(pt[0].GetDouble(), 3), Math.Round(pt[1].GetDouble(), 3)]);
                }
            }
            return list.Any() ? list : null;
        }
        return null;
    }

    private static Dictionary<string, object> GetCompactClip(Clip c, LinkFold fold, Dictionary<string, Dictionary<string, object>> grades)
    {
        var keyframes = GetCompactKeyframes(c);

        var outDict = new Dictionary<string, object>
        {
            ["id"] = c.Id,
            ["mediaRef"] = c.MediaRef,
            ["frames"] = new[] { c.StartFrame, c.StartFrame + c.DurationFrames }
        };

        if (c.MediaType != ClipType.video) outDict["mediaType"] = c.MediaType.ToString();
        if (c.SourceClipType != c.MediaType) outDict["sourceClipType"] = c.SourceClipType.ToString();

        if (c.MediaType != ClipType.text)
        {
            if (c.TrimStartFrame != 0) outDict["trimStartFrame"] = c.TrimStartFrame;
            if (c.TrimEndFrame != 0) outDict["trimEndFrame"] = c.TrimEndFrame;
        }

        if (Math.Abs(c.Speed - 1.0) > 0.0001) outDict["speed"] = Math.Round(c.Speed, 3);
        if (Math.Abs(c.Volume - 1.0) > 0.0001) outDict["volume"] = Math.Round(c.Volume, 3);
        if (Math.Abs(c.Opacity - 1.0) > 0.0001) outDict["opacity"] = Math.Round(c.Opacity, 3);

        if (c.FadeInFrames != 0) outDict["fadeInFrames"] = c.FadeInFrames;
        if (c.FadeOutFrames != 0) outDict["fadeOutFrames"] = c.FadeOutFrames;
        if (c.FadeInInterpolation != Interpolation.linear) outDict["fadeInInterpolation"] = c.FadeInInterpolation.ToString();
        if (c.FadeOutInterpolation != Interpolation.linear) outDict["fadeOutInterpolation"] = c.FadeOutInterpolation.ToString();

        if (c.TextContent != null) outDict["textContent"] = c.TextContent;
        if (c.CaptionGroupId != null) outDict["captionGroupId"] = c.CaptionGroupId;
        if (c.LinkGroupId != null && !fold.FoldedAudioIds.Contains(c.Id)) outDict["linkGroupId"] = c.LinkGroupId;

        if (c.TextStyle != null && c.TextStyle.Value.ValueKind != JsonValueKind.Null && c.TextStyle.Value.ValueKind != JsonValueKind.Undefined)
        {
            outDict["textStyle"] = c.TextStyle.Value;
        }
        if (c.TextAnimation != null && c.TextAnimation.Value.ValueKind != JsonValueKind.Null && c.TextAnimation.Value.ValueKind != JsonValueKind.Undefined)
        {
            outDict["textAnimation"] = c.TextAnimation.Value;
        }
        if (c.WordTimings != null && c.WordTimings.Count > 0)
        {
            outDict["wordTimings"] = c.WordTimings;
        }

        if (grades.TryGetValue(c.Id, out var grade))
        {
            outDict["color"] = grade;
        }

        if (keyframes != null && keyframes.Count > 0)
        {
            outDict["keyframes"] = keyframes;
        }

        var compactedEffects = GetCompactEffects(c.Effects);
        if (compactedEffects != null && compactedEffects.Count > 0)
        {
            outDict["effects"] = compactedEffects;
        }

        if (fold.PartnerByVisualId.TryGetValue(c.Id, out var partner))
        {
            outDict["audio"] = GetAudioSummary(partner.Clip, partner.TrackIndex, c);
            outDict.Remove("linkGroupId");
        }

        return outDict;
    }

    private static List<int[]> GetTrackGaps(Track track)
    {
        var spans = track.Clips
            .Where(c => string.IsNullOrEmpty(c.CaptionGroupId))
            .Select(c => new[] { c.StartFrame, c.StartFrame + c.DurationFrames })
            .OrderBy(s => s[0])
            .ToList();

        var gaps = new List<int[]>();
        int? maxEnd = null;
        foreach (var span in spans)
        {
            if (maxEnd != null && span[0] > maxEnd.Value)
            {
                gaps.Add([maxEnd.Value, span[0]]);
            }
            maxEnd = Math.Max(maxEnd ?? span[1], span[1]);
        }
        return gaps;
    }

    private static void ProcessCaptionGroups(
        List<Dictionary<string, object>> compactedClips,
        bool captionDetail,
        int? startFrame,
        int? endFrame,
        out List<Dictionary<string, object>> looseClips,
        out List<Dictionary<string, object>> captionGroups)
    {
        looseClips = [];
        captionGroups = [];

        var groupedByGid = new Dictionary<string, List<Dictionary<string, object>>>();
        var gidOrder = new List<string>();

        foreach (var clip in compactedClips)
        {
            if (clip.TryGetValue("captionGroupId", out var gidObj) && gidObj is string gid && !string.IsNullOrEmpty(gid))
            {
                if (!groupedByGid.TryGetValue(gid, out var list))
                {
                    list = [];
                    groupedByGid[gid] = list;
                    gidOrder.Add(gid);
                }
                list.Add(clip);
            }
            else
            {
                looseClips.Add(clip);
            }
        }

        foreach (var gid in gidOrder)
        {
            var members = groupedByGid[gid];
            var counts = new Dictionary<string, int>();
            var canonicalDicts = new Dictionary<string, Dictionary<string, object>>();
            var modalKey = "";

            var rowKeys = new HashSet<string> { "id", "frames", "textContent", "captionGroupId", "wordTimings" };

            var entries = new List<(Dictionary<string, object> Clip, string Key)>();
            foreach (var clip in members)
            {
                var residual = new Dictionary<string, object>();
                foreach (var kvp in clip)
                {
                    if (rowKeys.Contains(kvp.Key)) continue;
                    if (kvp.Key == "transform" && kvp.Value is Dictionary<string, object> t)
                    {
                        var tCopy = new Dictionary<string, object>(t);
                        tCopy.Remove("width");
                        tCopy.Remove("height");
                        if (tCopy.Count > 0) residual["transform"] = tCopy;
                    }
                    else if (kvp.Key == "transform" && kvp.Value is Transform tr)
                    {
                        var tCopy = new Dictionary<string, object>
                        {
                            ["centerX"] = tr.CenterX,
                            ["centerY"] = tr.CenterY,
                            ["flipHorizontal"] = tr.FlipHorizontal,
                            ["flipVertical"] = tr.FlipVertical
                        };
                        residual["transform"] = tCopy;
                    }
                    else
                    {
                        residual[kvp.Key] = kvp.Value;
                    }
                }

                var key = JsonSerializer.Serialize(residual, new JsonSerializerOptions { WriteIndented = false });
                counts[key] = counts.GetValueOrDefault(key) + 1;
                canonicalDicts[key] = residual;
                if (modalKey == "" || counts[key] > counts[modalKey])
                {
                    modalKey = key;
                }
                entries.Add((clip, key));
            }

            var shared = canonicalDicts.GetValueOrDefault(modalKey) ?? [];
            var rows = new List<List<object>>();
            var frameMin = int.MaxValue;
            var frameMax = 0;

            foreach (var entry in entries)
            {
                var clip = entry.Clip;
                var key = entry.Key;
                var frames = clip["frames"] as int[];
                if (frames == null && clip["frames"] is Array arr && arr.Length >= 2)
                {
                    frames = [(int)arr.GetValue(0)!, (int)arr.GetValue(1)!];
                }
                int start = frames?[0] ?? 0;
                int end = frames?[1] ?? 0;

                frameMin = Math.Min(frameMin, start);
                frameMax = Math.Max(frameMax, end);

                if (key == modalKey)
                {
                    rows.Add([clip["id"], start, end, clip.GetValueOrDefault("textContent") ?? ""]);
                }
                else
                {
                    looseClips.Add(clip);
                }
            }

            int total = rows.Count;
            if (startFrame != null || endFrame != null)
            {
                var sf = startFrame ?? 0;
                var ef = endFrame ?? int.MaxValue;
                rows = rows.Where(r => (int)r[1] < ef && (int)r[2] > sf).ToList();
            }

            rows = rows.OrderBy(r => (int)r[1]).ToList();

            var group = new Dictionary<string, object>
            {
                ["captionGroupId"] = gid,
                ["clipCount"] = total,
                ["frameRange"] = new[] { frameMin, frameMax }
            };

            if (shared.Count > 0)
            {
                group["shared"] = shared;
            }

            if (!captionDetail)
            {
                if (rows.Count > 0)
                {
                    var firstText = rows.First()[3]?.ToString() ?? "";
                    var lastText = rows.Last()[3]?.ToString() ?? "";
                    group["textPreview"] = rows.Count == 1
                        ? Truncate(firstText)
                        : $"{Truncate(firstText)} … {Truncate(lastText)}";
                }
                group["clipsNote"] = "Per-clip rows omitted — re-read with captionDetail:true for [\"clipId\", \"startFrame\", \"endFrame\", \"text\"] rows; get_transcript has the spoken words.";
            }
            else
            {
                var shown = rows.Take(200).ToList();
                group["clipFormat"] = new[] { "clipId", "startFrame", "endFrame", "text" };
                group["clips"] = shown;
                if (shown.Count < total)
                {
                    group["clipsNote"] = $"Showing {shown.Count} of {total} caption clips. Page with startFrame/endFrame.";
                }
            }

            captionGroups.Add(group);
        }

        looseClips = looseClips.OrderBy(c => {
            var frames = c["frames"] as int[];
            if (frames == null && c["frames"] is Array arr && arr.Length >= 2)
            {
                return (int)arr.GetValue(0)!;
            }
            return 0;
        }).ToList();
    }

    private static object GetTimeline(JsonObject args)
    {
        var session = GetSession();
        var startFrame = args["startFrame"]?.GetValue<int>();
        var endFrame = args["endFrame"]?.GetValue<int>();
        var captionDetail = args["captionDetail"]?.GetValue<bool>() ?? false;

        var timeline = session.ActiveTimeline;

        var grades = new Dictionary<string, Dictionary<string, object>>();
        foreach (var track in timeline.Tracks)
        {
            foreach (var clip in track.Clips)
            {
                var colorObj = GetColorObject(clip.Effects);
                if (colorObj != null) grades[clip.Id] = colorObj;
            }
        }

        var fold = BuildLinkFoldPlan(timeline);

        var tracksList = new List<object>();
        for (int i = 0; i < timeline.Tracks.Count; i++)
        {
            var track = timeline.Tracks[i];
            
            var compactedClips = new List<Dictionary<string, object>>();
            foreach (var c in track.Clips)
            {
                if (!fold.FoldedAudioIds.Contains(c.Id))
                {
                    compactedClips.Add(GetCompactClip(c, fold, grades));
                }
            }

            ProcessCaptionGroups(
                compactedClips,
                captionDetail,
                startFrame,
                endFrame,
                out var loose,
                out var captionGroups);

            var visible = loose;
            if (startFrame != null || endFrame != null)
            {
                var sf = startFrame ?? 0;
                var ef = endFrame ?? int.MaxValue;
                visible = loose.Where(c => {
                    var frames = c["frames"] as int[];
                    if (frames == null && c["frames"] is Array arr && arr.Length >= 2)
                    {
                        frames = [(int)arr.GetValue(0)!, (int)arr.GetValue(1)!];
                    }
                    if (frames == null) return false;
                    return frames[0] < ef && frames[1] > sf;
                }).ToList();
            }

            var trackDict = new Dictionary<string, object>
            {
                ["index"] = i,
                ["trackId"] = track.Id,
                ["type"] = track.Type.ToString(),
                ["label"] = TrackLabel(timeline, i)
            };

            if (track.Muted) trackDict["muted"] = true;
            if (track.Hidden) trackDict["hidden"] = true;
            if (!track.SyncLocked) trackDict["syncLocked"] = false;
            if (track.Locked) trackDict["locked"] = true;

            if (fold.FoldedCountByTrack.TryGetValue(i, out var fc))
            {
                trackDict["linkedClips"] = fc;
            }

            var gaps = GetTrackGaps(track);
            if (gaps.Count > 0)
            {
                trackDict["gaps"] = gaps;
            }

            if (visible.Count > 0)
            {
                trackDict["clips"] = visible;
            }

            if (visible.Count < loose.Count)
            {
                trackDict["totalClips"] = loose.Count;
            }

            if (captionGroups.Count > 0)
            {
                trackDict["captionGroups"] = captionGroups;
            }

            tracksList.Add(trackDict);
        }

        var payload = new Dictionary<string, object>
        {
            ["id"] = timeline.Id,
            ["name"] = timeline.Name,
            ["fps"] = timeline.Fps,
            ["width"] = timeline.Width,
            ["height"] = timeline.Height,
            ["totalFrames"] = timeline.TotalFrames,
            ["durationSeconds"] = (double)timeline.TotalFrames / Math.Max(1, timeline.Fps),
            ["currentFrame"] = PlayheadFrame(session, timeline),
            ["canGenerate"] = true,
            ["tracks"] = tracksList
        };

        if (startFrame != null || endFrame != null)
        {
            var sf = startFrame ?? 0;
            var ef = Math.Min(endFrame ?? int.MaxValue, timeline.TotalFrames);
            payload["window"] = new[] { sf, ef };
        }

        if (session.ProjectFile.Timelines.Count > 1)
        {
            payload["timelines"] = session.ProjectFile.Timelines.Select(t => new {
                timelineId = t.Id,
                name = t.Name,
                active = t.Id == session.ProjectFile.ActiveTimelineId
            }).ToList();
        }

        return payload;
    }

    private static object CreateTimeline(JsonObject args)
    {
        var session = GetSession();
        var name = args["name"]?.GetValue<string>();
        var from = args["from"]?.GetValue<string>();

        string newId;
        string newName;
        string note;

        if (from != null)
        {
            var source = session.ProjectFile.Timelines.FirstOrDefault(t => t.Id == from);
            if (source == null)
            {
                throw new InvalidOperationException($"No timeline with id '{from}'.");
            }

            var json = JsonSerializer.Serialize(source, JsonOptions.ForProject);
            var clone = JsonSerializer.Deserialize<Timeline>(json, JsonOptions.ForProject)
                ?? throw new InvalidOperationException("Failed to clone timeline");

            clone.Id = Timeline.NewId();
            foreach (var track in clone.Tracks)
            {
                track.Id = Timeline.NewId();
                foreach (var clip in track.Clips)
                {
                    clip.Id = Timeline.NewId();
                }
            }

            newId = clone.Id;
            newName = name ?? $"{source.Name} copy";
            clone.Name = newName;

            note = $"Duplicated \"{source.Name}\" and switched to the copy. Its clip and track ids are new — re-read get_timeline before editing.";
            session.RecordBeforeEdit();
            session.ProjectFile.Timelines.Add(clone);
            session.ProjectFile.ActiveTimelineId = newId;
            if (session.ProjectFile.OpenTimelineIds is { } openDup && !openDup.Contains(newId))
                openDup.Add(newId);
        }
        else
        {
            var active = session.ActiveTimeline;
            var empty = new Timeline
            {
                Id = Timeline.NewId(),
                Name = name ?? $"Timeline {session.ProjectFile.Timelines.Count + 1}",
                Fps = active.Fps,
                Width = active.Width,
                Height = active.Height,
                SettingsConfigured = active.SettingsConfigured,
                Tracks =
                [
                    new Track { Type = ClipType.video },
                    new Track { Type = ClipType.video },
                    new Track { Type = ClipType.audio },
                    new Track { Type = ClipType.audio }
                ]
            };
            newId = empty.Id;
            newName = empty.Name;

            note = "Empty and now active; all edit tools target it.";
            session.RecordBeforeEdit();
            session.ProjectFile.Timelines.Add(empty);
            session.ProjectFile.ActiveTimelineId = newId;
            if (session.ProjectFile.OpenTimelineIds is { } openNew && !openNew.Contains(newId))
                openNew.Add(newId);
        }

        session.MarkDirty();

        return new
        {
            timelineId = newId,
            name = newName,
            active = true,
            note = note
        };
    }

    private static object SetActiveTimeline(JsonObject args)
    {
        var session = GetSession();
        var id = args["timelineId"]?.GetValue<string>() ?? throw new InvalidOperationException("timelineId is required");

        var target = session.ProjectFile.Timelines.FirstOrDefault(t => t.Id == id);
        if (target == null)
        {
            throw new InvalidOperationException($"No timeline with id '{id}'.");
        }

        var note = "Re-read get_timeline — clip and track ids from the previous timeline no longer apply.";
        if (session.ProjectFile.ActiveTimelineId == target.Id)
        {
            note = "Already the active timeline.";
        }
        else
        {
            session.ProjectFile.ActiveTimelineId = target.Id;
            if (session.ProjectFile.OpenTimelineIds is { } open && !open.Contains(target.Id))
                open.Add(target.Id);
            session.MarkDirty();
        }

        return new
        {
            timelineId = target.Id,
            name = target.Name,
            active = true,
            totalFrames = target.TotalFrames,
            fps = target.Fps,
            trackCount = target.Tracks.Count,
            note = note
        };
    }

    private static object SetProjectSettings(JsonObject args)
    {
        var session = GetSession();
        var fps = args["fps"]?.GetValue<int>();
        var width = args["width"]?.GetValue<int>();
        var height = args["height"]?.GetValue<int>();
        var aspectRatio = args["aspectRatio"]?.GetValue<string>();
        var quality = args["quality"]?.GetValue<string>();

        var result = EditCommands.SetProjectSettings(session, fps, width, height, aspectRatio, quality);
        if (!result.Ok)
        {
            throw new InvalidOperationException(result.Error ?? "Failed to set project settings");
        }

        return new
        {
            fps = session.ActiveTimeline.Fps,
            resolution = $"{session.ActiveTimeline.Width}x{session.ActiveTimeline.Height}",
            note = "Settings updated."
        };
    }

    // --- Media implementations ---

    private static object GetMedia(JsonObject args)
    {
        var session = GetSession();
        // Polling is how an agent watches a queued import, so it must not report one stuck pending.
        ResolveInterruptedImports(session);
        var idsToken = args["ids"];
        var idFilter = new HashSet<string>();
        if (idsToken is JsonArray ja)
        {
            foreach (var item in ja)
            {
                if (item != null) idFilter.Add(item.GetValue<string>());
            }
        }
        // Mac: pending means any generationStatus, failures included, so a poll sees how a job ended.
        var pendingOnly = args["pending"]?.GetValue<bool>() == true;

        var assets = new List<object>();
        foreach (var entry in session.Manifest.Entries)
        {
            if (idFilter.Count > 0 && !idFilter.Contains(entry.Id)) continue;
            if (pendingOnly
                && (string.IsNullOrEmpty(entry.GenerationStatus)
                    || entry.GenerationStatus is "none" or DemoGenerationStatus))
                continue;

            var asset = new Dictionary<string, object>
            {
                ["id"] = entry.Id,
                ["name"] = entry.Name,
                ["type"] = entry.Type.ToString().ToLowerInvariant()
            };
            if (entry.Duration > 0) asset["durationSeconds"] = entry.Duration;
            if (entry.SourceWidth != null) asset["width"] = entry.SourceWidth.Value;
            if (entry.SourceHeight != null) asset["height"] = entry.SourceHeight.Value;
            if (entry.SourceFps != null) asset["fps"] = entry.SourceFps.Value;
            if (entry.HasAudio == true) asset["hasAudio"] = true;
            if (entry.FolderId != null) asset["folderId"] = entry.FolderId;
            if (entry.GenerationStatus != null) asset["generationStatus"] = entry.GenerationStatus;
            AddHostedGenerationFields(asset, entry);

            assets.Add(asset);
        }

        var payload = new Dictionary<string, object>
        {
            ["assets"] = assets
        };

        if (idFilter.Count == 0 && !pendingOnly)
        {
            payload["timelines"] = session.ProjectFile.Timelines.Select(t => new
            {
                timelineId = t.Id,
                name = t.Name,
                active = t.Id == session.ProjectFile.ActiveTimelineId,
                durationSeconds = (double)t.TotalFrames / Math.Max(1, t.Fps)
            }).ToList();
        }

        return payload;
    }

    /// <summary>Mac inspect_media sentence cap.</summary>
    private const int InspectSegmentLimit = 400;

    private static ToolCallResult InspectMedia(JsonObject args)
    {
        var mediaRef = args["mediaRef"]?.GetValue<string>() ?? throw new InvalidOperationException("mediaRef is required");
        var wantWords = args["wordTimestamps"]?.GetValue<bool>() == true;
        var maxFrames = Math.Clamp(args["maxFrames"]?.GetValue<int>() ?? 3, 1, 8);
        var windowStartArg = Num(args["startSeconds"]);
        var windowEndArg = Num(args["endSeconds"]);
        var clipId = args["clipId"]?.GetValue<string>();
        var language = args["language"]?.GetValue<string>();

        var snap = ProjectHub.RunSerialized(() =>
        {
            var session = GetSession();
            var entry = session.Manifest.Entries.FirstOrDefault(e => e.Id == mediaRef);
            if (entry == null)
                throw new InvalidOperationException($"Media asset not found: {mediaRef}");

            var meta = new Dictionary<string, object>
            {
                ["id"] = entry.Id,
                ["name"] = entry.Name,
                ["type"] = entry.Type.ToString().ToLowerInvariant(),
                ["durationSeconds"] = entry.Duration,
                ["hasAudio"] = entry.HasAudio ?? false
            };
            if (entry.SourceWidth != null) meta["width"] = entry.SourceWidth.Value;
            if (entry.SourceHeight != null) meta["height"] = entry.SourceHeight.Value;
            if (entry.SourceFps != null) meta["fps"] = entry.SourceFps.Value;

            // WP-399: decided from path text under the gate. Opening an allowed external path to check
            // where it now leads happens below, outside it.
            var blocked = session.Resolver.IsBlocked(entry);
            var path = blocked ? "" : session.Resolver.ResolvePath(entry) ?? "";
            if (blocked)
                meta["externalAccess"] = "notAllowed";
            var online = false;

            var clip = clipId is null
                ? null
                : session.ActiveTimeline.Tracks.SelectMany(t => t.Clips).FirstOrDefault(c => c.Id == clipId);
            if (clipId is not null && clip is null)
                throw new InvalidOperationException($"Clip not found: {clipId}");

            return (meta, path, online, entry.Type, entry.Duration, entry.HasAudio, Clip: clip,
                Fps: session.ActiveTimeline.Fps, Entry: entry, session.Resolver);
        });

        // WP-399: what inspect_media returns goes to the caller, often a hosted model, so an allowed
        // external file must still lead where it did when the user allowed it.
        if (snap.path.Length > 0 && snap.Resolver.ExternalState(snap.Entry) != ExternalMediaState.Allowed)
        {
            snap.path = "";
            snap.meta["externalAccess"] = "targetChanged";
        }
        snap.online = snap.path.Length > 0 && File.Exists(snap.path);
        if (snap.online)
            snap.meta["byteSize"] = new FileInfo(snap.path).Length;
        else
            snap.meta["offline"] = true;
        if (snap.meta.TryGetValue("externalAccess", out var access))
            snap.meta["note"] = (string)access == "notAllowed" ? NotAllowedMessage(mediaRef) : TargetChangedMessage(mediaRef);

        // Transcription blocks (Whisper can run for minutes), so it stays in this method: inspect_media
        // is registered in OffDispatcherTools and McpDispatcherDisciplineTests accounts for
        // InspectMedia by name. Do not move this call into a helper.
        TranscriptionResult? transcript = null;
        string transcriptSource = "";
        string? transcriptError = null;
        if (snap.online && snap.Type is ClipType.video or ClipType.audio && snap.HasAudio != false)
        {
            try
            {
                if (TranscriptDiskCache.TryLoad(snap.path, out var cached) && cached is not null)
                {
                    transcript = cached;
                    transcriptSource = "cache";
                }
                else
                {
                    var provider = GetTranscriptionProvider();
                    transcript = provider.TranscribeAsync(snap.path, language).GetAwaiter().GetResult();
                    transcriptSource = provider.Provider.ToString();
                    try { TranscriptDiskCache.Store(snap.path, transcript); }
                    catch { /* cache write is best-effort */ }
                }
            }
            catch (Exception ex)
            {
                transcriptError = ex.Message;
            }
        }

        AppendInspectTranscription(
            snap.meta, snap.path, snap.online, snap.Type, snap.HasAudio, snap.Clip, snap.Fps,
            wantWords, windowStartArg, windowEndArg, transcript, transcriptSource, transcriptError);

        var content = new List<TextContent> { TextContent.FromText(JsonSerializer.Serialize(snap.meta)) };

        if (snap.online && snap.Type is ClipType.video or ClipType.image)
        {
            var windowStart = Math.Max(0, windowStartArg ?? 0);
            var windowEnd = windowEndArg ?? snap.Duration;

            List<double> timestamps;
            if (snap.Type == ClipType.image || snap.Duration <= 0)
            {
                timestamps = [0.0];
            }
            else
            {
                windowEnd = Math.Clamp(windowEnd, windowStart, snap.Duration);
                var span = Math.Max(0, windowEnd - windowStart);
                var count = span == 0 ? 1 : maxFrames;
                timestamps = Enumerable.Range(0, count)
                    .Select(i => windowStart + span * (i + 0.5) / count)
                    .ToList();
            }

            foreach (var ts in timestamps)
            {
                try
                {
                    var png = FfmpegPngEncoder.ExtractAsync(snap.path, ts).GetAwaiter().GetResult();
                    content.Add(TextContent.FromText($"frame at {ts:F3}s:"));
                    content.Add(TextContent.FromPng(png));
                }
                catch (Exception ex)
                {
                    content.Add(TextContent.FromText($"frame at {ts:F3}s: not decodable ({ex.Message})"));
                }
            }
        }

        return new ToolCallResult { IsError = false, Content = content };
    }

    /// <summary>
    /// Mac inspect_media transcript block: sentence rows for spoken media and cue rows for subtitle
    /// assets, using the same on-device Whisper provider and disk cache as get_transcript.
    /// Times are source seconds, or project frames when a clipId scopes the request.
    /// </summary>
    private static void AppendInspectTranscription(
        Dictionary<string, object> meta,
        string path,
        bool online,
        ClipType type,
        bool? hasAudio,
        Clip? clip,
        int fps,
        bool wantWords,
        double? windowStartArg,
        double? windowEndArg,
        TranscriptionResult? transcript,
        string transcriptSource,
        string? transcriptError)
    {
        if (!online) return;

        if (type == ClipType.subtitle)
        {
            try
            {
                var cues = SubtitleFile.Read(path);
                var rows = cues.Entries
                    .Take(InspectSegmentLimit)
                    .Select(e => new object[] { e.Text, Round3(e.StartSeconds), Round3(e.EndSeconds) })
                    .ToList();
                var block = new Dictionary<string, object>
                {
                    ["timing"] = "source seconds",
                    ["source"] = "subtitle file",
                    ["cues"] = rows,
                };
                if (cues.Entries.Count > rows.Count)
                    block["nextStartSeconds"] = Round3(cues.Entries[rows.Count].StartSeconds);
                meta["transcription"] = block;
            }
            catch (Exception ex)
            {
                meta["transcription"] = new Dictionary<string, object> { ["error"] = ex.Message };
            }
            return;
        }

        if (type is not (ClipType.video or ClipType.audio)) return;
        if (hasAudio == false)
        {
            meta["transcription"] = new Dictionary<string, object>
            {
                ["error"] = "media has no audio stream",
            };
            return;
        }
        if (transcriptError is not null)
        {
            meta["transcription"] = new Dictionary<string, object> { ["error"] = transcriptError };
            return;
        }
        if (transcript is null) return;
        var source = transcriptSource;

        // A clipId scopes the request to what that clip actually shows, and reports project frames.
        var speed = clip is null ? 1 : Math.Max(clip.Speed, 0.0001);
        var visibleStart = clip is null ? 0 : clip.TrimStartFrame / (double)fps;
        var visibleEnd = clip is null
            ? double.MaxValue
            : visibleStart + clip.DurationFrames * speed / fps;

        var windowStart = Math.Max(windowStartArg ?? 0, visibleStart);
        var windowEnd = Math.Min(windowEndArg ?? double.MaxValue, visibleEnd);

        object TimeValue(double seconds)
        {
            if (clip is null) return Round3(seconds);
            var frame = (int)Math.Round(
                clip.StartFrame + (seconds * fps - clip.TrimStartFrame) / speed,
                MidpointRounding.AwayFromZero);
            return Math.Clamp(frame, clip.StartFrame, clip.EndFrame);
        }

        var segments = transcript.Segments
            .Where(seg => seg.End > windowStart && seg.Start < windowEnd)
            .Where(seg => TranscriptionSpecials.StripSpecialTokens(seg.Text).Length > 0)
            .ToList();
        var rowsOut = segments
            .Take(InspectSegmentLimit)
            .Select(seg => new object[]
            {
                TranscriptionSpecials.StripSpecialTokens(seg.Text),
                TimeValue(seg.Start),
                TimeValue(seg.End),
            })
            .ToList();

        var payload = new Dictionary<string, object>
        {
            ["timing"] = clip is null ? "source seconds" : "project frames",
            ["source"] = source,
            ["segments"] = rowsOut,
        };
        if (segments.Count > rowsOut.Count)
            payload["nextStartSeconds"] = Round3(segments[rowsOut.Count].Start);

        if (wantWords)
        {
            var words = transcript.Words
                .Where(w => w.Start is { } ws && w.End is { } we && we > windowStart && ws < windowEnd)
                .Where(w => !TranscriptionSpecials.IsSpecialToken(w.Text))
                .Take(TranscriptWordLimit)
                .Select(w => new object[] { w.Text, TimeValue(w.Start!.Value), TimeValue(w.End!.Value) })
                .ToList();
            payload["words"] = words;
        }

        meta["transcription"] = payload;
    }

    private static object ImportMedia(JsonObject args)
    {
        var session = GetSession();
        string? downloadedSource = null;
        try
        {
            // Accept the advertised source={path|url|bytes|matte} shape and the legacy top-level path.
            var path = args["path"]?.GetValue<string>();
            var folderId = args["folderId"]?.GetValue<string>();
            if (args["folder"]?.GetValue<string>() is { } folderPath)
                folderId = ResolveFolderId(session, folderPath);
            var targetName = args["targetName"]?.GetValue<string>() ?? args["name"]?.GetValue<string>();
            if (args["source"] is JsonObject source)
            {
                if (!string.IsNullOrWhiteSpace(path))
                    throw new InvalidOperationException("set either legacy path or source, not both");

                var sourceKinds = new[] { source["path"], source["url"], source["bytes"], source["matte"] }
                    .Count(value => value is not null);
                if (sourceKinds != 1)
                    throw new InvalidOperationException($"source must set exactly one of path, url, bytes, or matte (got {sourceKinds})");
                path = source["path"]?.GetValue<string>();
                if (path is null && source["url"]?.GetValue<string>() is { } url)
                {
                    var mimeType = source["mimeType"]?.GetValue<string>();
                    if (TryQueueUrlImport(session, url, mimeType, targetName, folderId) is { } queued)
                        return queued;
                    downloadedSource = DownloadImportSource(url, mimeType);
                    path = downloadedSource;
                }
                if (path is null && source["bytes"]?.GetValue<string>() is { } base64)
                {
                    return ReadyImportResponse(ProjectHub.RunSerialized(() =>
                    {
                        var result = MediaLibraryCommands.ImportBase64(
                            session,
                            base64,
                            source["mimeType"]?.GetValue<string>() ?? string.Empty,
                            targetName,
                            folderId,
                            ValidateAndProbeImportedMediaMetadata,
                            out var entry);
                        if (!result.Ok || entry is null)
                            throw new InvalidOperationException(result.Error ?? "source.bytes import failed");
                        return entry;
                    }));
                }
                if (path is null && source["matte"] is JsonObject matte)
                {
                    return ReadyImportResponse(ProjectHub.RunSerialized(() =>
                    {
                        var result = MediaLibraryCommands.CreateMatte(
                            session,
                            matte["hex"]?.GetValue<string>() ?? string.Empty,
                            matte["aspectRatio"]?.GetValue<string>(),
                            targetName,
                            folderId,
                            out var entry);
                        if (!result.Ok || entry is null)
                            throw new InvalidOperationException(result.Error ?? "source.matte import failed");
                        return entry;
                    }));
                }
                if (path is null)
                {
                    throw new InvalidOperationException(
                        "source must set exactly one supported path, url, bytes, or matte value.");
                }
            }
            if (string.IsNullOrWhiteSpace(path))
                throw new InvalidOperationException("path (or source.path / source.url) is required");

            var resolvedPath = ProjectHub.ResolvePath(path);
            var reference = ShouldReferenceInPlace(args["source"] as JsonObject, downloadedSource);
            // A reference moves no bytes, and a download already landed; only a big copy can queue.
            if (!reference
                && downloadedSource is null
                && TryQueueCopyImport(session, resolvedPath, targetName, folderId) is { } queuedCopy)
                return queuedCopy;
            return ReadyImportResponse(ImportMediaFileEntry(session, resolvedPath, targetName, folderId, reference));
        }
        finally
        {
            DeleteDownloadedImportSource(downloadedSource);
        }
    }

    private static MediaManifestEntry ImportMediaFileEntry(
        ProjectSession session,
        string sourcePath,
        string? targetName,
        string? folderId,
        bool referenceInPlace = false,
        CancellationToken cancellationToken = default)
    {
        var snapshot = ProjectHub.RunSerialized(() => (
            PackagePath: session.PackagePath,
            Unreadable: session.ManifestUnreadable,
            FolderExists: MediaLibraryCommands.FolderExistsOrRoot(session.Manifest, folderId)));

        var preparedResult = MediaLibraryCommands.PrepareImportedFile(
            snapshot.PackagePath,
            snapshot.Unreadable,
            snapshot.FolderExists,
            sourcePath,
            targetName,
            folderId,
            ValidateAndProbeImportedMediaMetadata,
            out var prepared,
            referenceInPlace,
            cancellationToken);
        if (!preparedResult.Ok || prepared is null)
            throw new InvalidOperationException(preparedResult.Error ?? "source.path import failed");

        return ProjectHub.RunSerialized(() =>
        {
            var commit = MediaLibraryCommands.CommitImportedFile(session, prepared.Value);
            if (!commit.Ok)
                throw new InvalidOperationException(commit.Error ?? "source.path import failed");
            return prepared.Value.Entry;
        });
    }

    private static void ValidateAndProbeImportedMediaMetadata(MediaManifestEntry entry, string sourcePath)
    {
        if (entry.Type == ClipType.lottie)
        {
            ValidateAndApplyLottieMetadata(entry, sourcePath);
            return;
        }

        if (entry.Type == ClipType.subtitle)
        {
            // Subtitles carry no stream for ffprobe to read; the cues themselves are the validation.
            var cues = SubtitleFile.Read(sourcePath);
            entry.Duration = SubtitleFile.DurationSeconds(cues.Entries);
            entry.SourceWidth = null;
            entry.SourceHeight = null;
            entry.SourceFps = null;
            entry.HasAudio = false;
            return;
        }

        MediaProbeResult meta;
        try
        {
            meta = new FfmpegMediaProbe().ProbeAsync(sourcePath).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            throw new InvalidDataException($"media validation failed for '{Path.GetFileName(sourcePath)}': {ex.Message}", ex);
        }

        if (entry.Type == ClipType.video && (!meta.HasVideo || meta.DurationSeconds <= 0))
            throw new InvalidDataException("media validation failed: video stream or positive duration is missing");
        if (entry.Type == ClipType.audio && (!meta.HasAudio || meta.DurationSeconds <= 0))
            throw new InvalidDataException("media validation failed: audio stream or positive duration is missing");
        if (entry.Type == ClipType.image && (!meta.HasVideo || meta.Width is not > 0 || meta.Height is not > 0))
            throw new InvalidDataException("media validation failed: decodable image dimensions are missing");

        entry.Duration = entry.Type == ClipType.image ? 0 : meta.DurationSeconds;
        entry.SourceWidth = entry.Type == ClipType.audio ? null : meta.Width;
        entry.SourceHeight = entry.Type == ClipType.audio ? null : meta.Height;
        entry.SourceFps = entry.Type == ClipType.video ? meta.Fps : null;
        entry.HasAudio = meta.HasAudio;
    }

    private static void ValidateAndApplyLottieMetadata(MediaManifestEntry entry, string sourcePath)
    {
        using var document = ReadLottieDocument(sourcePath);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("layers", out var layers)
            || layers.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("media validation failed: Lottie JSON has no layers array");
        }

        var width = RequiredPositiveInt(root, "w");
        var height = RequiredPositiveInt(root, "h");
        var fps = RequiredPositiveDouble(root, "fr");
        var firstFrame = RequiredDouble(root, "ip");
        var lastFrame = RequiredDouble(root, "op");
        if (lastFrame <= firstFrame)
            throw new InvalidDataException("media validation failed: Lottie frame range is invalid");

        entry.Duration = (lastFrame - firstFrame) / fps;
        entry.SourceWidth = width;
        entry.SourceHeight = height;
        entry.SourceFps = fps;
        entry.HasAudio = false;
    }

    private static JsonDocument ReadLottieDocument(string sourcePath)
    {
        try
        {
            if (!string.Equals(Path.GetExtension(sourcePath), ".lottie", StringComparison.OrdinalIgnoreCase))
            {
                if (new FileInfo(sourcePath).Length > MaxLottieJsonBytes)
                    throw new InvalidDataException("media validation failed: Lottie JSON exceeds the 100 MiB limit");
                return JsonDocument.Parse(File.ReadAllBytes(sourcePath));
            }

            using var archive = ZipFile.OpenRead(sourcePath);
            var animation = archive.Entries.FirstOrDefault(candidate =>
                    candidate.FullName.StartsWith("animations/", StringComparison.OrdinalIgnoreCase)
                    && candidate.FullName.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidDataException("media validation failed: .lottie archive has no animation JSON");
            if (animation.Length > MaxLottieJsonBytes)
                throw new InvalidDataException("media validation failed: Lottie animation JSON exceeds the 100 MiB limit");
            using var stream = animation.Open();
            return JsonDocument.Parse(stream);
        }
        catch (InvalidDataException)
        {
            throw;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            throw new InvalidDataException($"media validation failed: invalid Lottie file ({ex.Message})", ex);
        }
    }

    private static int RequiredPositiveInt(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var property)
            || !property.TryGetInt32(out var value)
            || value <= 0)
            throw new InvalidDataException($"media validation failed: Lottie '{name}' must be a positive integer");
        return value;
    }

    private static double RequiredPositiveDouble(JsonElement root, string name)
    {
        var value = RequiredDouble(root, name);
        if (!double.IsFinite(value) || value <= 0)
            throw new InvalidDataException($"media validation failed: Lottie '{name}' must be positive");
        return value;
    }

    private static double RequiredDouble(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var property) || !property.TryGetDouble(out var value))
            throw new InvalidDataException($"media validation failed: Lottie '{name}' is required");
        return value;
    }

    private static readonly HttpClient ImportHttpClient =
        RemoteMediaDownloader.CreateHttpClient(TimeSpan.FromMinutes(5));
    private const long RemoteImportMaxBytes = 5L * 1024 * 1024 * 1024;

    /// <summary>
    /// Downloads an import source URL to a uniquely named temp directory, keeping a clean media
    /// filename (from the URL path, else the Content-Type) so the package copy is readable.
    /// </summary>
    private static string DownloadImportSource(string url, string? mimeOverride)
    {
        var uri = ValidateImportUrl(url);

        var tempDir = Path.Combine(Path.GetTempPath(), "temno-import-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(tempDir);
            var partialPath = Path.Combine(tempDir, "payload.partial");
            var download = DownloadImportAsync(uri, partialPath, CancellationToken.None)
                .GetAwaiter().GetResult();
            var filename = Path.GetFileName(download.FinalUri.LocalPath);
            string? forcedExtension = null;
            if (!string.IsNullOrWhiteSpace(mimeOverride))
            {
                if (!MediaLibraryCommands.TryGetMimeType(mimeOverride, out var overrideExtension, out _))
                    throw new InvalidOperationException($"import_media: unsupported source.mimeType '{mimeOverride}'");
                forcedExtension = "." + overrideExtension;
            }

            if (string.IsNullOrWhiteSpace(forcedExtension) && string.IsNullOrWhiteSpace(Path.GetExtension(filename)))
            {
                if (!MediaLibraryCommands.TryGetMimeType(
                        download.ContentType,
                        out var responseExtension,
                        out _))
                    throw new InvalidOperationException(
                        $"import_media: cannot determine a media file type for {url} (no file extension and unrecognized Content-Type '{download.ContentType}').");
                forcedExtension = "." + responseExtension;
            }

            filename = MakeSafeDownloadFilename(filename, forcedExtension);
            var tempPath = Path.Combine(tempDir, filename);
            File.Move(partialPath, tempPath);
            return tempPath;
        }
        catch
        {
            try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true); }
            catch { }
            throw;
        }
    }

    private static string MakeSafeDownloadFilename(string filename, string? forcedExtension)
    {
        var stem = Path.GetFileNameWithoutExtension(filename);
        if (string.IsNullOrWhiteSpace(stem)) stem = "download";
        var invalid = Path.GetInvalidFileNameChars();
        stem = new string(stem.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
        if (stem.Length == 0) stem = "download";
        var extension = forcedExtension ?? Path.GetExtension(filename).ToLowerInvariant();
        return stem + extension;
    }

    /// <summary>
    /// WP-160: reference-in-place is only meaningful for a file the user already owns.
    /// </summary>
    /// <remarks>
    /// <c>bytes</c> and <c>matte</c> return before this is reached, but <c>url</c> does not: a
    /// download sets <c>path</c> to a temp file under <c>temno-import-*</c>, and
    /// <see cref="DeleteDownloadedImportSource"/> removes that directory in the enclosing
    /// <c>finally</c>. Honouring the flag there wrote <c>external.absolutePath</c> pointing at a file
    /// the same call was about to delete, and reported success — the WP-154 failure shape, in the
    /// import path. Found by the WP-160 T1 spot-check (Grok 4.6 xhigh, 2026-09-04); the contracts
    /// already said the flag is ignored for <c>url</c>, so this makes the code match them rather than
    /// changing what they promise.
    /// </remarks>
    /// <param name="downloadedSource">
    /// Non-null when this import downloaded its own temp file, which is exactly when the flag must
    /// not be honoured.
    /// </param>
    public static bool ShouldReferenceInPlace(JsonObject? source, string? downloadedSource)
        => downloadedSource is null && source?["reference"]?.GetValue<bool>() == true;

    private static void DeleteDownloadedImportSource(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        try
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(path));
            if (directory is null
                || !Path.GetFileName(directory).StartsWith("temno-import-", StringComparison.Ordinal))
                return;
            Directory.Delete(directory, recursive: true);
        }
        catch
        {
            // Best-effort cleanup must not turn a completed import into an error.
        }
    }

    private static object SearchMedia(JsonObject args)
    {
        var query = args["query"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(query))
            throw new InvalidOperationException("query is required");

        var scope = args["scope"]?.GetValue<string>()?.Trim().ToLowerInvariant() ?? "both";
        if (scope is not "visual" and not "spoken" and not "both")
            throw new InvalidOperationException("scope must be visual, spoken, or both");

        var mediaRef = args["mediaRef"]?.GetValue<string>();
        var limit = Math.Clamp(args["limit"]?.GetValue<int>() ?? 20, 1, 100);

        var snapshot = ProjectHub.RunSerialized(() =>
        {
            var session = GetSession();
            var assets = session.Manifest.Entries
                .Select(entry => new VisualSearchAsset(
                    entry.Id,
                    string.IsNullOrWhiteSpace(entry.Name) ? entry.Id : entry.Name,
                    entry.Type,
                    session.Resolver.ResolvePath(entry),
                    entry.Duration))
                .ToList();
            var cacheScope = GetVisualSearchCacheScope(session);
            var spokenCandidates = session.Manifest.Entries
                .Where(e => e.Type is ClipType.video or ClipType.audio)
                .Where(e => mediaRef is null || e.Id == mediaRef)
                .Select(e => (
                    Id: e.Id,
                    Name: string.IsNullOrWhiteSpace(e.Name) ? e.Id : e.Name,
                    Path: session.Resolver.ResolvePath(e)))
                .ToList();
            var external = session.Manifest.Entries.Where(e => e.Source is MediaSource.ExternalSource).ToList();
            return (assets, cacheScope, spokenCandidates, external, session.Resolver);
        });

        // WP-399: search returns what the files show and say, so an allowed external file must still lead
        // where it did. Checked here, outside the session gate, once per file; a file that fails is left out.
        var transfer = snapshot.Resolver.ForTransfer();
        var refused = snapshot.external
            .Where(e => transfer.ResolvePath(e) is null)
            .Select(e => e.Id)
            .ToHashSet(StringComparer.Ordinal);
        var assets = snapshot.assets
            .Select(a => refused.Contains(a.MediaRef) ? a with { SourcePath = null } : a)
            .ToList();
        var cacheScope = snapshot.cacheScope;
        var spokenCandidates = snapshot.spokenCandidates
            .Select(c => refused.Contains(c.Id) ? (c.Id, c.Name, Path: (string?)null) : c)
            .ToList();

        VisualSearchResponse? visual = null;
        if (scope is "visual" or "both")
        {
            var request = new VisualSearchRequest(cacheScope, assets, query, mediaRef, limit);
            lock (_visualSearchLock)
            {
                visual = GetVisualSearchService()
                    .SearchAsync(request, CancellationToken.None)
                    .GetAwaiter()
                    .GetResult();
            }
        }

        IReadOnlyList<TranscriptSearch.Hit> spokenHits = [];
        var spokenStatus = "not_requested";
        var spokenCachedAssets = 0;
        var spokenSkipped = 0;
        var spokenNames = spokenCandidates.ToDictionary(c => c.Id, c => c.Name, StringComparer.Ordinal);
        if (scope is "spoken" or "both")
        {
            spokenStatus = "ok";
            var loaded = new List<(string MediaRef, TranscriptionResult Transcript)>();
            foreach (var c in spokenCandidates)
            {
                if (string.IsNullOrWhiteSpace(c.Path) || !File.Exists(c.Path))
                {
                    spokenSkipped++;
                    continue;
                }
                if (!TranscriptDiskCache.TryLoad(c.Path, out var tr) || tr is null)
                {
                    spokenSkipped++;
                    continue;
                }
                spokenCachedAssets++;
                loaded.Add((c.Id, tr));
            }

            spokenHits = TranscriptSearch.Search(query, loaded, limit);
        }

        var spokenRows = spokenHits.Select(h => (object)new
        {
            mediaRef = h.MediaRef,
            name = spokenNames.GetValueOrDefault(h.MediaRef) ?? h.MediaRef,
            startSeconds = Math.Round(h.StartSeconds, 3),
            endSeconds = Math.Round(h.EndSeconds, 3),
            text = h.Text,
        }).ToList();

        var visualResults = visual?.Results.Select(hit => (object)new
        {
            mediaRef = hit.MediaRef,
            name = hit.Name,
            timestampSeconds = Math.Round(hit.TimestampSeconds, 3),
            startSeconds = Math.Round(hit.TimestampSeconds, 3),
            score = Math.Round(hit.Score, 4),
            meanColor = hit.MeanColorHex,
        }).ToList() ?? [];

        // Flattened results for Media-panel filters: visual first, then spoken (dedupe mediaRef).
        var flat = new List<object>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        if (visual is not null)
        {
            foreach (var hit in visual.Results)
            {
                if (!seen.Add(hit.MediaRef)) continue;
                flat.Add(new
                {
                    mediaRef = hit.MediaRef,
                    name = hit.Name,
                    timestampSeconds = Math.Round(hit.TimestampSeconds, 3),
                    score = Math.Round(hit.Score, 4),
                    meanColor = hit.MeanColorHex,
                    source = "visual",
                });
            }
        }
        foreach (var h in spokenHits)
        {
            if (!seen.Add(h.MediaRef)) continue;
            flat.Add(new
            {
                mediaRef = h.MediaRef,
                name = spokenNames.GetValueOrDefault(h.MediaRef) ?? h.MediaRef,
                timestampSeconds = Math.Round(h.StartSeconds, 3),
                score = 1.0,
                text = h.Text,
                source = "spoken",
            });
        }

        var visualStatus = visual?.Status ?? "not_requested";
        string status;
        string message;
        if (scope == "visual")
        {
            status = visualStatus;
            message = visual?.Message ?? "";
        }
        else if (scope == "spoken")
        {
            status = spokenHits.Count > 0 ? "ok" : "no_results";
            message = spokenHits.Count > 0
                ? "Spoken keyword search over cached transcripts (on-device)."
                : spokenCachedAssets == 0
                    ? "No cached transcripts yet. Run get_transcript or add_captions first, then search again."
                    : "No spoken segments matched the query.";
        }
        else
        {
            status = (visual?.Results.Count > 0 || spokenHits.Count > 0) ? "ok"
                : visualStatus is "unsupported_query" or "model_not_installed" ? visualStatus
                : "no_results";
            if (visualStatus is "ok" && spokenHits.Count == 0 && spokenCachedAssets == 0)
                status = "partial";
            message = string.Join(" ",
                visual?.Message ?? "",
                spokenHits.Count > 0
                    ? $"Spoken: {spokenHits.Count} hit(s)."
                    : spokenCachedAssets == 0
                        ? "Spoken: no cached transcripts yet."
                        : "Spoken: no keyword matches.").Trim();
        }

        return new
        {
            status,
            scope,
            visualStatus,
            spokenStatus,
            localOnly = true,
            queryGrammar = VisualSearchQueryParser.SupportedGrammar,
            queryKind = visual?.QueryKind ?? (scope == "spoken" ? "spoken" : null),
            moments = visualResults,
            spoken = spokenRows,
            results = flat.Count > 0 ? flat : visualResults,
            index = new
            {
                status = visual?.IndexStatus ?? (scope == "spoken" ? "spoken-only" : "ready"),
                mode = visual?.IndexMode,
                model = visual?.EmbedderModelId,
                indexedAssets = visual?.IndexedAssets ?? 0,
                cachedAssets = visual?.CachedAssets ?? 0,
                invalidatedAssets = visual?.InvalidatedAssets ?? 0,
                skippedOffline = visual?.SkippedOffline ?? 0,
                skippedNonVisual = visual?.SkippedNonVisual ?? 0,
                skippedUnreadable = visual?.SkippedUnreadable ?? 0,
                cachePersisted = visual?.CachePersisted ?? false,
                spokenCachedAssets,
                spokenSkippedUncached = spokenSkipped,
            },
            message,
        };
    }

    private static string GetVisualSearchCacheScope(ProjectSession session)
    {
        if (!string.IsNullOrWhiteSpace(session.PackagePath))
            return "project:" + Path.GetFullPath(session.PackagePath);

        var sources = session.Manifest.Entries
            .OrderBy(entry => entry.Id, StringComparer.Ordinal)
            .Select(entry => string.Join("\u001f", entry.Id, entry.Type, session.Resolver.ResolvePath(entry) ?? ""));
        return "unsaved:" + string.Join("\u001e", sources);
    }

    // --- Clips implementations ---

    // ===== Wave A: color / effects / keyframes / transitions / export wiring =====

    private static object ApplyColorTool(JsonObject args)
    {
        var session = GetSession();
        var clipIds = ParseStringArray(args["clipIds"]);
        if (clipIds.Count == 0) throw new InvalidOperationException("clipIds is required");

        var colorArgs = BuildColorArgs(args);
        if (args["color"] is JsonObject colorObj)
        {
            // Full-grade paste (usually copied from get_timeline's color object). GradeState's
            // dictionary ctor natively reads JsonElement values.
            colorArgs.Color = JsonSerializer.Deserialize<Dictionary<string, object>>(colorObj.ToJsonString());
        }

        var snapshot = TakeTimelineSnapshot(session.ActiveTimeline);
        var result = EditCommands.ApplyColor(session, clipIds, colorArgs);
        if (!result.Ok) throw new InvalidOperationException(result.Error ?? "apply_color failed");
        return GetMutationResult(session, snapshot);
    }

    private static ColorArgs BuildColorArgs(JsonObject a) => new()
    {
        Reset = a["reset"]?.GetValue<bool>(),
        Exposure = Num(a["exposure"]),
        Contrast = Num(a["contrast"]),
        Saturation = Num(a["saturation"]),
        Vibrance = Num(a["vibrance"]),
        Temperature = Num(a["temperature"]),
        Tint = Num(a["tint"]),
        Highlights = Num(a["highlights"]),
        Shadows = Num(a["shadows"]),
        Blacks = Num(a["blacks"]),
        Whites = Num(a["whites"]),
        ShadowsHue = Num(a["shadowsHue"]),
        ShadowsAmount = Num(a["shadowsAmount"]),
        ShadowsLum = Num(a["shadowsLum"]),
        MidsHue = Num(a["midsHue"]),
        MidsAmount = Num(a["midsAmount"]),
        MidsGamma = Num(a["midsGamma"]),
        HighsHue = Num(a["highsHue"]),
        HighsAmount = Num(a["highsAmount"]),
        HighsGain = Num(a["highsGain"]),
        MasterCurve = ParseCurveArray(a["masterCurve"]),
        RedCurve = ParseCurveArray(a["redCurve"]),
        GreenCurve = ParseCurveArray(a["greenCurve"]),
        BlueCurve = ParseCurveArray(a["blueCurve"]),
        HueCurves = ParseHueTargets(a["hueCurves"]),
        Lut = ParseLut(a["lut"]),
    };

    private static object ApplyEffectTool(JsonObject args)
    {
        var session = GetSession();
        var clipIds = ParseStringArray(args["clipIds"]);
        if (clipIds.Count == 0) throw new InvalidOperationException("clipIds is required");

        var adds = new List<EffectEntry>();
        if (args["effects"] is JsonArray effArr)
        {
            foreach (var e in effArr)
            {
                if (e is not JsonObject o) continue;
                var type = o["type"]?.GetValue<string>();
                if (string.IsNullOrEmpty(type)) throw new InvalidOperationException("each effect requires a type");
                Dictionary<string, double>? prms = null;
                if (o["params"] is JsonObject po)
                {
                    prms = new Dictionary<string, double>();
                    foreach (var kv in po)
                        if (Num(kv.Value) is { } val) prms[kv.Key] = val;
                }
                adds.Add(new EffectEntry
                {
                    Type = type,
                    Params = prms,
                    Enabled = o["enabled"]?.GetValue<bool>(),
                    Position = ReadOptionalInt(o["position"], "effect position"),
                });
            }
        }
        var removes = ParseStringArray(args["remove"]);

        var snapshot = TakeTimelineSnapshot(session.ActiveTimeline);
        var result = EditCommands.ApplyEffect(session, clipIds, adds, removes);
        if (!result.Ok) throw new InvalidOperationException(result.Error ?? "apply_effect failed");
        return GetMutationResult(session, snapshot);
    }

    private static object SetKeyframesTool(JsonObject args)
    {
        var session = GetSession();
        var clipId = args["clipId"]?.GetValue<string>() ?? throw new InvalidOperationException("clipId is required");
        var property = args["property"]?.GetValue<string>() ?? throw new InvalidOperationException("property is required");

        var rows = new List<object>();
        if (args["keyframes"] is JsonArray kfArr)
        {
            foreach (var row in kfArr)
            {
                if (row is not JsonArray cols) continue;
                var inner = new List<object>();
                foreach (var c in cols)
                {
                    // Num() covers int/long/double-backed JSON values (TryGetValue<long> alone misses
                    // int-backed ones); a non-numeric cell (e.g. an interpolation name) stays a string.
                    if (Num(c) is { } num) inner.Add(num);
                    else if (c is JsonValue v && v.TryGetValue<string>(out var s)) inner.Add(s);
                }
                rows.Add(inner);
            }
        }

        var snapshot = TakeTimelineSnapshot(session.ActiveTimeline);
        var result = EditCommands.SetKeyframes(session, clipId, property, rows);
        if (!result.Ok) throw new InvalidOperationException(result.Error ?? "set_keyframes failed");
        return GetMutationResult(session, snapshot);
    }

    private static object AddTransitionTool(JsonObject args)
    {
        var session = GetSession();
        var a = args["clipIdA"]?.GetValue<string>() ?? throw new InvalidOperationException("clipIdA is required");
        var b = args["clipIdB"]?.GetValue<string>() ?? throw new InvalidOperationException("clipIdB is required");
        var duration = args["durationFrames"]?.GetValue<int>() ?? throw new InvalidOperationException("durationFrames is required");
        var type = args["type"]?.GetValue<string>();

        var snapshot = TakeTimelineSnapshot(session.ActiveTimeline);
        var result = EditCommands.AddTransition(session, a, b, type, duration);
        if (!result.Ok) throw new InvalidOperationException(result.Error ?? "add_transition failed");
        return GetMutationResult(session, snapshot);
    }

    private static object RemoveTransitionTool(JsonObject args)
    {
        var session = GetSession();
        var id = args["transitionId"]?.GetValue<string>() ?? throw new InvalidOperationException("transitionId is required");

        var snapshot = TakeTimelineSnapshot(session.ActiveTimeline);
        var result = EditCommands.RemoveTransition(session, id);
        if (!result.Ok) throw new InvalidOperationException(result.Error ?? "remove_transition failed");
        return GetMutationResult(session, snapshot);
    }

    private static object ListTransitionsTool(JsonObject args)
    {
        var session = GetSession();
        var transitions = EditCommands.ListTransitions(session).Select(t => new
        {
            id = t.Id,
            clipIdA = t.ClipIdA,
            clipIdB = t.ClipIdB,
            type = char.ToLowerInvariant(t.Type.ToString()[0]) + t.Type.ToString()[1..],
            durationFrames = t.DurationFrames,
        }).ToList();
        return new { transitions };
    }

    private static object ExportProject(JsonObject args)
    {
        var session = GetSession();
        var mode = (args["mode"]?.GetValue<string>() ?? "video").Trim().ToLowerInvariant();
        if (mode is not ("video" or "xml" or "fcpxml" or "palmier"))
            throw new InvalidOperationException($"Unknown export mode '{mode}'. Use video, fcpxml, xml, or palmier.");
        var allowOfflineMedia = args["allowOfflineMedia"]?.GetValue<bool>() ?? false;
        if (allowOfflineMedia && mode is "xml" or "fcpxml" or "palmier")
            throw new InvalidOperationException(
                "export_project: allowOfflineMedia applies only to video mode; xml, fcpxml and palmier exports do not refuse offline media.");
        var outputPath = args["outputPath"]?.GetValue<string>();
        var omittedPath = string.IsNullOrWhiteSpace(outputPath);
        // WP-398: an omitted outputPath never replaces anything, so it is judged as overwrite=false.
        var overwrite = !omittedPath && (args["overwrite"]?.GetValue<bool>() ?? true);
        if (omittedPath)
        {
            outputPath = DefaultExportPath(session, mode);
            if (mode == "video")
                outputPath = ExportContainer.CoerceExtension(ParseCodec(args["codec"]?.GetValue<string>()), outputPath);
        }

        // Every call holds its destination here until it returns, so two calls never pick or write one
        // destination at once. A video export has been handed to the export queue by then, whose
        // reservation covers it from there to the end of the render.
        lock (InFlightDestinations)
        {
            if (omittedPath)
                outputPath = UniqueExportPath(outputPath!);
            outputPath = SettleExportDestination(mode, ProjectHub.ResolvePath(outputPath!), overwrite, session.PackagePath);
            // WP-403: a pending video export publishes to its destination when it finishes, so a file
            // written there now would be replaced or would make that export fail. overwrite does not
            // change that. Video mode is refused by the queue itself.
            if (mode != "video" && HeldByPendingExport(outputPath))
            {
                throw new InvalidOperationException(
                    $"outputPath is the destination of a video export that is waiting or in progress: {outputPath}. "
                    + "Choose another path, or wait for it or cancel it with manage_exports.");
            }
            if (!InFlightDestinations.Add(outputPath))
                throw new InvalidOperationException($"An export to \"{Path.GetFileName(outputPath)}\" is already in progress.");
        }

        try
        {
            return ExportProjectTo(args, session, mode, allowOfflineMedia, outputPath, omittedPath, overwrite);
        }
        finally
        {
            lock (InFlightDestinations)
                InFlightDestinations.Remove(outputPath);
        }
    }

    /// <summary>WP-398: destinations an export_project call is settling or writing right now.</summary>
    private static readonly HashSet<string> InFlightDestinations = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// WP-403: true when a waiting or rendering export will publish to <paramref name="destination"/>
    /// under any spelling: the same file name in the same folder, compared as WP-398 compares folders
    /// (path text, resolved path, and the file system's identity of an existing folder), so an extended
    /// <c>\\?\</c> path, a junction or a loopback share does not get past it.
    /// </summary>
    private static bool HeldByPendingExport(string destination)
    {
        if (_exportQueue.IsDestinationReserved(destination))
            return true;
        var name = Path.GetFileName(destination);
        var folder = Path.GetDirectoryName(Path.GetFullPath(destination));
        if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(folder))
            return false;
        return _exportQueue.Jobs.Any(job =>
            job.State.IsPending()
            && string.Equals(Path.GetFileName(job.OutputPath), name, StringComparison.OrdinalIgnoreCase)
            && Path.GetDirectoryName(Path.GetFullPath(job.OutputPath)) is { Length: > 0 } jobFolder
            && ExportDestination.IsWithin(jobFolder, folder)
            && ExportDestination.IsWithin(folder, jobFolder));
    }

    private static object ExportProjectTo(
        JsonObject args,
        ProjectSession session,
        string mode,
        bool allowOfflineMedia,
        string outputPath,
        bool omittedPath,
        bool overwrite)
    {

        var timelineId = args["timelineId"]?.GetValue<string>();
        // export_project runs off the dispatcher (see OffDispatcherTools), so serialize the short
        // read of project state rather than the whole render.
        var timeline = ProjectHub.RunSerialized(() => timelineId != null
            ? session.ProjectFile.Timelines.FirstOrDefault(t => t.Id == timelineId)
                ?? throw new InvalidOperationException($"Timeline not found: {timelineId}")
            : session.ActiveTimeline);

        switch (mode)
        {
            case "video":
            {
                if (timeline.TotalFrames <= 0)
                    throw new InvalidOperationException("Timeline has no content to export.");
                var codec = ParseCodec(args["codec"]?.GetValue<string>());
                var resolution = ParseResolution(args["resolution"]?.GetValue<string>());
                var preset = new ExportPreset("export", codec, resolution)
                {
                    MaxRenderQuality = args["maxRenderQuality"]?.GetValue<bool>() ?? false,
                    MaxBitDepth = args["maxBitDepth"]?.GetValue<bool>() ?? false,
                };
                // preferHardware arg overrides the saved pref; default comes from user preferences.
                var preferHardware = args["preferHardware"]?.GetValue<bool>()
                    ?? UserPreferences.Load().ResolvePreferHardwareEncode();
                // WP-215 R1: one selection seam for the compositor and the renderer beneath it.
                var acceleration = preferHardware
                    ? HardwareAccelerationMode.PreferHardware
                    : HardwareAccelerationMode.Software;

                // Before the exporter exists: it owns a D3D11 device, and a throw after it is built
                // would leave that device alive.
                if (_exportQueue.IsDestinationReserved(outputPath))
                    throw new InvalidOperationException($"An export to \"{Path.GetFileName(outputPath)}\" is already waiting or in progress.");
                // WP-170: the editor runs this preflight before it asks; an agent that opted in is
                // told the same list, with where each file is used, instead of being asked.
                var preflight = allowOfflineMedia ? RunExportPreflight(session, timeline) : null;
                var offlineMedia = preflight is null ? null : OfflineMediaJson(session, timeline, preflight);

                // WP-399: an agent's render lands in a file it chose, so an allowed external file must still
                // lead where it did when the user allowed it (checked once per file for this export).
                var exporter = new TimelineExporter(
                    CompositorFactory.Create(),
                    new ExportFrameSource(),
                    session.Resolver.ForTransfer(),
                    timelineLookup: SessionTimelineLookup.For(session),
                    acceleration: acceleration,
                    ownsFrameSource: true);
                // The editor's "Export anyway". Its consent covers the clips its warning listed; an
                // agent consents before it can see a list, so this covers whatever the render drops,
                // and the result names each one. A render with no picture left is refused regardless.
                exporter.AllowPartialPicture = allowOfflineMedia;

                var queue = args["background"]?.GetValue<bool>() ?? omittedPath;
                if (queue)
                {
                    var job = _exportQueue.EnqueueVideo(exporter, timeline, preset, outputPath, overwrite: overwrite);
                    var queued = new { status = "queued", mode, jobId = job.Id, outputPath, codec = codec.ToString(), resolution = resolution.ToString(), preferHardware };
                    if (preflight is null) return queued;
                    TrackOfflineExport(job, exporter, preflight);
                    return WithOfflineReceipt(queued, offlineMedia!, [.. OfflineWarnings(preflight, null)], missingPicture: null);
                }

                // Run through the queue even when the caller wants to block: the job then has an id
                // that cancel_export can reach, and it inherits the queue's cancellation token
                // instead of running with CancellationToken.None and being unkillable.
                var syncJob = _exportQueue.EnqueueVideo(exporter, timeline, preset, outputPath, overwrite: overwrite);
                if (preflight is not null) TrackOfflineExport(syncJob, exporter, preflight);
                WaitForJob(syncJob);
                if (syncJob.State == ExportJobState.Failed)
                    throw new InvalidOperationException(syncJob.Error ?? "Export failed.");
                if (syncJob.State == ExportJobState.Cancelled)
                    throw new OperationCanceledException($"Export {syncJob.Id} was cancelled.");
                var exported = new { status = "success", mode, jobId = syncJob.Id, outputPath, codec = codec.ToString(), resolution = resolution.ToString(), encoder = exporter.LastEncoderName, preferHardware };
                if (preflight is null) return exported;
                var dropped = exporter.LastVideoDiagnostics;
                var names = PreflightNames(preflight);
                return WithOfflineReceipt(
                    exported,
                    offlineMedia!,
                    [.. OfflineWarnings(preflight, MissingPictureSentence(dropped, names))],
                    MissingPictureJson(dropped, names));
            }
            case "fcpxml":
            {
                var target = string.Equals(args["fcpxmlTarget"]?.GetValue<string>(), "fcp", StringComparison.OrdinalIgnoreCase)
                    ? FcpXmlTarget.Fcp : FcpXmlTarget.Resolve;
                var report = FcpXmlExporter.Export(
                    timeline, session.Resolver, outputPath,
                    resolveTimeline: SessionTimelineLookup.For(session),
                    target: target);
                return new
                {
                    status = "success",
                    mode,
                    outputPath,
                    target = target.ToString(),
                    omissions = InterchangeOmissionsJson(report),
                    notTransported = report.NotTransported,
                };
            }
            case "xml":
            {
                // XMEML 4 (FCP7) for Premiere / Resolve (WP-112).
                var report = XmemlExporter.Export(
                    timeline,
                    session.Resolver,
                    outputPath,
                    resolveTimeline: SessionTimelineLookup.For(session));
                return new
                {
                    status = "success",
                    mode,
                    outputPath,
                    format = report.Format,
                    omissions = InterchangeOmissionsJson(report),
                    notTransported = report.NotTransported,
                };
            }
            case "palmier":
            {
                // Self-contained package: copy/collect media into the destination package (WP-112).
                // The mode name stays "palmier" (MCP wire); a new package is written as .temno
                // (WP-193). SettleExportDestination has already named the package and refused
                // anything there that is not a project package (WP-398); the exporter checks again.
                var report = TemnoProjectExporter.Export(
                    session.ProjectFile,
                    session.Manifest,
                    session.PackagePath,
                    outputPath,
                    generationLogBytes: null,
                    replaceExistingPackage: overwrite,
                    resolver: session.Resolver);
                return new
                {
                    status = "success",
                    mode,
                    outputPath,
                    collected = report.Collected,
                    copiedInternal = report.CopiedInternal,
                    missing = report.Missing.Select(m => new { id = m.Id, name = m.Name }).ToList(),
                    // WP-399: files outside the project the user has not allowed stay out, by reference.
                    notAllowed = report.NotAllowed.Select(m => new { id = m.Id, name = m.Name }).ToList(),
                    totalBytes = report.TotalBytes,
                    warnings = report.Warnings,
                };
            }
            default:
                throw new InvalidOperationException($"Unknown export mode '{mode}'. Use video, fcpxml, xml, or palmier.");
        }
    }

    /// <summary>
    /// What an XMEML/FCPXML export left out (WP-236): empty only when no marker, transition or
    /// clip was dropped. <c>notTransported</c> travels beside it so success never reads as lossless.
    /// </summary>
    private static List<object> InterchangeOmissionsJson(InterchangeExportReport report)
        => report.Omissions
            .Select(o => (object)new { kind = o.Kind, count = o.Count, detail = o.Detail })
            .ToList();

    /// <summary>
    /// Blocks the calling (MCP pool) thread until a queued export settles. export_project is in
    /// <see cref="OffDispatcherTools"/>, so this never occupies the WinUI dispatcher.
    /// </summary>
    private static void WaitForJob(ExportJob job)
    {
        using var settled = new ManualResetEventSlim(false);
        void OnChanged(ExportJob changed)
        {
            if (changed.Id == job.Id && changed.State.IsFinished())
                settled.Set();
        }

        _exportQueue.JobChanged += OnChanged;
        try
        {
            if (job.State.IsFinished()) return;
            settled.Wait();
        }
        finally
        {
            _exportQueue.JobChanged -= OnChanged;
        }
    }

    private static object AddTextsTool(JsonObject args)
    {
        var session = GetSession();
        if (args["entries"] is not JsonArray entries || entries.Count == 0)
            throw new InvalidOperationException("Missing or empty 'entries' array");

        var timeline = session.ActiveTimeline;
        var specs = new List<TextClipSpec>(entries.Count);
        for (var i = 0; i < entries.Count; i++)
        {
            if (entries[i] is not JsonObject entry)
                throw new InvalidOperationException($"entries[{i}] must be an object");

            var startFrame = entry["startFrame"]?.GetValue<int>()
                ?? throw new InvalidOperationException($"entries[{i}]: startFrame is required");
            var endFrame = entry["endFrame"]?.GetValue<int>()
                ?? throw new InvalidOperationException($"entries[{i}]: endFrame is required");
            var content = entry["content"]?.GetValue<string>()
                ?? throw new InvalidOperationException($"entries[{i}]: content is required");

            var style = new TextStyle();
            ParseTextStylePatch(entry, $"entries[{i}]").ApplyTo(style);

            specs.Add(new TextClipSpec(
                entry["trackIndex"]?.GetValue<int>(),
                startFrame,
                endFrame,
                content,
                style,
                ParseAddTextTransform(entry["transform"], content, style, timeline, $"entries[{i}]"),
                ParseTextAnimationSpec(entry, $"entries[{i}]").Animation));
        }

        var snapshot = TakeTimelineSnapshot(timeline);
        var result = TextClipCommands.AddTexts(session, specs, out var createdIds);
        if (!result.Ok) throw new InvalidOperationException(result.Error ?? "add_texts failed");
        return GetMutationResult(session, snapshot, touched: createdIds);
    }

    private static object UpdateTextTool(JsonObject args)
    {
        var session = GetSession();
        var patch = new TextPatch
        {
            HasContent = args.ContainsKey("content"),
            Content = args["content"]?.GetValue<string>(),
            Style = ParseTextStylePatch(args, "update_text"),
        };

        if (args["transform"] is JsonObject t)
        {
            patch.CenterX = Num(t["centerX"]);
            patch.CenterY = Num(t["centerY"]);
            patch.Width = Num(t["width"]);
            patch.Height = Num(t["height"]);
        }

        var (setAnimation, animation, highlightOnly) = ParseTextAnimationSpec(args, "update_text");
        patch.SetAnimation = setAnimation;
        patch.Animation = animation;
        patch.HighlightOnly = highlightOnly;

        var snapshot = TakeTimelineSnapshot(session.ActiveTimeline);
        var result = TextClipCommands.UpdateText(
            session,
            ParseStringArray(args["clipIds"]),
            args["captionGroupId"]?.GetValue<string>(),
            patch,
            out var touched,
            out var notes);
        if (!result.Ok) throw new InvalidOperationException(result.Error ?? "update_text failed");
        return GetMutationResult(session, snapshot, touched: touched, notes: notes);
    }

    private static TextStylePatch ParseTextStylePatch(JsonObject args, string path)
    {
        TextStyle.RGBA? Hex(string key)
        {
            var value = args[key]?.GetValue<string>();
            if (value is null) return null;
            try { return TextClipCommands.ParseHexColor(value); }
            catch (FormatException ex) { throw new InvalidOperationException($"{path}.{key}: {ex.Message}"); }
        }

        TextAlignment? alignment = null;
        if (args["alignment"]?.GetValue<string>() is { } alignRaw)
        {
            alignment = alignRaw.ToLowerInvariant() switch
            {
                "left" => TextAlignment.Left,
                "center" => TextAlignment.Center,
                "right" => TextAlignment.Right,
                _ => throw new InvalidOperationException($"{path}.alignment must be left, center, or right"),
            };
        }

        return new TextStylePatch
        {
            FontName = args["fontName"]?.GetValue<string>(),
            FontSize = Num(args["fontSize"]),
            IsBold = args["isBold"]?.GetValue<bool>(),
            IsItalic = args["isItalic"]?.GetValue<bool>(),
            Color = Hex("color"),
            Alignment = alignment,
            BorderColor = Hex("borderColor"),
            BackgroundColor = Hex("backgroundColor"),
        };
    }

    /// <summary>
    /// Mac rule: transform is either {centerX, centerY} for auto-fit, or all four of
    /// {centerX, centerY, width, height}.
    /// </summary>
    private static Transform? ParseAddTextTransform(
        JsonNode? node, string content, TextStyle style, Timeline timeline, string path)
    {
        if (node is not JsonObject t) return null;
        var cx = Num(t["centerX"]);
        var cy = Num(t["centerY"]);
        var w = Num(t["width"]);
        var h = Num(t["height"]);
        if (cx is null && cy is null && w is null && h is null) return null;
        if (cx is null || cy is null)
            throw new InvalidOperationException($"{path}: transform must be either {{centerX, centerY}} for auto-fit, or all four of {{centerX, centerY, width, height}}");
        if (w is not null && h is not null)
            return new Transform { CenterX = cx.Value, CenterY = cy.Value, Width = w.Value, Height = h.Value };
        if (w is not null || h is not null)
            throw new InvalidOperationException($"{path}: transform must be either {{centerX, centerY}} for auto-fit, or all four of {{centerX, centerY, width, height}}");
        return TextClipCommands.FitTransform(
            content, style, Math.Max(1, timeline.Width), Math.Max(1, timeline.Height), cx.Value, cy.Value);
    }

    private static (bool SetAnimation, TextAnimation? Animation, TextStyle.RGBA? HighlightOnly) ParseTextAnimationSpec(
        JsonObject args, string path)
    {
        var raw = args["animation"]?.GetValue<string>();
        TextStyle.RGBA? highlight = null;
        if (args["highlightColor"]?.GetValue<string>() is { } hex)
        {
            try { highlight = TextClipCommands.ParseHexColor(hex); }
            catch (FormatException ex) { throw new InvalidOperationException($"{path}.highlightColor: {ex.Message}"); }
        }

        if (raw is null)
            return (false, null, highlight);
        if (raw == "off")
            return (true, null, null);
        if (!TextAnimation.Presets.Contains(raw))
            throw new InvalidOperationException($"{path}: animation must be one of {string.Join(", ", TextAnimation.AgentValues)}");

        return (true, new TextAnimation { Preset = raw, Highlight = highlight }, null);
    }

    /// <summary>
    /// Rejects arguments the tool's registered schema does not declare, including nested objects
    /// that list properties. Objects without properties stay open (color-grade paste, effect params).
    /// </summary>
    private static void RejectUnknownArguments(string toolName, JsonObject args)
    {
        var schema = McpToolRegistry.Tools.FirstOrDefault(t => t.Name == toolName)?.InputSchema;
        if (schema is null)
            return;
        var unknown = new List<string>();
        CollectUnknownKeys(args, schema, "", unknown, closedIfNoProperties: true);
        if (unknown.Count == 0)
            return;
        unknown.Sort(StringComparer.Ordinal);
        McpCategoryLog.UnknownArguments(toolName, unknown);
        throw new InvalidOperationException(
            $"{toolName}: unknown argument(s) {string.Join(", ", unknown)}");
    }

    private static void CollectUnknownKeys(
        JsonNode? value,
        JsonObject schema,
        string path,
        List<string> unknown,
        bool closedIfNoProperties)
    {
        if (value is null)
            return;
        var schemaType = schema["type"] is JsonValue typeValue && typeValue.TryGetValue<string>(out var typeText)
            ? typeText
            : null;

        if (value is JsonObject obj && schemaType is null or "object")
        {
            if (schema["properties"] is JsonObject properties)
            {
                foreach (var pair in obj)
                {
                    var childPath = string.IsNullOrEmpty(path) ? pair.Key : $"{path}.{pair.Key}";
                    if (properties[pair.Key] is not JsonObject childSchema)
                    {
                        unknown.Add(childPath);
                        continue;
                    }

                    CollectUnknownKeys(pair.Value, childSchema, childPath, unknown, closedIfNoProperties: false);
                }
            }
            else if (closedIfNoProperties)
            {
                foreach (var pair in obj)
                    unknown.Add(string.IsNullOrEmpty(path) ? pair.Key : $"{path}.{pair.Key}");
            }

            return;
        }

        if (value is JsonArray array
            && schemaType is null or "array"
            && schema["items"] is JsonObject itemSchema)
        {
            for (var i = 0; i < array.Count; i++)
            {
                if (array[i] is { } element)
                    CollectUnknownKeys(element, itemSchema, $"{path}[{i}]", unknown, closedIfNoProperties: false);
            }
        }
    }

    private static object AddCaptions(JsonObject args)
    {
        var session = GetSession();

        if (args["subtitleMediaRef"]?.GetValue<string>() is { } subtitleRef)
        {
            var others = args
                .Select(pair => pair.Key)
                .Where(key => !string.Equals(key, "subtitleMediaRef", StringComparison.Ordinal))
                .OrderBy(key => key, StringComparer.Ordinal)
                .ToList();
            if (others.Count > 0)
                throw new InvalidOperationException(
                    "add_captions: subtitleMediaRef cannot be combined with other arguments (got "
                    + string.Join(", ", others) + ").");
            return AddCaptionsFromSubtitle(session, subtitleRef);
        }

        if (args["censorProfanity"]?.GetValue<bool>() == true)
            throw new InvalidOperationException(
                "add_captions: censorProfanity is not available with on-device Whisper on Windows.");

        var request = new CaptionRequest { Language = args["language"]?.GetValue<string>() };
        ParseTextStylePatch(args, "add_captions").ApplyTo(request.Style);

        double? rotation = null;
        if (args["transform"] is JsonObject t)
        {
            if (Num(t["centerX"]) is { } cx) request.CenterX = cx;
            if (Num(t["centerY"]) is { } cy) request.CenterY = cy;
            if (Num(t["rotation"]) is { } rot) rotation = rot;
        }

        if (args["textCase"]?.GetValue<string>() is { } rawCase)
        {
            if (!Enum.TryParse<CaptionCase>(rawCase, out var parsed))
                throw new InvalidOperationException($"add_captions: textCase must be auto, upper, or lower (got {rawCase})");
            request.TextCase = parsed;
        }

        if (args["maxWords"]?.GetValue<int>() is { } maxWords)
        {
            if (maxWords < 1)
                throw new InvalidOperationException($"add_captions: maxWords must be >= 1 (got {maxWords})");
            request.MaxWords = maxWords;
        }

        if (args["maxCharacters"]?.GetValue<int>() is { } maxCharacters)
        {
            if (maxCharacters < 1)
                throw new InvalidOperationException($"add_captions: maxCharacters must be >= 1 (got {maxCharacters})");
            request.MaxCharacters = maxCharacters;
        }

        if (Num(args["maximumGapSeconds"]) is { } gapSeconds)
        {
            if (!CaptionSpecBuilder.IsValidGapSeconds(gapSeconds))
                throw new InvalidOperationException(
                    $"add_captions: maximumGapSeconds must be between 0 and {CaptionSpecBuilder.MaximumGapSecondsLimit} (got {gapSeconds})");
            request.MaximumGapSeconds = gapSeconds;
        }

        if (args["trackIndex"]?.GetValue<int>() is { } captionTrackIndex)
        {
            var trackCount = session.ActiveTimeline.Tracks.Count;
            if (captionTrackIndex < 0 || captionTrackIndex >= trackCount)
                throw new InvalidOperationException(
                    $"add_captions: trackIndex {captionTrackIndex} out of range (0..{trackCount - 1})");
            request.TrackIndex = captionTrackIndex;
        }

        var (_, animation, highlightOnly) = ParseTextAnimationSpec(args, "add_captions");
        if (animation is not null) request.Animation = animation;
        else if (highlightOnly is not null)
            request.Animation = new TextAnimation { Preset = "none", Highlight = highlightOnly };

        var snapshot = TakeTimelineSnapshot(session.ActiveTimeline);
        var result = CaptionGenerator
            .GenerateAsync(session, GetTranscriptionProvider(), request)
            .GetAwaiter().GetResult();
        if (result.CreatedClipIds.Count == 0)
            throw new InvalidOperationException("No speech detected to caption.");

        if (rotation is { } degrees)
        {
            foreach (var clip in session.ActiveTimeline.Tracks[0].Clips
                         .Where(c => result.CreatedClipIds.Contains(c.Id)))
            {
                clip.Transform ??= new Transform();
                clip.Transform.Rotation = degrees;
            }
        }

        var extra = new Dictionary<string, object>
        {
            ["captionsCreated"] = result.CreatedClipIds.Count,
            ["captionGroupId"] = session.ActiveTimeline.Tracks[0].Clips
                .First(c => c.Id == result.CreatedClipIds[0]).CaptionGroupId ?? "",
            ["transcriptionSource"] = result.TranscriptionSource,
        };
        if (result.Skipped.Count > 0)
        {
            extra["skipped"] = result.Skipped
                .Select(s => new { clipId = s.ClipId, mediaRef = s.MediaRef, reason = s.Reason })
                .ToList();
        }
        return GetMutationResult(session, snapshot, touched: result.CreatedClipIds, extra: extra);
    }

    /// <summary>
    /// Mac add_captions subtitleMediaRef: place an SRT/VTT library asset's cues as caption clips on
    /// a new top video track, in one undo step, with gap closing off.
    /// </summary>
    private static object AddCaptionsFromSubtitle(ProjectSession session, string subtitleRef)
    {
        var entry = session.Manifest.Entries.FirstOrDefault(e => e.Id == subtitleRef)
            ?? throw new InvalidOperationException($"Media asset not found: {subtitleRef}");
        if (entry.Type != ClipType.subtitle)
            throw new InvalidOperationException(
                $"add_captions: subtitleMediaRef must reference an SRT or WebVTT asset (got {entry.Type}).");

        // WP-399: the cues become caption text the caller reads back, so the file must be one the
        // project may hand on.
        var path = TransferPath(session, entry, subtitleRef);
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
            throw new InvalidOperationException($"add_captions: subtitle asset is offline: {entry.Name}");

        CaptionParseResult parsed;
        try
        {
            parsed = SubtitleFile.Read(path);
        }
        catch (InvalidDataException ex)
        {
            throw new InvalidOperationException($"add_captions: {ex.Message}");
        }

        var timeline = session.ActiveTimeline;
        var style = new TextStyle { FontSize = 48 };
        var canvasW = Math.Max(1, timeline.Width);
        var canvasH = Math.Max(1, timeline.Height);
        var groupId = Timeline.NewId();

        var snapshot = TakeTimelineSnapshot(timeline);
        var import = CaptionImporter.ImportCaptions(
            session,
            parsed.Entries,
            trackIndex: null,
            style: style,
            transform: null,
            maximumGapSeconds: CaptionSpecBuilder.SubtitleImportGapSeconds,
            transformFor: text => TextClipCommands.FitTransform(text, style, canvasW, canvasH, 0.5, 0.9),
            captionGroupId: groupId);
        if (!import.Ok)
            throw new InvalidOperationException($"add_captions: {import.Error}");

        var created = timeline.Tracks[0].Clips.Select(c => c.Id).ToList();
        var extra = new Dictionary<string, object>
        {
            ["captionsCreated"] = created.Count,
            ["captionGroupId"] = groupId,
            ["transcriptionSource"] = "subtitle file",
            ["subtitleMediaRef"] = subtitleRef,
        };
        var warnings = parsed.Warnings.ToList();
        if (warnings.Count > 0)
            extra["warnings"] = warnings;
        return GetMutationResult(session, snapshot, touched: created, extra: extra);
    }

    private const int TranscriptWordLimit = 10000;

    private static object GetTranscript(JsonObject args)
    {
        // Off-dispatcher (see OffDispatcherTools): transcribing a timeline runs Whisper or a cloud
        // round-trip and takes minutes. TimelineTranscriptBuilder.BuildAsync only reads the session.
        var (session, timeline) = ProjectHub.RunSerialized(() =>
        {
            var s = GetSession();
            return (s, s.ActiveTimeline);
        });

        var clipFilter = args["clipId"]?.GetValue<string>();
        var trackFilter = args["trackIndex"]?.GetValue<int>();
        if (trackFilter is { } wantedTrack)
        {
            var trackCount = ProjectHub.RunSerialized(() => timeline.Tracks.Count);
            if (wantedTrack < 0 || wantedTrack >= trackCount)
                throw new InvalidOperationException(
                    $"trackIndex {wantedTrack} out of range (0..{trackCount - 1})");
        }
        var windowStart = args["startFrame"]?.GetValue<int>();
        var windowEnd = args["endFrame"]?.GetValue<int>();
        if (windowStart is { } ws && windowEnd is { } we && ws >= we)
            throw new InvalidOperationException($"startFrame ({ws}) must be less than endFrame ({we})");
        if (clipFilter is not null
            && !ProjectHub.RunSerialized(
                () => timeline.Tracks.SelectMany(t => t.Clips).Any(c => c.Id == clipFilter)))
            throw new InvalidOperationException($"Clip not found: {clipFilter}");

        var granularity = args["granularity"]?.GetValue<string>() ?? "words";
        if (granularity is not ("words" or "segments"))
            throw new InvalidOperationException($"granularity must be 'words' or 'segments' (got '{granularity}')");
        var segments = granularity == "segments";

        var transcript = TimelineTranscriptBuilder
            .BuildAsync(session, GetTranscriptionProvider(), args["language"]?.GetValue<string>())
            .GetAwaiter().GetResult();

        // Payload mirrors Mac TimelineTranscript.responsePayload: project-frame words per clip,
        // capped with a continuation cursor; speakers as run-length turns.
        var clipsOut = new List<Dictionary<string, object>>();
        var totalWords = 0;
        var remaining = TranscriptWordLimit;
        int? lastEnd = null;
        var speakerRuns = new List<object[]>();
        string? currentSpeaker = null;
        var speakerRunStarted = false;

        foreach (var group in transcript.Groups(clipFilter))
        {
            if (trackFilter is { } onlyTrack && group.TrackIndex != onlyTrack) continue;
            var visible = new List<TimelineWord>();
            foreach (var word in group.Words)
            {
                if (windowStart is { } start && word.EndFrame <= start) continue;
                if (windowEnd is { } end && word.StartFrame >= end) continue;
                totalWords++;
                if (remaining <= 0) continue;
                visible.Add(word);
                remaining--;
                lastEnd = word.EndFrame;
                if (transcript.IncludesSpeakers && (!speakerRunStarted || currentSpeaker != word.Speaker))
                {
                    speakerRunStarted = true;
                    currentSpeaker = word.Speaker;
                    speakerRuns.Add([word.Index, word.Speaker ?? (object)"unknown"]);
                }
            }
            if (visible.Count == 0) continue;

            var clipOut = new Dictionary<string, object>
            {
                ["clipId"] = group.ClipId,
                ["trackIndex"] = group.TrackIndex,
                ["startFrame"] = group.ClipStartFrame,
                ["endFrame"] = group.ClipEndFrame,
            };
            if (segments)
                clipOut["segments"] = SegmentRows(visible, timeline.Fps);
            else
                clipOut["words"] = visible.Select(w => new object[] { w.Index, w.Text, w.StartFrame }).ToList();
            clipsOut.Add(clipOut);
        }

        var outDict = new Dictionary<string, object>
        {
            ["fps"] = timeline.Fps,
            ["timing"] = "projectFrames",
            ["transcriptionSource"] = transcript.Source,
            ["clips"] = clipsOut,
        };
        if (segments)
            outDict["segmentFormat"] = new[] { "firstWordIndex", "text", "start", "end" };
        else
            outDict["wordFormat"] = new[] { "index", "text", "start" };
        if (speakerRuns.Count > 0)
        {
            outDict["speakers"] = speakerRuns;
            outDict["speakersNote"] = "[firstWordIndex, speaker] — each run holds until the next entry.";
        }
        if (totalWords > TranscriptWordLimit)
        {
            outDict["totalWords"] = totalWords;
            if (lastEnd is { } cursor)
            {
                outDict["nextStartFrame"] = cursor;
                outDict["wordsNote"] = $"First {TranscriptWordLimit} of {totalWords} words. Continue with startFrame = nextStartFrame.";
            }
        }
        if (transcript.Skipped.Count > 0)
        {
            outDict["skipped"] = transcript.Skipped
                .Select(s => new { clipId = s.ClipId, mediaRef = s.MediaRef, reason = s.Reason })
                .ToList();
        }
        return outDict;
    }

    /// <summary>Sentence-ish rows for comprehension reads (Mac segmentRows break rules).</summary>
    private static List<object[]> SegmentRows(IReadOnlyList<TimelineWord> words, int fps)
    {
        var rows = new List<object[]>();
        var run = new List<TimelineWord>();
        void Flush()
        {
            if (run.Count == 0) return;
            rows.Add([run[0].Index, string.Join(" ", run.Select(w => w.Text)), run[0].StartFrame, run[^1].EndFrame]);
            run.Clear();
        }
        foreach (var word in words)
        {
            if (run.Count > 0)
            {
                var last = run[^1];
                if (last.Speaker != word.Speaker || word.StartFrame - last.EndFrame > fps || run.Count >= 48)
                    Flush();
            }
            run.Add(word);
            if (word.Text.EndsWith('.') || word.Text.EndsWith('!') || word.Text.EndsWith('?'))
                Flush();
        }
        Flush();
        return rows;
    }

    private static object RemoveWords(JsonObject args)
    {
        var session = GetSession();
        var timeline = session.ActiveTimeline;

        var rawWords = args["words"] as JsonArray;
        var rawMatches = args["matches"] as JsonArray;
        if (rawWords is { Count: 0 } || rawMatches is { Count: 0 })
            throw new InvalidOperationException("remove_words: words or matches must not be empty.");
        if (rawWords is null && rawMatches is null)
            throw new InvalidOperationException(
                "Missing 'words' or 'matches'. Pass word indices from get_transcript, e.g. [5, [12, 18]], or exact words like [\"um\", \"uh\"].");
        if (rawWords is not null && rawMatches is not null)
            throw new InvalidOperationException("remove_words: pass either words or matches, not both.");

        var aggressiveness = CutAggressiveness.balanced;
        if (args["cutAggressiveness"]?.GetValue<string>() is { } rawAggro
            && !Enum.TryParse(rawAggro, out aggressiveness))
            throw new InvalidOperationException(
                $"cutAggressiveness must be one of: {string.Join(", ", Enum.GetNames<CutAggressiveness>())}.");

        var transcript = TimelineTranscriptBuilder
            .BuildAsync(session, GetTranscriptionProvider(), args["language"]?.GetValue<string>())
            .GetAwaiter().GetResult();
        var allWords = transcript.Words;
        if (allWords.Count == 0)
            throw new InvalidOperationException("No transcribable speech on the timeline.");

        var selected = new HashSet<int>();
        var ignored = new List<int>();
        var maxIndex = allWords.Count - 1;
        if (rawWords is not null)
        {
            foreach (var (a, b) in ParseWordSpans(rawWords))
            {
                var lo = Math.Min(a, b);
                var hi = Math.Max(a, b);
                if (hi < 0 || lo > maxIndex) { ignored.Add(lo); continue; }
                if (lo < 0) ignored.Add(lo);
                if (hi > maxIndex) ignored.Add(hi);
                for (var idx = Math.Max(0, lo); idx <= Math.Min(maxIndex, hi); idx++) selected.Add(idx);
            }
            if (selected.Count == 0)
                throw new InvalidOperationException($"None of the requested word indices are in range 0...{maxIndex}. Re-read get_transcript.");
        }
        else
        {
            var matches = ParseWordMatches(rawMatches!);
            foreach (var word in allWords)
            {
                if (matches.Contains(NormalizedWordMatch(word.Text)))
                    selected.Add(word.Index);
            }
            if (selected.Count == 0)
                throw new InvalidOperationException(
                    $"No transcript words matched: {string.Join(", ", matches.OrderBy(m => m, StringComparer.Ordinal))}. Re-read get_transcript or pass exact word indices.");
        }

        var keepGapFrames = WordCutPlanner.KeepGapFrames(aggressiveness, timeline.Fps);
        var removedTexts = new List<string>();
        var rangesByTrack = new Dictionary<int, List<FrameRange>>();
        var involvedClipIds = new List<string>();
        foreach (var group in transcript.Groups())
        {
            if (!group.Words.Any(w => selected.Contains(w.Index))) continue;
            removedTexts.AddRange(group.Words
                .Where(w => selected.Contains(w.Index) && w.EndFrame > w.StartFrame)
                .Select(w => w.Text));
            var planWords = group.Words
                .Select(w => new WordCutPlanner.Word(w.StartFrame, w.EndFrame, selected.Contains(w.Index)))
                .ToList();
            var ranges = WordCutPlanner.CutRanges(planWords, group.ClipStartFrame, group.ClipEndFrame, keepGapFrames);
            if (ranges.Count == 0) continue;
            if (!rangesByTrack.TryGetValue(group.TrackIndex, out var list))
                rangesByTrack[group.TrackIndex] = list = [];
            list.AddRange(ranges);
            involvedClipIds.Add(group.ClipId);
        }
        if (rangesByTrack.Count == 0)
            throw new InvalidOperationException("The selected words resolved to no removable frames. Re-read get_transcript.");

        var speechTracks = rangesByTrack.Keys.ToHashSet();
        var merged = RippleEngine.MergeRanges(rangesByTrack.SelectMany(kv => kv.Value).ToList());
        if (speechTracks.Count > 1 && !SpeechClipsAreLinkedTogether(timeline, involvedClipIds))
        {
            var tracks = string.Join(", ", speechTracks.OrderBy(t => t));
            throw new InvalidOperationException(
                $"Selected words span multiple unlinked tracks ({tracks}). Remove words one track at a time, or link the clips first.");
        }

        var cutTracks = LinkedCutTracks(session, speechTracks, merged);
        var snapshot = TakeTimelineSnapshot(timeline);
        var applied = ApplyRippleCutsOnTracks(session, cutTracks, merged);
        if (!applied.Ok)
            throw new InvalidOperationException(applied.Error ?? "Ripple delete refused.");

        var extra = new Dictionary<string, object>
        {
            ["removedWords"] = removedTexts.Count,
            ["removedFrames"] = merged.Sum(r => r.Length),
            ["cutAggressiveness"] = aggressiveness.ToString(),
            ["transcriptionSource"] = transcript.Source,
        };
        var preview = string.Join(" ", removedTexts.Take(24));
        if (preview.Length > 0)
            extra["removedText"] = removedTexts.Count > 24 ? preview + " …" : preview;
        if (ignored.Count > 0)
            extra["indicesIgnored"] = ignored.OrderBy(i => i).ToList();

        var notes = new List<string> { "Word indices shifted — re-read get_transcript before another remove_words." };
        return GetMutationResult(session, snapshot, extra: extra, notes: notes);
    }

    private static bool SpeechClipsAreLinkedTogether(Timeline timeline, IReadOnlyList<string> clipIds)
    {
        if (clipIds.Count <= 1)
            return true;
        var family = LinkGroup.Expand(timeline, [clipIds[0]]);
        return clipIds.All(id => family.Contains(id));
    }

    private static bool ClipOverlapsRanges(Clip clip, IReadOnlyList<FrameRange> ranges)
        => ranges.Any(r => clip.StartFrame < r.End && clip.EndFrame > r.Start);

    private static EditResult ApplyRippleCutsOnTracks(
        ProjectSession session,
        IReadOnlyList<int> trackIndices,
        IReadOnlyList<FrameRange> ranges)
    {
        if (ranges.Count == 0)
            return EditResult.Success();

        var uniqueTracks = trackIndices.Distinct().ToList();
        var tuples = ranges.Select(r => (r.Start, r.End)).ToList();
        return EditCommands.RippleDeleteRanges(session, uniqueTracks, tuples);
    }

    /// <summary>
    /// WP-337: the tracks a ripple cut of <paramref name="ranges"/> on <paramref name="seedTracks"/> must cut, shared by
    /// ripple_delete_ranges, remove_words and remove_silence. A cut trims, removes or moves every clip that ends after
    /// the first range starts, and each such clip's partners must move with it, so their tracks are cut too, to a
    /// fixed point. Partners share a link group or a multicam group (multicam clips carry no link). The Mac reaches the
    /// same result through sync lock, on by default; Windows has none, so the groups are what carry the other tracks.
    /// A track that joins through a partner may hold nothing inside the ranges but the clips being cut and their
    /// partners: cutting an unrelated clip, or leaving the track out and putting the partners out of sync, are both
    /// wrong, so the cut is refused (D-1).
    /// </summary>
    private static List<int> LinkedCutTracks(
        ProjectSession session,
        IReadOnlyCollection<int> seedTracks,
        IReadOnlyList<FrameRange> ranges)
    {
        var timeline = session.ActiveTimeline;
        var cut = seedTracks.Distinct().ToList();
        if (ranges.Count == 0)
            return cut;

        var trackOf = new Dictionary<string, int>(StringComparer.Ordinal);
        var members = new Dictionary<string, List<Clip>>(StringComparer.Ordinal);
        for (var t = 0; t < timeline.Tracks.Count; t++)
        {
            foreach (var clip in timeline.Tracks[t].Clips)
            {
                trackOf[clip.Id] = t;
                foreach (var group in GroupsOf(clip))
                {
                    if (!members.TryGetValue(group, out var list))
                        members[group] = list = [];
                    list.Add(clip);
                }
            }
        }
        IEnumerable<Clip> Partners(Clip clip) => GroupsOf(clip).SelectMany(g => members[g]);

        // The clips the cut trims, removes or moves on the seed tracks, and every clip that shares a group with them,
        // transitively. A moved clip counts: a J-cut's sound starts before its picture, so cutting the picture track's
        // lead-in must cut that sound too.
        var firstStart = ranges.Min(r => r.Start);
        var family = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Queue<Clip>(cut.SelectMany(t => timeline.Tracks[t].Clips).Where(c => c.EndFrame > firstStart));
        while (pending.TryDequeue(out var clip))
        {
            if (!family.Add(clip.Id)) continue;
            foreach (var partner in Partners(clip))
                pending.Enqueue(partner);
        }

        var included = cut.ToHashSet();
        for (var next = 0; next < cut.Count; next++)
        {
            foreach (var clip in timeline.Tracks[cut[next]].Clips.Where(c => c.EndFrame > firstStart))
            {
                foreach (var partner in Partners(clip))
                {
                    if (included.Add(trackOf[partner.Id]))
                        cut.Add(trackOf[partner.Id]);
                }
            }
        }

        var seeds = seedTracks.ToHashSet();
        foreach (var trackIndex in cut.Where(t => !seeds.Contains(t)).OrderBy(t => t))
        {
            var stray = timeline.Tracks[trackIndex].Clips
                .Where(c => ClipOverlapsRanges(c, ranges) && !family.Contains(c.Id))
                .OrderBy(c => c.StartFrame)
                .FirstOrDefault();
            if (stray is not null)
            {
                // The Audio tab shows this too, so it names the track as its header does and the clip by its media.
                var label = Temno.Core.Ui.TrackLabels.LabelFor(timeline.Tracks.Select(t => t.Type).ToList(), trackIndex);
                var name = session.Manifest.Entries.FirstOrDefault(e => e.Id == stray.MediaRef)?.Name;
                var clip = ShortIdForMessage(stray.Id) + (string.IsNullOrWhiteSpace(name) ? "" : $" (\"{name}\")");
                throw new InvalidOperationException(
                    $"Nothing was cut: track {trackIndex} ({label}) holds clip {clip} inside the cut, and it is not linked to the clips being cut. "
                    + $"That track must be cut with them to keep their linked clips in sync. Move the clip to another track or out of the cut, then cut again.");
            }
        }

        return cut;
    }

    /// <summary><paramref name="ranges"/> with every frame of <paramref name="remove"/> taken out, in order.</summary>
    private static List<FrameRange> SubtractRanges(IReadOnlyList<FrameRange> ranges, IReadOnlyList<FrameRange> remove)
    {
        var holes = RippleEngine.MergeRanges(remove);
        var result = new List<FrameRange>();
        foreach (var range in RippleEngine.MergeRanges(ranges))
        {
            var start = range.Start;
            foreach (var hole in holes.Where(h => h.End > range.Start && h.Start < range.End))
            {
                if (hole.Start > start)
                    result.Add(new FrameRange(start, hole.Start));
                start = Math.Max(start, hole.End);
            }
            if (start < range.End)
                result.Add(new FrameRange(start, range.End));
        }
        return result;
    }

    private static IEnumerable<string> GroupsOf(Clip clip)
    {
        if (!string.IsNullOrEmpty(clip.LinkGroupId))
            yield return "link:" + clip.LinkGroupId;
        if (!string.IsNullOrEmpty(clip.MulticamGroupId))
            yield return "multicam:" + clip.MulticamGroupId;
    }

    // Error results are not shortened on the way out, so a message that names an id shortens it here.
    private static string ShortIdForMessage(string id)
    {
        var universe = CollectIdUniverse();
        return universe.Contains(id) && ToolIdShortener.ShortIdMap(universe).TryGetValue(id, out var shortId) ? shortId : id;
    }

    private static List<(int A, int B)> ParseWordSpans(JsonArray raw)
    {
        var spans = new List<(int, int)>(raw.Count);
        for (var i = 0; i < raw.Count; i++)
        {
            var element = raw[i];
            if (Num(element) is { } single && single == Math.Floor(single))
            {
                spans.Add(((int)single, (int)single));
                continue;
            }
            if (element is JsonArray { Count: 2 } pair
                && Num(pair[0]) is { } a && Num(pair[1]) is { } b)
            {
                spans.Add(((int)a, (int)b));
                continue;
            }
            throw new InvalidOperationException($"words[{i}]: expected an integer index or an [start, end] pair.");
        }
        return spans;
    }

    private static HashSet<string> ParseWordMatches(JsonArray raw)
    {
        var matches = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < raw.Count; i++)
        {
            var text = raw[i]?.GetValue<string>()
                ?? throw new InvalidOperationException($"matches[{i}]: expected a string.");
            var normalized = NormalizedWordMatch(text);
            if (normalized.Length == 0)
                throw new InvalidOperationException($"matches[{i}]: expected a non-empty word.");
            matches.Add(normalized);
        }
        return matches;
    }

    private static string NormalizedWordMatch(string text)
        => text.Trim(TrimmableWordChars).ToLowerInvariant();

    private static readonly char[] TrimmableWordChars = Enumerable.Range(0, char.MaxValue + 1)
        .Select(c => (char)c)
        .Where(c => char.IsWhiteSpace(c) || char.IsPunctuation(c))
        .ToArray();

    private static ToolCallResult InspectTimeline(JsonObject args)
    {
        // inspect_timeline is in OffDispatcherTools: it decodes through ffmpeg and shells out again
        // to encode PNGs, which is far too much work to run on the WinUI dispatcher. Only the reads
        // of project state below are serialized.
        var (session, timeline, playhead) = ProjectHub.RunSerialized(() =>
        {
            var s = GetSession();
            return (s, s.ActiveTimeline, PlayheadFrame(s, s.ActiveTimeline));
        });
        var endFrame = args["endFrame"]?.GetValue<int>();
        // WP-435: with no frame given at all, "what the user sees in the preview" is the playhead frame (the Mac
        // renders 0). A range with only endFrame still starts at 0, as before.
        var startFrame = Math.Max(0, args["startFrame"]?.GetValue<int>() ?? (endFrame is null ? playhead : 0));
        var maxFrames = Math.Clamp(args["maxFrames"]?.GetValue<int>() ?? 5, 1, 10);

        List<int> frames;
        if (endFrame is { } end && end > startFrame)
        {
            var span = end - startFrame;
            var count = Math.Min(maxFrames, span + 1);
            frames = Enumerable.Range(0, count)
                .Select(i => startFrame + (int)Math.Round(span * (count == 1 ? 0.0 : i / (double)(count - 1))))
                .Distinct()
                .ToList();
        }
        else
        {
            frames = [startFrame];
        }

        // Cap render size so base64 payloads stay small; aspect follows the timeline.
        const int maxDimension = 640;
        var scale = Math.Min(1.0, maxDimension / (double)Math.Max(timeline.Width, timeline.Height));
        var outW = Math.Max(1, (int)Math.Round(timeline.Width * scale));
        var outH = Math.Max(1, (int)Math.Round(timeline.Height * scale));

        // WP-399: the frames go back to the caller, so an allowed external file must still lead where
        // it did when the user allowed it; checked once per file for this call, outside the gate.
        using var renderer = new TimelineFrameRenderer(
            new FfmpegFrameSource(),
            session.Resolver.ForTransfer(),
            timelineLookup: SessionTimelineLookup.For(session));
        var content = new List<TextContent>
        {
            TextContent.FromText(JsonSerializer.Serialize(new
            {
                status = "success",
                timelineId = timeline.Id,
                fps = timeline.Fps,
                size = $"{timeline.Width}x{timeline.Height}",
                renderedSize = $"{outW}x{outH}",
                frames,
            })),
        };

        foreach (var frame in frames)
        {
            var layers = ProjectHub.RunSerialized(() =>
                TimelineFrameResolver.Resolve(timeline, frame, session.Resolver)
                    .Select(layer => new
                    {
                        clipId = layer.ClipId,
                        mediaRef = layer.MediaRef,
                        type = layer.MediaType.ToString(),
                        sourceSeconds = Math.Round(layer.SourceSeconds, 3),
                        opacity = Math.Round(layer.Opacity, 3),
                        effects = layer.Effects?.Count(e => e.Enabled) ?? 0,
                    })
                    .ToList());

            content.Add(TextContent.FromText(JsonSerializer.Serialize(new
            {
                frame,
                seconds = Math.Round(frame / (double)Math.Max(1, timeline.Fps), 3),
                visibleClips = layers,
            })));

            using var rendered = renderer.RenderAsync(timeline, frame, outW, outH).GetAwaiter().GetResult();
            if (rendered is null)
            {
                content.Add(TextContent.FromText($"frame {frame}: no visible video layers (offline media or empty timeline)."));
                continue;
            }

            var png = FfmpegPngEncoder.EncodeAsync(rendered).GetAwaiter().GetResult();
            content.Add(TextContent.FromPng(png));
        }

        return new ToolCallResult { IsError = false, Content = content };
    }

    private static object ApplyLayout(JsonObject args)
    {
        var session = GetSession();
        var timeline = session.ActiveTimeline;

        LayoutKind kind;
        try { kind = LayoutEngine.ParseKind(args["layout"]?.GetValue<string>() ?? ""); }
        catch (ArgumentException ex) { throw new InvalidOperationException(ex.Message); }
        LayoutFit fit;
        try { fit = LayoutEngine.ParseFit(args["fit"]?.GetValue<string>()); }
        catch (ArgumentException ex) { throw new InvalidOperationException(ex.Message); }

        if (args["slots"] is not JsonArray slotsArr || slotsArr.Count == 0)
            throw new InvalidOperationException("apply_layout needs a non-empty 'slots' array");

        var validSlots = LayoutEngine.Slots(kind).ToDictionary(s => s.Name, s => s, StringComparer.Ordinal);
        var entries = new List<LayoutEntry>();
        var seenSlots = new HashSet<string>(StringComparer.Ordinal);
        var seenClips = new HashSet<string>(StringComparer.Ordinal);
        bool usesMedia = false, usesClip = false;

        for (var i = 0; i < slotsArr.Count; i++)
        {
            if (slotsArr[i] is not JsonObject e) throw new InvalidOperationException($"slots[{i}] must be an object");
            var slotName = e["slot"]?.GetValue<string>() ?? throw new InvalidOperationException($"slots[{i}]: slot is required");
            if (!validSlots.TryGetValue(slotName, out var slot))
                throw new InvalidOperationException($"slots[{i}]: '{slotName}' is not a slot of layout '{LayoutEngine.RawValue(kind)}'. Slots: {string.Join(", ", validSlots.Keys)}");
            if (!seenSlots.Add(slotName))
                throw new InvalidOperationException($"slots[{i}]: duplicate slot '{slotName}'");

            var mediaRef = e["mediaRef"]?.GetValue<string>();
            var clipIds = e["clipIds"] is JsonArray ca ? ParseStringArray(ca).ToList() : null;
            if ((mediaRef is null) == (clipIds is null))
                throw new InvalidOperationException($"slots[{i}]: provide exactly one of 'mediaRef' or 'clipIds'");
            if (clipIds is not null)
            {
                if (clipIds.Count == 0) throw new InvalidOperationException($"slots[{i}]: 'clipIds' must not be empty");
                foreach (var cid in clipIds)
                    if (!seenClips.Add(cid))
                        throw new InvalidOperationException($"slots[{i}]: clip '{cid}' is assigned to more than one slot; each clip can fill only one.");
            }
            usesMedia |= mediaRef is not null;
            usesClip |= clipIds is not null;

            (double X, double Y) anchor;
            try { anchor = LayoutEngine.ResolveAnchor(e["anchor"]?.GetValue<string>(), Num(e["anchorX"]), Num(e["anchorY"])); }
            catch (ArgumentException ex) { throw new InvalidOperationException($"slots[{i}]: {ex.Message}"); }
            entries.Add(new LayoutEntry(slot, mediaRef, clipIds, anchor));
        }

        var missing = validSlots.Keys.Except(seenSlots).ToList();
        if (missing.Count > 0)
            throw new InvalidOperationException($"layout '{LayoutEngine.RawValue(kind)}' needs every slot filled. Missing: {string.Join(", ", missing)}");
        if (usesMedia && usesClip)
            throw new InvalidOperationException("apply_layout: don't mix 'mediaRef' and 'clipIds' — either place new clips (all mediaRef) or re-layout existing clips (all clipIds).");

        var startFrame = args["startFrame"]?.GetValue<int>() ?? 0;
        var duration = args["endFrame"]?.GetValue<int>() is { } endFrame ? endFrame - startFrame : 0;

        var entryBySlot = new Dictionary<string, MediaManifestEntry>(StringComparer.Ordinal);
        if (usesMedia)
        {
            if (startFrame < 0) throw new InvalidOperationException($"startFrame must be >= 0 (got {startFrame})");
            if (duration < 1) throw new InvalidOperationException("apply_layout placing new clips requires endFrame > startFrame.");
            foreach (var e in entries)
            {
                var entry = session.Manifest.Entries.FirstOrDefault(m => m.Id == e.MediaRef)
                    ?? throw new InvalidOperationException($"slot '{e.Slot.Name}': media not found: {e.MediaRef}");
                if (entry.Type is not (ClipType.video or ClipType.image))
                    throw new InvalidOperationException($"slot '{e.Slot.Name}': asset {e.MediaRef} is {entry.Type}; layout slots take video or image.");
                entryBySlot[e.Slot.Name] = entry;
            }
        }
        else
        {
            ValidateLayoutClipIds(timeline, entries);
        }

        var snapshot = TakeTimelineSnapshot(timeline);
        session.RecordBeforeEdit();
        try
        {
            var clipsBySlot = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            if (usesMedia)
            {
                // One new top track per slot, inserted in ascending-Z order so higher-Z (PiP inset) ends on top.
                var trackBySlot = new Dictionary<string, Track>(StringComparer.Ordinal);
                foreach (var slot in LayoutEngine.Slots(kind).OrderBy(s => s.Z))
                {
                    var track = new Track { Type = ClipType.video };
                    timeline.Tracks.Insert(0, track);
                    trackBySlot[slot.Name] = track;
                }
                foreach (var e in entries)
                {
                    var entry = entryBySlot[e.Slot.Name];
                    var clip = new Clip
                    {
                        MediaRef = entry.Id,
                        MediaType = entry.Type,
                        SourceClipType = entry.Type,
                        StartFrame = startFrame,
                        DurationFrames = duration,
                    };
                    trackBySlot[e.Slot.Name].Clips.Add(clip);
                    clipsBySlot[e.Slot.Name] = [clip.Id];
                }
            }
            else
            {
                foreach (var e in entries) clipsBySlot[e.Slot.Name] = e.ClipIds!;
            }

            foreach (var e in entries)
            {
                foreach (var cid in clipsBySlot[e.Slot.Name])
                {
                    var (clip, _) = FindClipWithTrack(timeline, cid);
                    if (clip is null) continue;
                    var (sourceW, sourceH) = LayoutSourceDimensions(session, clip);
                    var (transform, crop) = LayoutEngine.Place(
                        kind, e.Slot.Name, sourceW, sourceH, timeline.Width, timeline.Height,
                        fit == LayoutFit.Fit ? "fit" : "fill", null, e.Anchor.X, e.Anchor.Y);
                    clip.Transform = transform;
                    clip.Crop = crop;
                    // Static layout placement supersedes any transform/crop animation.
                    clip.PositionTrack = null;
                    clip.ScaleTrack = null;
                    clip.RotationTrack = null;
                    clip.CropTrack = null;
                }
            }

            session.MarkDirty();
            var touched = clipsBySlot.Values.SelectMany(v => v).ToList();
            var notes = new List<string>();
            if (!usesMedia)
                notes.Add("Stacking follows current track order (index 0 on top); fix with manage_tracks reorder if an inset isn't on top.");
            return GetMutationResult(session, snapshot, touched: touched,
                extra: new Dictionary<string, object> { ["layout"] = LayoutEngine.RawValue(kind), ["slots"] = clipsBySlot },
                notes: notes);
        }
        catch
        {
            session.RollbackLastRecord();
            throw;
        }
    }

    private sealed record LayoutEntry(LayoutSlot Slot, string? MediaRef, List<string>? ClipIds, (double X, double Y) Anchor);

    private static (Clip? Clip, int TrackIndex) FindClipWithTrack(Timeline timeline, string clipId)
    {
        for (var i = 0; i < timeline.Tracks.Count; i++)
        {
            var clip = timeline.Tracks[i].Clips.FirstOrDefault(c => c.Id == clipId);
            if (clip is not null) return (clip, i);
        }
        return (null, -1);
    }

    private static (double Width, double Height) LayoutSourceDimensions(ProjectSession session, Clip clip)
    {
        var entry = session.Manifest.Entries.FirstOrDefault(m => m.Id == clip.MediaRef);
        return (entry?.SourceWidth ?? 0, entry?.SourceHeight ?? 0);
    }

    private static void ValidateLayoutClipIds(Timeline timeline, List<LayoutEntry> entries)
    {
        var rangesByTrack = new Dictionary<int, List<(string Slot, int Start, int End)>>();
        var slotIntervals = new Dictionary<string, List<(int Start, int End)>>(StringComparer.Ordinal);
        foreach (var e in entries)
        {
            foreach (var cid in e.ClipIds!)
            {
                var (clip, trackIndex) = FindClipWithTrack(timeline, cid);
                if (clip is null) throw new InvalidOperationException($"slot '{e.Slot.Name}': clip not found: {cid}");
                if (clip.MediaType is not (ClipType.video or ClipType.image))
                    throw new InvalidOperationException($"slot '{e.Slot.Name}': clip {cid} is {clip.MediaType}; layout applies to video/image clips.");
                var start = clip.StartFrame;
                var end = clip.EndFrame;
                if (rangesByTrack.TryGetValue(trackIndex, out var others))
                    foreach (var o in others)
                        if (o.Slot != e.Slot.Name && start < o.End && o.Start < end)
                            throw new InvalidOperationException(
                                $"clips in slots '{o.Slot}' and '{e.Slot.Name}' are on the same track and their times overlap; only the first would render. Move them to separate tracks (or place new clips with mediaRef) so every region shows.");
                if (!rangesByTrack.TryGetValue(trackIndex, out var list)) rangesByTrack[trackIndex] = list = [];
                list.Add((e.Slot.Name, start, end));
                if (!slotIntervals.TryGetValue(e.Slot.Name, out var il)) slotIntervals[e.Slot.Name] = il = [];
                il.Add((start, end));
            }
        }
        if (entries.Count > 1)
        {
            var candidates = slotIntervals.Values.SelectMany(iv => iv.Select(x => x.Start));
            var coincides = candidates.Any(f => slotIntervals.Values.All(iv => iv.Any(x => x.Start <= f && f < x.End)));
            if (!coincides)
                throw new InvalidOperationException(
                    "the selected clips never play at the same time, so no single frame shows every region. Overlap their timeline ranges (or place new clips with mediaRef) before laying them out.");
        }
    }

    private static object InspectColor(JsonObject args)
    {
        var clipId = args["clipId"]?.GetValue<string>();
        var mediaRef = args["mediaRef"]?.GetValue<string>();
        if (clipId is null && mediaRef is null)
            throw new InvalidOperationException("inspect_color requires clipId or mediaRef.");

        var atFrame = args["atFrame"]?.GetValue<int>();
        var subjectSpec = ResolveScopeSubject(clipId, mediaRef, atFrame);
        var report = MeasureScopes(subjectSpec);
        var readout = ScopeReadout(report);
        readout["subject"] = subjectSpec.Label;

        if (args["reference"]?.GetValue<string>() is { } referenceRef)
        {
            var refReport = MeasureScopes(ResolveScopeSubject(null, referenceRef, null));
            readout["referenceGap"] = new Dictionary<string, object>
            {
                ["lumaBlack"] = Round3(report.LumaBlack - refReport.LumaBlack),
                ["lumaWhite"] = Round3(report.LumaWhite - refReport.LumaWhite),
                ["lumaMean"] = Round3(report.LumaMean - refReport.LumaMean),
                ["warmCool"] = Round3(report.WarmCoolBias - refReport.WarmCoolBias),
                ["greenMagenta"] = Round3(report.GreenMagentaBias - refReport.GreenMagentaBias),
                ["saturation"] = Round3(report.SaturationMean - refReport.SaturationMean),
            };
        }
        return readout;
    }

    /// <summary>
    /// Everything <see cref="MeasureScopes"/> needs, copied out of the session so the decode can run
    /// off the dispatcher. inspect_color is in <see cref="OffDispatcherTools"/>: only this resolve
    /// step is serialized, never the ffmpeg decode.
    /// </summary>
    private sealed record ScopeSubject(
        string Path, double SourceSeconds, IReadOnlyList<Effect>? Effects, int ClipRelativeFrame, string Label);

    private static ScopeSubject ResolveScopeSubject(
        string? clipId, string? mediaRef, int? atFrame) => ProjectHub.RunSerialized(() =>
    {
        var session = GetSession();
        var subjectLabel = MeasureScopesResolve(
            session, clipId, mediaRef, atFrame,
            out var path, out var sourceSeconds, out var effects, out var clipRelative);
        return new ScopeSubject(path, sourceSeconds, effects, clipRelative, subjectLabel);
    });

    private static string MeasureScopesResolve(
        ProjectSession session, string? clipId, string? mediaRef, int? atFrame,
        out string path, out double sourceSeconds, out IReadOnlyList<Effect>? effects, out int clipRelativeFrame)
    {
        string subjectLabel;
        var timeline = session.ActiveTimeline;
        Clip? clip = null;
        MediaManifestEntry? entry;

        if (clipId is not null)
        {
            clip = timeline.Tracks.SelectMany(t => t.Clips).FirstOrDefault(c => c.Id == clipId)
                ?? throw new InvalidOperationException($"Clip not found: {clipId}");
            if (clip.MediaType is not (ClipType.video or ClipType.image))
                throw new InvalidOperationException($"inspect_color needs a video/image clip; {clipId} is {clip.MediaType}.");
            entry = session.Manifest.Entries.FirstOrDefault(e => e.Id == clip.MediaRef)
                ?? throw new InvalidOperationException($"Media not found for clip {clipId}.");
            var frame = Math.Clamp(
                atFrame ?? clip.StartFrame + clip.DurationFrames / 2,
                clip.StartFrame, Math.Max(clip.StartFrame, clip.EndFrame - 1));
            var speed = Math.Max(clip.Speed, 0.0001);
            sourceSeconds = (clip.TrimStartFrame + (frame - clip.StartFrame) * speed) / (double)Math.Max(1, timeline.Fps);
            subjectLabel = $"clip {clipId} (graded) at frame {frame}";
        }
        else
        {
            entry = session.Manifest.Entries.FirstOrDefault(e => e.Id == mediaRef)
                ?? throw new InvalidOperationException($"Media not found: {mediaRef}");
            sourceSeconds = entry.Type == ClipType.image ? 0 : Math.Max(0, (atFrame ?? 0) / (double)Math.Max(1, timeline.Fps));
            subjectLabel = $"media {mediaRef} (ungraded) at {sourceSeconds:F2}s";
        }

        var resolved = ResolveAssetPath(session, entry);
        if (resolved is null || !File.Exists(resolved))
            throw new InvalidOperationException("Media is offline; cannot measure color.");

        path = resolved;
        // Copy the effect stack: the decode runs off the dispatcher and must not read a list the
        // user may be editing.
        effects = clip?.Effects is { Count: > 0 } stack ? stack.ToList() : null;
        clipRelativeFrame = clip is null ? 0 : Math.Max(0, (atFrame ?? clip.StartFrame) - clip.StartFrame);
        return subjectLabel;
    }

    /// <summary>Decodes and measures. Runs off the dispatcher; touches no session state.</summary>
    private static ColorScopeReport MeasureScopes(ScopeSubject subject)
    {
        using var decoded = new FfmpegFrameSource()
            .GetFrameAsync(subject.Path, subject.SourceSeconds).GetAwaiter().GetResult();
        VideoFrame measured = decoded;
        if (subject.Effects is { Count: > 0 })
            measured = new CpuEffectsRenderer().Apply(decoded, subject.Effects, subject.ClipRelativeFrame);
        try
        {
            return ColorScopes.Measure(measured);
        }
        finally
        {
            if (!ReferenceEquals(measured, decoded)) measured.Dispose();
        }
    }

    private static Dictionary<string, object> ScopeReadout(ColorScopeReport r)
    {
        static double[] Rgb(RgbTriple t) => [Math.Round(t.R, 4), Math.Round(t.G, 4), Math.Round(t.B, 4)];
        return new Dictionary<string, object>
        {
            ["status"] = "success",
            ["luma"] = new Dictionary<string, object>
            {
                ["black"] = Round4(r.LumaBlack),
                ["white"] = Round4(r.LumaWhite),
                ["mean"] = Round4(r.LumaMean),
                ["clipLowPct"] = Round3(r.ClipLow * 100),
                ["clipHighPct"] = Round3(r.ClipHigh * 100),
                ["histogram16"] = r.LumaHistogram.Select(v => Math.Round(v, 5)).ToArray(),
            },
            ["rgb"] = new Dictionary<string, object>
            {
                ["mean"] = Rgb(r.MeanRgb),
                ["black"] = Rgb(r.BlackRgb),
                ["white"] = Rgb(r.WhiteRgb),
            },
            ["zones"] = new Dictionary<string, object>
            {
                ["shadows"] = Rgb(r.ShadowRgb),
                ["mids"] = Rgb(r.MidRgb),
                ["highs"] = Rgb(r.HighRgb),
            },
            ["balance"] = new Dictionary<string, object>
            {
                ["warmCool"] = Round4(r.WarmCoolBias),
                ["greenMagenta"] = Round4(r.GreenMagentaBias),
            },
            ["saturation"] = Round4(r.SaturationMean),
            ["colorfulPct"] = Round3(r.ColorfulPct * 100),
            ["hueHistogram12"] = r.HueHistogram.Select(v => Math.Round(v, 5)).ToArray(),
        };
    }

    private static double Round3(double v) => Math.Round(v, 3);
    private static double Round4(double v) => Math.Round(v, 4);

    private readonly record struct SilenceDetectClip(
        int TrackIndex,
        string ClipId,
        double Speed,
        int TrimStartFrame,
        int DurationFrames,
        int StartFrame,
        int EndFrame,
        string Path);

    private static object RemoveSilence(SilenceDetectionOptions options)
    {
        var snap = ProjectHub.RunSerialized(() =>
        {
            var session = GetSession();
            var timeline = session.ActiveTimeline;
            var fps = Math.Max(1, timeline.Fps);
            var entriesById = session.Manifest.Entries
                .GroupBy(e => e.Id, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

            var list = new List<SilenceDetectClip>();
            for (var trackIndex = 0; trackIndex < timeline.Tracks.Count; trackIndex++)
            {
                foreach (var clip in timeline.Tracks[trackIndex].Clips)
                {
                    if (clip.MediaType is not (ClipType.video or ClipType.audio)) continue;
                    if (!entriesById.TryGetValue(clip.MediaRef, out var entry) || entry.HasAudio == false) continue;
                    var path = ResolveAssetPath(session, entry);
                    if (path is null || !File.Exists(path)) continue;
                    list.Add(new SilenceDetectClip(
                        trackIndex,
                        clip.Id,
                        clip.Speed,
                        clip.TrimStartFrame,
                        clip.DurationFrames,
                        clip.StartFrame,
                        clip.EndFrame,
                        path));
                }
            }

            // WP-172: carriers hold no manifest entry, so the loop above never sees nested
            // audio. Plan the audible inner files here (same HasAudio/offline policy); the
            // decode+map pass runs below, off the gate, like the direct-clip detects.
            var nestedPlan = NestedSilenceScanner.Plan(
                timeline,
                SessionTimelineLookup.For(session),
                mediaRef =>
                {
                    if (!entriesById.TryGetValue(mediaRef, out var nestedEntry) || nestedEntry.HasAudio == false)
                        return null;
                    var nestedPath = ResolveAssetPath(session, nestedEntry);
                    return nestedPath is null || !File.Exists(nestedPath) ? null : nestedPath;
                });

            return (fps, list, nestedPlan);
        });

        var fps = snap.fps;
        var detector = new SilenceDetector();
        var rangesByTrack = new Dictionary<int, List<FrameRange>>();
        var hits = new List<(string ClipId, FrameRange Range)>();
        foreach (var clip in snap.list)
        {
            IReadOnlyList<SilenceRange> silence;
            try { silence = detector.DetectAsync(clip.Path, options).GetAwaiter().GetResult(); }
            catch (InvalidOperationException) { continue; }

            var speed = Math.Max(clip.Speed, 0.0001);
            var visibleStart = (double)clip.TrimStartFrame;
            var visibleEnd = visibleStart + clip.DurationFrames * speed;
            foreach (var range in silence)
            {
                var srcStart = Math.Max(range.Start * fps, visibleStart);
                var srcEnd = Math.Min(range.End * fps, visibleEnd);
                if (srcEnd <= srcStart) continue;
                int ToTimeline(double srcFrame) => (int)Math.Round(
                    clip.StartFrame + (srcFrame - visibleStart) / speed, MidpointRounding.AwayFromZero);
                var ts = Math.Clamp(ToTimeline(srcStart), clip.StartFrame, clip.EndFrame);
                var te = Math.Clamp(ToTimeline(srcEnd), clip.StartFrame, clip.EndFrame);
                if (te <= ts) continue;
                if (!rangesByTrack.TryGetValue(clip.TrackIndex, out var list))
                    rangesByTrack[clip.TrackIndex] = list = [];
                list.Add(new FrameRange(ts, te));
                hits.Add((clip.ClipId, new FrameRange(ts, te)));
            }
        }

        // WP-172: fold the nested hits into the same per-track ranges. The carrier clip id is
        // the involved id, so link expansion and ripple cuts treat the carrier as one cuttable
        // clip on its parent track — exactly what the cut phase below expects.
        if (snap.nestedPlan.Count > 0)
        {
            var nestedHits = new NestedSilenceScanner()
                .ScanAsync(snap.nestedPlan, options).GetAwaiter().GetResult();
            foreach (var hit in nestedHits)
            {
                if (!rangesByTrack.TryGetValue(hit.ParentTrackIndex, out var nestedList))
                    rangesByTrack[hit.ParentTrackIndex] = nestedList = [];
                nestedList.Add(hit.ParentRange);
                hits.Add((hit.CarrierClipId, hit.ParentRange));
            }
        }

        if (rangesByTrack.Count == 0)
            throw new InvalidOperationException(
                "No dead air on the timeline. The audio has no quiet, speech-free sections, or a transcription/audio backend is unavailable.");

        return ProjectHub.RunSerialized(() =>
        {
            var session = GetSession();
            var timeline = session.ActiveTimeline;
            var mergedByTrack = rangesByTrack.ToDictionary(kv => kv.Key, kv => RippleEngine.MergeRanges(kv.Value));
            var primaryTrack = mergedByTrack.MaxBy(kv => kv.Value.Sum(r => r.Length)).Key;
            var primaryIds = timeline.Tracks[primaryTrack].Clips.Select(c => c.Id).ToHashSet(StringComparer.Ordinal);
            var expanded = LinkGroup.Expand(timeline, hits.Where(h => primaryIds.Contains(h.ClipId)).Select(h => h.ClipId));

            // WP-337: only dead air found in the primary clips and their linked partners is cut. A quiet stretch in an
            // unrelated clip on a partner track is not theirs to remove.
            var merged = RippleEngine.MergeRanges(hits.Where(h => expanded.Contains(h.ClipId)).Select(h => h.Range).ToList());
            if (merged.Count == 0)
                merged = mergedByTrack[primaryTrack];

            // A cut removes the same frames from every track it reaches, so dead air is only where every clip with sound
            // on those tracks is quiet: a multicam camera's silent scratch audio must not cut the mic (WP-337 T1, Astra).
            // A clip scanned without result counts as sound everywhere; a muted track's clips are not heard.
            var sounding = snap.list.Select(c => c.ClipId).Concat(snap.nestedPlan.Select(s => s.CarrierClipId)).ToHashSet(StringComparer.Ordinal);
            var quiet = hits.GroupBy(h => h.ClipId, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => RippleEngine.MergeRanges(g.Select(h => h.Range).ToList()), StringComparer.Ordinal);
            var reached = LinkedCutTracks(session, [primaryTrack], merged);
            var heard = reached
                .Where(t => !timeline.Tracks[t].Muted)
                .SelectMany(t => timeline.Tracks[t].Clips)
                .Where(c => sounding.Contains(c.Id) && ClipOverlapsRanges(c, merged))
                .SelectMany(c => SubtractRanges([new FrameRange(c.StartFrame, c.EndFrame)], quiet.GetValueOrDefault(c.Id) ?? []))
                .ToList();
            merged = SubtractRanges(merged, heard);
            if (merged.Count == 0)
                throw new InvalidOperationException(
                    "No dead air to remove: every quiet section is audible in another clip the cut would reach, such as a multicam mic.");
            var cutTracks = LinkedCutTracks(session, [primaryTrack], merged);

            var snapshot = TakeTimelineSnapshot(timeline);
            var applied = ApplyRippleCutsOnTracks(session, cutTracks, merged);
            if (!applied.Ok)
                throw new InvalidOperationException(applied.Error ?? "Ripple delete refused.");

            var extra = new Dictionary<string, object>
            {
                ["sectionsRemoved"] = merged.Count,
                ["removedFrames"] = merged.Sum(r => r.Length),
                ["trackIndex"] = primaryTrack,
            };
            var notes = new List<string>();
            var skippedTracks = mergedByTrack.Keys.Except(cutTracks).OrderBy(i => i).ToList();
            if (skippedTracks.Count > 0)
            {
                notes.Add(
                    $"Removed dead air from track {string.Join(", ", cutTracks.OrderBy(i => i))}; "
                    + $"skipped unlinked track(s) {string.Join(", ", skippedTracks)}.");
            }

            return GetMutationResult(session, snapshot, extra: extra, notes: notes);
        });
    }

    private static object SyncClips(JsonObject args)
    {
        var session = GetSession();
        var referenceClipId = args["referenceClipId"]?.GetValue<string>()
            ?? throw new InvalidOperationException("sync_clips: referenceClipId is required");

        var targets = new List<string>();
        if (args["targetClipIds"] is JsonArray arr)
        {
            foreach (var n in arr)
            {
                if (n?.GetValue<string>() is { Length: > 0 } id)
                    targets.Add(id);
            }
        }
        if (args["targetClipId"]?.GetValue<string>() is { Length: > 0 } single)
            targets.Add(single);
        if (targets.Count == 0)
            throw new InvalidOperationException("sync_clips: provide targetClipId or targetClipIds.");

        var modeRaw = args["mode"]?.GetValue<string>() ?? "auto";
        if (!Enum.TryParse<ClipSyncEngine.SyncMode>(modeRaw, ignoreCase: true, out var mode))
            throw new InvalidOperationException("sync_clips: mode must be auto, audio, or timecode.");

        var searchWindow = args["searchWindowSeconds"]?.GetValue<double>()
            ?? ClipSyncEngine.DefaultSearchWindowSeconds;
        if (searchWindow <= 0)
            throw new InvalidOperationException("sync_clips: searchWindowSeconds must be > 0.");

        var minConfidence = args["minConfidence"]?.GetValue<double>()
            ?? ClipSyncEngine.DefaultMinConfidence;

        string? Resolve(string mediaRef)
        {
            var entry = session.Manifest.Entries.FirstOrDefault(e => e.Id == mediaRef);
            return entry is null ? null : ResolveAssetPath(session, entry);
        }

        var engine = new ClipSyncEngine(Resolve);
        var snapshot = TakeTimelineSnapshot(session.ActiveTimeline);
        var report = engine.SyncAsync(
            session,
            referenceClipId,
            targets,
            mode,
            searchWindow,
            minConfidence).GetAwaiter().GetResult();

        if (report.Synced.Count == 0)
        {
            var reason = report.Failures.FirstOrDefault()?.Reason ?? "no clips aligned";
            throw new InvalidOperationException("sync_clips: " + reason);
        }

        var extra = new Dictionary<string, object>
        {
            ["referenceClipId"] = referenceClipId,
            ["synced"] = report.Synced.Select(s => new
            {
                clipId = s.ClipId,
                offsetFrames = s.OffsetFrames,
                confidence = s.Confidence,
                method = s.Method,
            }).ToList(),
        };
        if (report.ShiftedFrames > 0)
            extra["shiftedFrames"] = report.ShiftedFrames;
        if (report.Failures.Count > 0)
        {
            extra["failed"] = report.Failures.Select(f => new
            {
                clipId = f.ClipId,
                reason = f.Reason,
            }).ToList();
        }

        return GetMutationResult(
            session,
            snapshot,
            touched: report.Synced.Select(s => s.ClipId).ToList(),
            extra: extra);
    }

    private static object DetectBeats(JsonObject args)
    {
        var mediaRef = args["mediaRef"]?.GetValue<string>() ?? throw new InvalidOperationException("mediaRef is required");
        var start = Num(args["startSeconds"]);
        var end = Num(args["endSeconds"]);

        var path = ProjectHub.RunSerialized(() =>
        {
            var session = GetSession();
            var entry = session.Manifest.Entries.FirstOrDefault(e => e.Id == mediaRef)
                ?? throw new InvalidOperationException($"Media asset not found: {mediaRef}");
            if (entry.Type == ClipType.image)
                throw new InvalidOperationException($"Media {mediaRef} is an image; detect_beats needs audio or video.");

            var resolved = ResolveAssetPath(session, entry);
            if (resolved is null || !File.Exists(resolved))
                throw new InvalidOperationException($"Media {mediaRef} is offline; cannot analyze beats.");
            return resolved;
        });

        var analysis = new EnergyFluxBeatDetector().DetectAsync(path).GetAwaiter().GetResult();

        bool InWindow(double t) => (start is null || t >= start.Value) && (end is null || t <= end.Value);
        var beats = analysis.Beats.Where(InWindow).Select(t => Math.Round(t, 3)).ToList();
        var downbeats = analysis.Downbeats.Where(InWindow).Select(t => Math.Round(t, 3)).ToList();

        return new
        {
            status = "success",
            mediaRef,
            bpm = Math.Round(analysis.Bpm, 2),
            beatCount = beats.Count,
            beats,
            downbeats,
            localOnly = true,
            note = "Detected on-device via FFmpeg energy-flux analysis. Times are seconds in source media.",
        };
    }

    private static object UpscaleMedia(JsonObject args)
    {
        if (TryRouteMediaSubmit(args, "upscale", out var hosted))
            return hosted!;
        var session = GetSession();
        var mediaRef = args["mediaRef"]?.GetValue<string>() ?? throw new InvalidOperationException("mediaRef is required");
        var source = session.Manifest.Entries.FirstOrDefault(e => e.Id == mediaRef)
            ?? throw new InvalidOperationException($"Media asset not found: {mediaRef}");
        if (source.Type == ClipType.audio)
            throw new InvalidOperationException($"Media {mediaRef} is audio; upscale_media needs a video or image asset.");
        // WP-399: refused before a placeholder or a job exists when the file is one the project may not hand on.
        var sourcePath = TransferPath(session, source, mediaRef);
        if (sourcePath is null || !File.Exists(sourcePath))
            throw new InvalidOperationException($"Media {mediaRef} is offline; cannot upload it for upscaling.");

        _ = GetGenerationService();
        var modelId = args["model"]?.GetValue<string>() ?? GetDefaultModel(GenerationKind.Upscale, "upscale-v1");
        var placeholderId = Guid.NewGuid().ToString().ToUpperInvariant();
        var ext = source.Type == ClipType.image ? "png" : "mp4";
        var placeholder = new MediaManifestEntry
        {
            Id = placeholderId,
            Name = source.Name + " (upscaled)",
            Type = source.Type,
            Source = MediaSource.Project($"media/gen-{placeholderId[..8]}.{ext}"),
            Duration = source.Duration,
            HasAudio = source.HasAudio,
            FolderId = source.FolderId,
            GenerationStatus = "preparing",
            GenerationInput = new GenerationInput
            {
                Prompt = $"Upscale of {source.Name}",
                Model = modelId,
                CreatedAt = DateTimeOffset.UtcNow,
            },
        };

        lock (_sessionLock)
        {
            session.Manifest.Entries.Add(placeholder);
            session.MarkDirty();
            session.Save();
        }
        var packagePath = session.PackagePath
            ?? throw new InvalidOperationException("Open project has no package path.");
        var sourceDuration = source.Duration;

        RunGenerationBackground(session, placeholderId, async token =>
        {
            var service = GetGenerationService();
            var client = service.Client;
            UpdatePlaceholderStatus(session, placeholderId, "generating", jobId: null);

            var uploaded = await UploadReferencesAsync(session, client, [mediaRef], token);
            if (uploaded.Count == 0)
                throw new InvalidOperationException("Failed to upload the source asset for upscaling.");

            var param = new UpscaleGenerationParams
            {
                SourceUrl = uploaded[0],
                DurationSeconds = (int)Math.Ceiling(sourceDuration),
            };
            var jobId = await client.SubmitGenerationAsync(modelId, param, null, token);
            UpdatePlaceholderStatus(session, placeholderId, "generating", jobId: jobId);

            var completedJob = await service.PollJobAsync(jobId, TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(10), token);
            if (completedJob.ResultUrls is not { Count: > 0 })
                throw new InvalidOperationException("Upscale succeeded but returned no result URLs.");

            UpdatePlaceholderStatus(session, placeholderId, "downloading", jobId: jobId, resultUrls: completedJob.ResultUrls);
            var landedPath = await service.DownloadAndLandAssetAsync(completedJob.ResultUrls[0], packagePath, placeholderId, token);

            double? duration = null; int? width = null, height = null; double? fps = null;
            try
            {
                var meta = await new FfmpegMediaProbe().ProbeAsync(landedPath);
                if (meta.DurationSeconds > 0) duration = meta.DurationSeconds;
                width = meta.Width; height = meta.Height; fps = meta.Fps;
            }
            catch { /* keep placeholder metadata */ }

            ProjectHub.RunSerialized(() =>
            {
                lock (_sessionLock)
                {
                    var entry = session.Manifest.Entries.FirstOrDefault(e => e.Id == placeholderId);
                    if (entry is null) return;
                    var hadSize = HasSourceSize(entry);
                    entry.Source = MediaSource.Project("media/" + Path.GetFileName(landedPath));
                    entry.GenerationStatus = CompletedGenerationStatus();
                    if (duration.HasValue) entry.Duration = duration.Value;
                    if (width.HasValue) entry.SourceWidth = width;
                    if (height.HasValue) entry.SourceHeight = height;
                    if (fps.HasValue) entry.SourceFps = fps;
                    RefitClipsPlacedBeforeSize(session, entry, hadSize);
                    session.MarkDirty();
                    session.Save();
                }
            });
        });

        return GenerationAccepted(
            placeholderId,
            $"Upscale started. Placeholder asset ID: {placeholderId}. Model: {modelId}. Poll get_media pending=true for completion.",
            new Dictionary<string, object> { ["sourceMediaRef"] = mediaRef });
    }

    private static object ManageExports(JsonObject args)
    {
        var action = args["action"]?.GetValue<string>()
            ?? throw new InvalidOperationException("manage_exports: action must be list or cancel");

        switch (action)
        {
            case "list":
            {
                if (args["jobId"] != null)
                    throw new InvalidOperationException("manage_exports: jobId only applies to cancel");
                var jobs = _exportQueue.Jobs; // newest-first
                var waitingIds = jobs.Where(j => j.State == ExportJobState.Waiting).Select(j => j.Id).ToList();
                var exports = jobs.Select(job =>
                {
                    var row = new Dictionary<string, object>
                    {
                        ["jobId"] = job.Id,
                        ["filename"] = Path.GetFileName(job.OutputPath),
                        ["path"] = job.OutputPath,
                        ["status"] = job.State.ReportName(),
                        ["progress"] = (int)Math.Round(job.Progress * 100),
                    };
                    var queueIndex = waitingIds.IndexOf(job.Id);
                    if (queueIndex >= 0) row["queuePosition"] = queueIndex + 1;
                    if (job.TotalFrames > 0) row["frames"] = new[] { job.CurrentFrame, job.TotalFrames };
                    if (job.Error is { } err) row["error"] = err;
                    AddOfflineExportFields(job, row);
                    return row;
                }).ToList();
                return new { exports };
            }
            case "cancel":
            {
                var jobId = args["jobId"]?.GetValue<string>();
                if (string.IsNullOrWhiteSpace(jobId))
                    throw new InvalidOperationException("manage_exports: cancel requires a valid jobId");
                var job = _exportQueue.TryGet(jobId)
                    ?? throw new InvalidOperationException("manage_exports: export not found");

                string responseStatus;
                bool cancellationRequested;
                switch (job.State)
                {
                    case ExportJobState.Waiting:
                        _exportQueue.Cancel(jobId);
                        responseStatus = "removed";
                        cancellationRequested = true;
                        break;
                    case ExportJobState.Rendering:
                        cancellationRequested = _exportQueue.Cancel(jobId);
                        responseStatus = cancellationRequested ? "canceling" : job.State.ReportName();
                        break;
                    default: // Completed / Failed / Cancelled
                        responseStatus = job.State.ReportName();
                        cancellationRequested = false;
                        break;
                }
                return new
                {
                    jobId = job.Id,
                    filename = Path.GetFileName(job.OutputPath),
                    status = responseStatus,
                    cancellationRequested,
                };
            }
            default:
                throw new InvalidOperationException("manage_exports: action must be list or cancel");
        }
    }

    private static VideoCodec ParseCodec(string? s) => s switch
    {
        "H.265" or "h265" or "HEVC" => VideoCodec.H265,
        "ProRes" or "prores" => VideoCodec.ProRes,
        "HEVC 10-bit HDR" or "HEVC HDR" or "HDR" or "hdr" => VideoCodec.HevcHdr,
        _ => VideoCodec.H264,
    };

    private static ExportResolution ParseResolution(string? s) => s switch
    {
        "720p" => ExportResolution.R720p,
        "1080p" => ExportResolution.R1080p,
        "2K" => ExportResolution.R1440p,
        "4K" => ExportResolution.R4k,
        _ => ExportResolution.MatchTimeline,
    };

    private static int? ReadOptionalInt(JsonNode? node, string label)
    {
        if (node is null)
            return null;
        if (node is JsonValue value)
        {
            if (value.TryGetValue<int>(out var i))
                return i;
            if (value.TryGetValue<long>(out var l) && l is >= int.MinValue and <= int.MaxValue)
                return (int)l;
            if (value.TryGetValue<double>(out var d)
                && d == Math.Truncate(d)
                && d is >= int.MinValue and <= int.MaxValue)
                return (int)d;
        }

        throw new InvalidOperationException($"{label} must be an integer");
    }

    private static double? Num(JsonNode? n)
    {
        if (n is JsonValue v)
        {
            if (v.TryGetValue<double>(out var d)) return d;
            if (v.TryGetValue<long>(out var l)) return l;
            if (v.TryGetValue<int>(out var i)) return i;
            if (v.TryGetValue<decimal>(out var m)) return (double)m;
        }
        return null;
    }

    private static (int TrimStart, int Duration) ResolveSourceSpan(
        MediaManifestEntry? entry,
        ClipType entryType,
        int fps,
        JsonNode? sourceNode,
        int? durationOrEndDelta,
        string path,
        string framesLabel)
    {
        double[]? source = null;
        if (sourceNode is JsonArray arr)
        {
            if (arr.Count != 2)
                throw new InvalidOperationException($"{path}: source must be [startSeconds, endSeconds] (got {arr.Count} elements)");
            var startSec = Num(arr[0]) ?? throw new InvalidOperationException($"{path}: source[0] must be a number");
            var endSec = Num(arr[1]) ?? throw new InvalidOperationException($"{path}: source[1] must be a number");
            source = [startSec, endSec];
        }

        if (source is not null && durationOrEndDelta is not null)
            throw new InvalidOperationException($"{path}: set source OR {framesLabel}, not both — source picks a span of the asset, {framesLabel} an exact timeline length.");

        fps = Math.Max(1, fps);
        var isStill = entryType == ClipType.image;
        var assetSeconds = entry?.Duration ?? 0;
        int SecondsToFrame(double s) => (int)Math.Round(s * fps);

        if (source is not null)
        {
            if (assetSeconds <= 0 && !isStill)
                throw new InvalidOperationException($"{path}: source needs a known source length; this asset has none. Use {framesLabel}.");
            var start = Math.Max(source[0], 0);
            var end = isStill ? source[1] : Math.Min(source[1], assetSeconds);
            if (end <= start)
                throw new InvalidOperationException($"{path}: source end ({source[1]}) must be greater than start ({source[0]})");
            var trimStart = SecondsToFrame(start);
            var duration = Math.Max(1, SecondsToFrame(end) - trimStart);
            return (trimStart, duration);
        }

        if (durationOrEndDelta is { } d)
        {
            if (d < 1)
                throw new InvalidOperationException($"{path}: {framesLabel} must span at least 1 frame");
            return (0, d);
        }

        if (assetSeconds > 0)
            return (0, Math.Max(1, SecondsToFrame(assetSeconds)));
        return (0, 150);
    }

    private static string DefaultExportPath(ProjectSession session, string mode)
    {
        var downloads = EnvCompat.Get("TEMNO_DOWNLOADS_DIR");
        if (string.IsNullOrWhiteSpace(downloads))
            downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        Directory.CreateDirectory(downloads);
        var packagePath = session.PackagePath ?? "";
        var name = string.IsNullOrEmpty(packagePath)
            ? "Untitled Project"
            : Path.GetFileNameWithoutExtension(packagePath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var ext = mode switch
        {
            "fcpxml" => ".fcpxml",
            "xml" => ".xml",
            "palmier" => "." + ProjectConstants.FileExtension,
            _ => ".mp4",
        };
        return Path.Combine(downloads, name + ext);
    }

    /// <summary>
    /// An omitted outputPath never replaces anything (WP-398, the Mac's <c>uniqueExportURL</c>):
    /// a taken name gains " 2", " 3", … before its extension.
    /// </summary>
    private static string UniqueExportPath(string path)
    {
        bool Taken(string candidate)
            => ExportDestination.IsOccupied(candidate)
                || _exportQueue.IsDestinationReserved(candidate)
                || InFlightDestinations.Contains(candidate);

        if (!Taken(path))
            return path;
        var directory = Path.GetDirectoryName(path)!;
        var stem = Path.GetFileNameWithoutExtension(path);
        var extension = Path.GetExtension(path);
        for (var index = 2; index < 10_000; index++)
        {
            var candidate = Path.Combine(directory, $"{stem} {index}{extension}");
            if (!Taken(candidate))
                return candidate;
        }

        throw new IOException($"Could not find a free export name beside {path}.");
    }

    /// <summary>
    /// WP-398: settles the path export_project writes, then decides against what is there. In
    /// palmier mode the package name is settled first (a new package gains .temno), so overwrite=false
    /// is judged on the folder that would be replaced. overwrite=false refuses anything that exists.
    /// No mode writes inside a .temno or .palmier folder, so an export cannot plant the project.json
    /// that would make a folder look like a package; the file modes also stay out of the open package
    /// under any alias. A package export replaces only a project package; the other modes never
    /// write over a folder.
    /// </summary>
    private static string SettleExportDestination(string mode, string requested, bool overwrite, string? openPackage)
    {
        var destination = mode == "palmier"
            ? ExportDestination.ResolvePackagePath(requested)
            : requested;
        if (ExportDestination.EnclosingPackageFolder(destination) is { } packageFolder)
        {
            throw new InvalidOperationException(
                $"outputPath is inside a project package folder ({packageFolder}): {destination}. "
                + "export_project never writes into a .temno or .palmier folder; export beside it.");
        }

        if (mode != "palmier"
            && !string.IsNullOrEmpty(openPackage)
            && Path.GetDirectoryName(Path.GetFullPath(destination)) is { } parent
            && ExportDestination.IsWithin(openPackage, parent))
        {
            throw new InvalidOperationException(
                $"outputPath is inside the open project's package ({openPackage}): {destination}. Export beside it.");
        }

        var kind = ExportDestination.Inspect(destination);
        if (kind != ExportDestinationKind.Absent && !overwrite)
            throw new InvalidOperationException($"outputPath already exists and overwrite=false: {destination}");

        if (mode == "palmier")
        {
            if (ExportDestination.PackageRefusal(destination, kind, replaceExistingPackage: true) is { } refusal)
                throw new InvalidOperationException(refusal);
        }
        else if (kind is ExportDestinationKind.Folder or ExportDestinationKind.Package or ExportDestinationKind.FolderLink)
        {
            throw new InvalidOperationException(
                $"outputPath is a folder: {destination}. {mode} mode writes a file, so name the file.");
        }
        else if (kind is ExportDestinationKind.FileLink)
        {
            // Writing through a link would land wherever it points, past the checks above.
            throw new InvalidOperationException(
                $"outputPath is a symbolic link, which export_project never writes through: {destination}. Name the file itself.");
        }

        return destination;
    }

    private static List<List<double>>? ParseCurveArray(JsonNode? node)
    {
        if (node is not JsonArray arr || arr.Count == 0) return null;
        var points = new List<List<double>>();
        foreach (var pt in arr)
        {
            double? x = null, y = null;
            if (pt is JsonArray pair && pair.Count >= 2) { x = Num(pair[0]); y = Num(pair[1]); }
            else if (pt is JsonObject o) { x = Num(o["x"]); y = Num(o["y"]); }
            if (x.HasValue && y.HasValue) points.Add([x.Value, y.Value]);
        }
        return points.Count > 0 ? points : null;
    }

    private static List<HueTargetInput>? ParseHueTargets(JsonNode? node)
    {
        if ((node as JsonObject)?["targets"] is not JsonArray targets) return null;
        var list = new List<HueTargetInput>();
        foreach (var t in targets)
        {
            if (t is not JsonObject o || Num(o["targetHue"]) is not { } th) continue;
            list.Add(new HueTargetInput { TargetHue = th, HueShift = Num(o["hueShift"]), SatScale = Num(o["satScale"]), LumShift = Num(o["lumShift"]) });
        }
        return list.Count > 0 ? list : null;
    }

    private static LutInput? ParseLut(JsonNode? node)
    {
        if (node is not JsonObject o) return null;
        var path = o["path"]?.GetValue<string>();
        if (string.IsNullOrEmpty(path)) return null;
        return new LutInput { Path = path, Strength = Num(o["strength"]) ?? Num(o["intensity"]) };
    }

    private static object ManageTracks(JsonObject args)
    {
        var session = GetSession();
        var snapshot = TakeTimelineSnapshot(session.ActiveTimeline);
        if (args.ContainsKey("reorder") || args.ContainsKey("set") || args.ContainsKey("remove"))
        {
            var tracks = session.ActiveTimeline.Tracks;
            string ResolveTrackId(JsonObject entry, string path)
            {
                var id = entry["trackId"]?.GetValue<string>();
                var index = entry["index"]?.GetValue<int>();
                if (!string.IsNullOrEmpty(id) && index is null)
                {
                    if (tracks.All(t => !string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase)))
                        throw new InvalidOperationException($"{path}: pass one current trackId or index");
                    return id;
                }
                if (string.IsNullOrEmpty(id) && index is { } i)
                {
                    if (i < 0 || i >= tracks.Count)
                        throw new InvalidOperationException($"{path}: track index {i} out of range (timeline has {tracks.Count} tracks)");
                    return tracks[i].Id;
                }
                throw new InvalidOperationException($"{path}: pass one current trackId or index");
            }

            var reorders = new List<(string Id, int To)>();
            if (args["reorder"] is JsonArray reorderArr)
            {
                for (var i = 0; i < reorderArr.Count; i++)
                {
                    var entry = reorderArr[i] as JsonObject
                        ?? throw new InvalidOperationException($"reorder[{i}] must be an object");
                    var to = entry["to"]?.GetValue<int>()
                        ?? throw new InvalidOperationException($"reorder[{i}]: 'to' is required and must be an integer");
                    reorders.Add((ResolveTrackId(entry, $"reorder[{i}]"), to));
                }
            }

            var flagSets = new List<(string Id, bool? Muted, bool? Hidden, bool? SyncLocked)>();
            if (args["set"] is JsonArray setArr)
            {
                for (var i = 0; i < setArr.Count; i++)
                {
                    var entry = setArr[i] as JsonObject
                        ?? throw new InvalidOperationException($"set[{i}] must be an object");
                    var muted = entry["muted"]?.GetValue<bool>();
                    var hidden = entry["hidden"]?.GetValue<bool>();
                    var syncLocked = entry["syncLocked"]?.GetValue<bool>();
                    if (muted is null && hidden is null && syncLocked is null)
                        throw new InvalidOperationException($"set[{i}]: pass at least one of muted, hidden, syncLocked");
                    flagSets.Add((ResolveTrackId(entry, $"set[{i}]"), muted, hidden, syncLocked));
                }
            }

            var removeIds = new List<string>();
            if (args["remove"] is JsonArray removeArr)
            {
                for (var i = 0; i < removeArr.Count; i++)
                {
                    var path = $"remove[{i}]";
                    if (removeArr[i] is JsonObject obj)
                        removeIds.Add(ResolveTrackId(obj, path));
                    else if (removeArr[i] is JsonValue val && val.TryGetValue<int>(out var index))
                    {
                        if (index < 0 || index >= tracks.Count)
                            throw new InvalidOperationException($"{path}: track index {index} out of range (timeline has {tracks.Count} tracks)");
                        removeIds.Add(tracks[index].Id);
                    }
                    else
                        throw new InvalidOperationException($"{path} must be an integer index or track selector object");
                }
            }

            var result = EditCommands.ApplyTrackEdits(session, reorders, flagSets, removeIds);
            if (!result.Ok)
                throw new InvalidOperationException(result.Error ?? "Failed to manage tracks");

            var extra = new Dictionary<string, object>
            {
                ["tracks"] = session.ActiveTimeline.Tracks.Select((t, i) =>
                {
                    var row = new Dictionary<string, object>
                    {
                        ["trackId"] = t.Id,
                        ["index"] = i,
                        ["label"] = TrackLabel(session.ActiveTimeline, i),
                        ["type"] = t.Type.ToString(),
                    };
                    if (t.Muted) row["muted"] = true;
                    if (t.Hidden) row["hidden"] = true;
                    if (!t.SyncLocked) row["syncLocked"] = false;
                    if (t.Locked) row["locked"] = true;
                    return row;
                }).ToList(),
            };
            return GetMutationResult(session, snapshot, extra: extra);
        }

        var action = args["action"]?.GetValue<string>() ?? throw new InvalidOperationException("action is required");
        var typeStr = args["type"]?.GetValue<string>();
        var trackIndex = args["trackIndex"]?.GetValue<int>();

        ClipType? type = null;
        if (typeStr != null && Enum.TryParse<ClipType>(typeStr, out var ct))
        {
            type = ct;
        }

        var legacy = EditCommands.ManageTracks(session, action, type, trackIndex);
        if (!legacy.Ok)
        {
            throw new InvalidOperationException(legacy.Error ?? "Failed to manage tracks");
        }

        return GetMutationResult(session, snapshot);
    }

    /// <summary>
    /// Library-only asset types (WP-115: subtitle) must never become timeline clips. Without this the
    /// placement paths would happily drop an SRT onto a video track and fail much later in decode.
    /// </summary>
    private static void RejectUnplaceableAsset(string mediaRef, ClipType entryType)
    {
        if (entryType.IsTimelinePlaceable()) return;
        throw new InvalidOperationException(
            $"{mediaRef} is a {entryType} asset and cannot be placed on the timeline. "
            + "Use add_captions with subtitleMediaRef to place its cues as caption clips.");
    }

    private static object AddClips(JsonObject args)
    {
        var session = GetSession();
        var snapshot = TakeTimelineSnapshot(session.ActiveTimeline);
        var entries = args["entries"] as JsonArray ?? throw new InvalidOperationException("entries is required");

        var prepared = new List<(int TrackIndex, Clip Clip)>();
        foreach (var item in entries)
        {
            if (item == null) continue;
            var mediaRef = item["mediaRef"]?.GetValue<string>() ?? throw new InvalidOperationException("mediaRef is required on entries");
            var trackIndex = item["trackIndex"]?.GetValue<int>();
            var startFrame = item["startFrame"]?.GetValue<int>() ?? throw new InvalidOperationException("startFrame is required on entries");
            var endFrame = item["endFrame"]?.GetValue<int>();
            var sourceNode = item["source"];
            
            var entry = session.Manifest.Entries.FirstOrDefault(e => e.Id == mediaRef);
            var entryType = entry?.Type ?? ClipType.video;
            RejectUnplaceableAsset(mediaRef, entryType);

            if (trackIndex == null)
            {
                int targetTrackIdx = -1;
                for (int i = 0; i < session.ActiveTimeline.Tracks.Count; i++)
                {
                    var track = session.ActiveTimeline.Tracks[i];
                    if (track.Type == entryType || (track.Type == ClipType.video && entryType == ClipType.image))
                    {
                        targetTrackIdx = i;
                        break;
                    }
                }
                if (targetTrackIdx == -1)
                {
                    session.ActiveTimeline.Tracks.Add(new Track { Type = entryType == ClipType.audio ? ClipType.audio : ClipType.video });
                    targetTrackIdx = session.ActiveTimeline.Tracks.Count - 1;
                }
                trackIndex = targetTrackIdx;
            }

            int? endDelta = endFrame is { } ef ? ef - startFrame : null;
            var (trimStart, duration) = ResolveSourceSpan(
                entry, entryType, session.ActiveTimeline.Fps, sourceNode, endDelta, "entries", "endFrame");

            var clip = new Clip
            {
                Id = Timeline.NewId(),
                MediaRef = mediaRef,
                MediaType = entryType,
                SourceClipType = entryType,
                StartFrame = startFrame,
                DurationFrames = duration,
                TrimStartFrame = trimStart,
                Speed = 1.0,
                Volume = 1.0,
                Opacity = 1.0,
                Transform = LayoutEngine.FitTransform(entry, session.ActiveTimeline),
            };

            prepared.Add((trackIndex.Value, clip));
        }

        var result = EditCommands.AddClips(session, prepared, overwrite: true);
        if (!result.Ok)
        {
            throw new InvalidOperationException(result.Error ?? "Failed to add clips");
        }

        return GetMutationResult(session, snapshot, prepared.Select(p => p.Clip.Id).ToList());
    }

    private static object InsertClips(JsonObject args)
    {
        var session = GetSession();
        var snapshot = TakeTimelineSnapshot(session.ActiveTimeline);
        var trackIndex = args["trackIndex"]?.GetValue<int>() ?? throw new InvalidOperationException("trackIndex is required");
        var atFrame = args["atFrame"]?.GetValue<int>() ?? throw new InvalidOperationException("atFrame is required");
        var entries = args["entries"] as JsonArray ?? throw new InvalidOperationException("entries is required");

        var list = new List<Clip>();
        foreach (var item in entries)
        {
            if (item == null) continue;
            var mediaRef = item["mediaRef"]?.GetValue<string>() ?? throw new InvalidOperationException("mediaRef is required on entries");
            var durationFrames = item["durationFrames"]?.GetValue<int>();
            var sourceNode = item["source"];

            var entry = session.Manifest.Entries.FirstOrDefault(e => e.Id == mediaRef);
            var entryType = entry?.Type ?? ClipType.video;
            RejectUnplaceableAsset(mediaRef, entryType);

            var (trimStart, duration) = ResolveSourceSpan(
                entry, entryType, session.ActiveTimeline.Fps, sourceNode, durationFrames, "entries", "durationFrames");

            list.Add(new Clip
            {
                Id = Timeline.NewId(),
                MediaRef = mediaRef,
                MediaType = entryType,
                SourceClipType = entryType,
                DurationFrames = duration,
                TrimStartFrame = trimStart,
                Speed = 1.0,
                Volume = 1.0,
                Opacity = 1.0,
                Transform = LayoutEngine.FitTransform(entry, session.ActiveTimeline),
            });
        }

        var result = EditCommands.InsertClips(session, trackIndex, atFrame, list);
        if (!result.Ok)
        {
            throw new InvalidOperationException(result.Error ?? "Failed to insert clips");
        }

        return GetMutationResult(session, snapshot, list.Select(c => c.Id).ToList());
    }

    private static object MoveClips(JsonObject args)
    {
        var session = GetSession();
        var snapshot = TakeTimelineSnapshot(session.ActiveTimeline);
        var movesArray = args["moves"] as JsonArray ?? throw new InvalidOperationException("moves array is required");

        var movesList = new List<string>();
        session.RecordBeforeEdit();
        try
        {
            foreach (var item in movesArray)
            {
                if (item == null) continue;
                var clipId = item["clipId"]?.GetValue<string>() ?? throw new InvalidOperationException("clipId is required on moves");
                var toTrack = item["toTrack"]?.GetValue<int>();
                var toFrame = item["toFrame"]?.GetValue<int>();

                var timeline = session.ActiveTimeline;
                Clip? clip = null;
                int currentTrackIndex = -1;
                foreach (var track in timeline.Tracks)
                {
                    currentTrackIndex++;
                    clip = track.Clips.FirstOrDefault(c => c.Id == clipId);
                    if (clip != null) break;
                }

                if (clip == null)
                {
                    throw new InvalidOperationException($"Clip not found: {clipId}");
                }

                var targetTrackIndex = toTrack ?? currentTrackIndex;
                var targetFrame = toFrame ?? clip.StartFrame;

                var result = EditCommands.MoveClip(session, clipId, targetTrackIndex, targetFrame);
                if (!result.Ok)
                {
                    throw new InvalidOperationException(result.Error ?? $"Failed to move clip {clipId}");
                }
                movesList.Add(clipId);
            }
        }
        catch
        {
            session.RollbackLastRecord();
            throw;
        }

        return GetMutationResult(session, snapshot, movesList);
    }

    private static object RemoveClips(JsonObject args)
    {
        var session = GetSession();
        var snapshot = TakeTimelineSnapshot(session.ActiveTimeline);
        var clipIdsToken = args["clipIds"];
        var clipIds = new List<string>();
        if (clipIdsToken is JsonArray ja)
        {
            foreach (var item in ja)
            {
                if (item != null) clipIds.Add(item.GetValue<string>());
            }
        }
        else
        {
            throw new InvalidOperationException("clipIds must be an array");
        }

        var result = EditCommands.RemoveClips(session, clipIds, ripple: false);
        if (!result.Ok)
        {
            throw new InvalidOperationException(result.Error ?? "Failed to remove clips");
        }

        return GetMutationResult(session, snapshot);
    }

    private static object RestoreLinkedAudioTool(JsonObject args)
    {
        var session = GetSession();
        var snapshot = TakeTimelineSnapshot(session.ActiveTimeline);
        if (args["clipIds"] is not JsonArray ja)
        {
            throw new InvalidOperationException("clipIds must be an array");
        }

        var clipIds = new List<string>();
        foreach (var item in ja)
        {
            if (item != null) clipIds.Add(item.GetValue<string>());
        }

        if (clipIds.Count == 0)
        {
            throw new InvalidOperationException("clipIds must not be empty");
        }

        var (restored, skipped) = EditCommands.RestoreLinkedAudio(session, clipIds);
        var extra = new Dictionary<string, object>
        {
            ["restored"] = restored.Select(r => new Dictionary<string, object>
            {
                ["clipId"] = r.ClipId,
                ["audioClipId"] = r.AudioClipId,
                ["audioTrack"] = r.AudioTrackIndex,
            }).ToList(),
            ["skipped"] = skipped.Select(s => new Dictionary<string, object>
            {
                ["clipId"] = s.ClipId,
                ["reason"] = s.Reason,
            }).ToList(),
        };
        return GetMutationResult(session, snapshot, extra: extra);
    }

    private static object SplitClips(JsonObject args)
    {
        var session = GetSession();
        var snapshot = TakeTimelineSnapshot(session.ActiveTimeline);
        var splitsArray = args["splits"] as JsonArray;
        
        session.RecordBeforeEdit();
        try
        {
            if (splitsArray != null)
            {
                foreach (var item in splitsArray)
                {
                    if (item == null) continue;
                    var clipId = item["clipId"]?.GetValue<string>() ?? throw new InvalidOperationException("clipId is required");
                    var atFrame = item["atFrame"]?.GetValue<int>() ?? throw new InvalidOperationException("atFrame is required");

                    var result = EditCommands.SplitClip(session, clipId, atFrame);
                    if (!result.Ok)
                    {
                        throw new InvalidOperationException(result.Error ?? $"Failed to split clip {clipId}");
                    }
                }
            }
            else
            {
                var trackIndex = args["trackIndex"]?.GetValue<int>() ?? throw new InvalidOperationException("trackIndex or splits is required");
                var framesArray = args["frames"] as JsonArray ?? throw new InvalidOperationException("frames array is required when splits is omitted");

                var timeline = session.ActiveTimeline;
                if (trackIndex < 0 || trackIndex >= timeline.Tracks.Count)
                {
                    throw new InvalidOperationException($"trackIndex {trackIndex} out of range");
                }

                var track = timeline.Tracks[trackIndex];
                foreach (var item in framesArray)
                {
                    if (item == null) continue;
                    var atFrame = item.GetValue<int>();
                    var clip = track.Clips.FirstOrDefault(c => atFrame > c.StartFrame && atFrame < c.EndFrame);
                    if (clip == null)
                    {
                        throw new InvalidOperationException($"Frame {atFrame} is not inside any clip on track {trackIndex}");
                    }

                    var result = EditCommands.SplitClip(session, clip.Id, atFrame);
                    if (!result.Ok)
                    {
                        throw new InvalidOperationException(result.Error ?? $"Failed to split clip {clip.Id} at {atFrame}");
                    }
                }
            }
        }
        catch
        {
            session.RollbackLastRecord();
            throw;
        }

        return GetMutationResult(session, snapshot);
    }

    // Mac parity (WP-331): units default to project frames; seconds are the clip's source seconds, so they need
    // clipId and map through its trim, speed and position. Values may be fractional; each end is rounded and
    // clamped to the clip (or the timeline), a range left empty is counted as rangesIgnored, and overlaps merge.
    private static object RippleDeleteRanges(JsonObject args)
    {
        var session = GetSession();
        var snapshot = TakeTimelineSnapshot(session.ActiveTimeline);
        var clipId = args["clipId"]?.GetValue<string>();
        var trackIndexToken = args["trackIndex"];
        var rangesArray = args["ranges"] as JsonArray;
        if (rangesArray is null || rangesArray.Count == 0)
            throw new InvalidOperationException("Missing or empty 'ranges' array");
        var units = args["units"]?.GetValue<string>() ?? "frames";
        if (units is not ("seconds" or "frames"))
            throw new InvalidOperationException($"units must be 'seconds' or 'frames' (got '{units}')");
        if ((clipId is null) == (trackIndexToken is null))
            throw new InvalidOperationException("Provide exactly one of 'clipId' (cut within a single clip; allows 'seconds') or 'trackIndex' (cut project-frame ranges spanning a whole track in one call).");

        var raw = new List<(double Start, double End)>();
        for (var i = 0; i < rangesArray.Count; i++)
        {
            if (rangesArray[i] is not JsonArray pair || pair.Count != 2)
                throw new InvalidOperationException($"ranges[{i}]: expected [start, end]");
            if (Num(pair[0]) is not { } start || Num(pair[1]) is not { } end || !double.IsFinite(start) || !double.IsFinite(end))
                throw new InvalidOperationException($"ranges[{i}]: start and end must be numbers");
            if (!(end > start))
                throw new InvalidOperationException($"ranges[{i}]: end ({end}) must be greater than start ({start})");
            raw.Add((start, end));
        }

        static int ClampFrame(double value, int min, int max) =>
            value <= min ? min : value >= max ? max : (int)Math.Round(value, MidpointRounding.AwayFromZero);

        var ranges = new List<(int Start, int End)>();
        var dropped = 0;
        int resolvedTrackIdx;
        var timeline = session.ActiveTimeline;
        if (clipId is not null)
        {
            Clip? clip = null;
            resolvedTrackIdx = -1;
            for (var t = 0; t < timeline.Tracks.Count && clip is null; t++)
            {
                clip = timeline.Tracks[t].Clips.FirstOrDefault(c => c.Id == clipId);
                if (clip is not null) resolvedTrackIdx = t;
            }
            if (clip is null)
                throw new InvalidOperationException($"Clip not found: {clipId}");
            var clipEnd = clip.StartFrame + clip.DurationFrames;
            double ToFrame(double v) => units == "frames"
                ? v
                : clip.StartFrame + (v * timeline.Fps - clip.TrimStartFrame) / Math.Max(clip.Speed, 0.0001);
            foreach (var (start, end) in raw)
            {
                var s = ClampFrame(ToFrame(start), clip.StartFrame, clipEnd);
                var e = ClampFrame(ToFrame(end), clip.StartFrame, clipEnd);
                if (e > s) ranges.Add((s, e)); else dropped++;
            }
            if (ranges.Count == 0)
                throw new InvalidOperationException($"No ranges fall within clip {clipId} (frames {clip.StartFrame}..{clipEnd}). In '{units}' units, ranges must overlap the clip's visible span.");
        }
        else
        {
            if (units != "frames")
                throw new InvalidOperationException("units 'seconds' requires a clipId for source-media mapping; with trackIndex, ranges are project frames.");
            resolvedTrackIdx = ReadOptionalInt(trackIndexToken, "trackIndex")!.Value;
            if (resolvedTrackIdx < 0 || resolvedTrackIdx >= timeline.Tracks.Count)
                throw new InvalidOperationException($"Track index out of range: {resolvedTrackIdx}");
            foreach (var (start, end) in raw)
            {
                var s = ClampFrame(start, 0, timeline.TotalFrames);
                var e = ClampFrame(end, 0, timeline.TotalFrames);
                if (e > s) ranges.Add((s, e)); else dropped++;
            }
            if (ranges.Count == 0)
                throw new InvalidOperationException($"No valid project-frame ranges to delete on track {resolvedTrackIdx}.");
        }

        // Overlapping ranges merge, as on the Mac: each range is cut from the already-shortened track, so a repeated
        // or overlapping range would otherwise remove frames twice.
        var merged = new List<(int Start, int End)>();
        foreach (var range in ranges.OrderBy(r => r.Start))
        {
            if (merged.Count > 0 && range.Start <= merged[^1].End)
                merged[^1] = (merged[^1].Start, Math.Max(merged[^1].End, range.End));
            else
                merged.Add(range);
        }

        // WP-337: linked partners are cut by the same frames in the same undo step. ignoreSyncLockedTracks stays on the
        // wire for Mac parity and changes nothing: Windows has no sync lock (D-2).
        var cutTracks = LinkedCutTracks(session, [resolvedTrackIdx], merged.Select(r => new FrameRange(r.Start, r.End)).ToList());
        var result = EditCommands.RippleDeleteRanges(session, cutTracks, merged);
        if (!result.Ok)
        {
            throw new InvalidOperationException(result.Error ?? "Failed to ripple delete");
        }

        var extra = new Dictionary<string, object> { ["removedFrames"] = merged.Sum(r => r.End - r.Start) };
        if (dropped > 0)
            extra["rangesIgnored"] = dropped;
        return GetMutationResult(session, snapshot, extra: extra);
    }

    private static object SetClipProperties(JsonObject args)
    {
        var session = GetSession();
        var snapshot = TakeTimelineSnapshot(session.ActiveTimeline);
        var clipIdsArray = args["clipIds"] as JsonArray ?? throw new InvalidOperationException("clipIds is required");
        var durationFrames = args["durationFrames"]?.GetValue<int>();
        var trimStartFrame = args["trimStartFrame"]?.GetValue<int>();
        var trimEndFrame = args["trimEndFrame"]?.GetValue<int>();
        var speed = args["speed"]?.GetValue<double>();
        var volume = args["volume"]?.GetValue<double>();
        var opacity = args["opacity"]?.GetValue<double>();
        var blendMode = args["blendMode"]?.GetValue<string>();

        Transform? transform = null;
        if (args["transform"] is JsonObject to)
        {
            transform = new Transform
            {
                CenterX = to["centerX"]?.GetValue<double>() ?? 0.5,
                CenterY = to["centerY"]?.GetValue<double>() ?? 0.5,
                Width = to["width"]?.GetValue<double>() ?? 1.0,
                Height = to["height"]?.GetValue<double>() ?? 1.0,
                Rotation = 0,
                FlipHorizontal = to["flipHorizontal"]?.GetValue<bool>() ?? false,
                FlipVertical = to["flipVertical"]?.GetValue<bool>() ?? false
            };
        }

        var clipIds = new List<string>();
        foreach (var item in clipIdsArray)
        {
            if (item != null) clipIds.Add(item.GetValue<string>());
        }

        var result = EditCommands.SetClipProperties(
            session, clipIds, durationFrames, trimStartFrame, trimEndFrame, speed, volume, opacity, transform, blendMode);
        if (!result.Ok)
        {
            throw new InvalidOperationException(result.Error ?? "Failed to set clip properties");
        }

        return GetMutationResult(session, snapshot, clipIds);
    }

    private static object Undo()
    {
        var session = GetSession();
        if (!session.CanUndo())
        {
            throw new InvalidOperationException("Nothing to undo.");
        }
        session.Undo();
        ResolveInterruptedImports(session);
        return new { status = "success" };
    }

    private static object Redo()
    {
        var session = GetSession();
        if (!session.CanRedo())
        {
            throw new InvalidOperationException("Nothing to redo.");
        }
        session.Redo();
        ResolveInterruptedImports(session);
        return new { status = "success" };
    }

    // --- Markers implementations ---

    private static object AddMarker(JsonObject args)
    {
        var session = GetSession();
        var frame = args["frame"]?.GetValue<int>() ?? throw new InvalidOperationException("frame is required");
        var name = args["name"]?.GetValue<string>();
        var color = args["color"]?.GetValue<string>();
        var comment = args["comment"]?.GetValue<string>();

        var result = EditCommands.AddMarker(session, frame, name, color, comment);
        if (!result.Ok)
        {
            throw new InvalidOperationException(result.Error ?? "Failed to add marker");
        }

        var marker = session.ActiveTimeline.Markers.Last();
        return new Dictionary<string, object>
        {
            ["status"] = "added",
            ["markerId"] = marker.Id,
            ["frame"] = marker.Frame,
            ["name"] = marker.Name ?? "Marker",
            ["color"] = marker.Color ?? "blue"
        };
    }

    private static object ListMarkers(JsonObject args)
    {
        var session = GetSession();
        var markersList = session.ActiveTimeline.Markers
            .OrderBy(m => m.Frame)
            .Select(m => {
                var dict = new Dictionary<string, object>
                {
                    ["markerId"] = m.Id,
                    ["frame"] = m.Frame,
                    ["name"] = m.Name ?? "Marker",
                    ["color"] = m.Color ?? "blue"
                };
                if (!string.IsNullOrEmpty(m.Note))
                {
                    dict["comment"] = m.Note;
                }
                return dict;
            }).ToList();

        return new { markers = markersList };
    }

    private static object UpdateMarker(JsonObject args)
    {
        var session = GetSession();
        var markerId = args["markerId"]?.GetValue<string>() ?? throw new InvalidOperationException("markerId is required");
        var frame = args["frame"]?.GetValue<int>();
        var name = args["name"]?.GetValue<string>();
        var color = args["color"]?.GetValue<string>();
        var comment = args["comment"]?.GetValue<string>();

        var result = EditCommands.UpdateMarker(session, markerId, frame, name, color, comment);
        if (!result.Ok)
        {
            throw new InvalidOperationException(result.Error ?? "Failed to update marker");
        }

        var marker = session.ActiveTimeline.Markers.First(m => m.Id == markerId);
        var response = new Dictionary<string, object>
        {
            ["status"] = "updated",
            ["markerId"] = marker.Id,
            ["frame"] = marker.Frame,
            ["name"] = marker.Name ?? "Marker",
            ["color"] = marker.Color ?? "blue"
        };
        if (!string.IsNullOrEmpty(marker.Note))
        {
            response["comment"] = marker.Note;
        }
        return response;
    }

    private static object RemoveMarker(JsonObject args)
    {
        var session = GetSession();
        var markerId = args["markerId"]?.GetValue<string>() ?? throw new InvalidOperationException("markerId is required");

        var result = EditCommands.RemoveMarker(session, markerId);
        if (!result.Ok)
        {
            throw new InvalidOperationException(result.Error ?? "Failed to remove marker");
        }

        return new { status = "removed", markerId = markerId };
    }

    // --- Generation implementations ---

    private static object ListModels(JsonObject args)
    {
        var typeFilter = args["type"]?.GetValue<string>();
        var service = TryGetGenerationService();
        if (service is null)
        {
            return new Dictionary<string, object>
            {
                ["loaded"] = false,
                ["configured"] = false,
                ["models"] = Array.Empty<object>(),
                ["note"] = GenerationUnavailableMessage(),
            };
        }

        var models = service.Client.ListModelsAsync().GetAwaiter().GetResult();
        models.AddRange(ListHostedImageModels());
        models.AddRange(ListHostedMediaModelsAsync().GetAwaiter().GetResult());
        models = models.GroupBy(m => m.Id, StringComparer.OrdinalIgnoreCase).Select(g => g.Last()).ToList();

        if (!string.IsNullOrEmpty(typeFilter))
        {
            if (Enum.TryParse<GenerationKind>(typeFilter, true, out var kind))
            {
                models = models.Where(m => m.Kind == kind).ToList();
            }
            else
            {
                models = [];
            }
        }

        var result = new Dictionary<string, object>
        {
            ["models"] = models,
            ["loaded"] = true,
            ["configured"] = true,
        };
        if (IsFakeGenerationEnabled())
        {
            result["demo"] = true;
            result["note"] = FakeGenerationNote;
        }
        return result;
    }

    /// <summary>How long a model-list lookup may stall a caller before falling back.</summary>
    private static readonly TimeSpan DefaultModelLookupTimeout = TimeSpan.FromSeconds(5);

    private static string GetDefaultModel(GenerationKind kind, string fallback)
    {
        var service = TryGetGenerationService();
        if (service is null) return fallback;
        // Hosted generation is quote-only, so it has no default model.
        if (service.Client is HostedGenerationClient)
            throw new InvalidOperationException(HostedGenerationClient.QuoteRequiredMessage);
        try
        {
            // The generation handlers still run on the dispatcher (they mutate the manifest), so an
            // unreachable backend would otherwise freeze the app for the HttpClient's full timeout.
            var models = service.Client.ListModelsAsync()
                .WaitAsync(DefaultModelLookupTimeout)
                .GetAwaiter().GetResult();
            var first = models.FirstOrDefault(m => m.Kind == kind);
            if (first != null) return first.Id;
        }
        catch (TimeoutException)
        {
            // Bounded stall by design: only a late catalog falls back. Backend faults and
            // cancellation stay observable so a generate call never silently submits a
            // hard-coded model id the catalog did not return.
        }
        return fallback;
    }

    private static Dictionary<string, object> GenerationAccepted(
        string mediaRef,
        string note,
        Dictionary<string, object>? extra = null)
    {
        var dict = extra is null
            ? new Dictionary<string, object>()
            : new Dictionary<string, object>(extra);
        dict["status"] = "generating";
        dict["mediaRef"] = mediaRef;
        if (IsFakeGenerationEnabled())
        {
            dict["demo"] = true;
            dict["note"] = FakeGenerationNote;
        }
        else
        {
            dict["note"] = note;
        }
        return dict;
    }

    private static object GenerateVideo(JsonObject args)
    {
        if (TryRouteMediaSubmit(args, "video", out var hosted))
            return hosted!;
        var session = GetSession();
        _ = GetGenerationService();
        var prompt = args["prompt"]?.GetValue<string>() ?? throw new InvalidOperationException("prompt is required");
        var name = args["name"]?.GetValue<string>();
        var modelId = args["model"]?.GetValue<string>() ?? GetDefaultModel(GenerationKind.Video, "video-gen-v1");
        var duration = args["duration"]?.GetValue<int>() ?? 5;
        var aspectRatio = args["aspectRatio"]?.GetValue<string>() ?? "16:9";
        var resolution = args["resolution"]?.GetValue<string>();
        var folder = args["folder"]?.GetValue<string>();

        var startFrameMediaRef = args["startFrameMediaRef"]?.GetValue<string>();
        var endFrameMediaRef = args["endFrameMediaRef"]?.GetValue<string>();
        var sourceVideoMediaRef = args["sourceVideoMediaRef"]?.GetValue<string>();

        var referenceImageMediaRefs = ParseStringArray(args["referenceImageMediaRefs"]);
        var referenceVideoMediaRefs = ParseStringArray(args["referenceVideoMediaRefs"]);
        var referenceAudioMediaRefs = ParseStringArray(args["referenceAudioMediaRefs"]);

        var placeholderId = Guid.NewGuid().ToString().ToUpperInvariant();
        var placeholderName = name ?? (prompt.Length > 30 ? prompt.Substring(0, 30) : prompt);
        var filename = $"gen-{placeholderId.Substring(0, Math.Min(8, placeholderId.Length))}.mp4";

        var folderId = ResolveFolderId(session, folder);

        var placeholder = new MediaManifestEntry
        {
            Id = placeholderId,
            Name = placeholderName,
            Type = ClipType.video,
            Source = MediaSource.Project("media/" + filename),
            Duration = duration,
            GenerationStatus = "preparing",
            FolderId = folderId,
            GenerationInput = new GenerationInput
            {
                Prompt = prompt,
                Model = modelId,
                Duration = duration,
                AspectRatio = aspectRatio,
                Resolution = resolution,
                CreatedAt = DateTimeOffset.UtcNow
            }
        };

        lock (_sessionLock)
        {
            session.Manifest.Entries.Add(placeholder);
            session.MarkDirty();
            session.Save();
        }
        var packagePath = session.PackagePath
            ?? throw new InvalidOperationException("Open project has no package path.");

        RunGenerationBackground(session, placeholderId, async token =>
        {
            var service = GetGenerationService();
            var client = service.Client;

            UpdatePlaceholderStatus(session, placeholderId, "generating", jobId: null);

            var uploadedStartFrame = startFrameMediaRef != null
                ? await UploadReferencesAsync(session, client, new List<string> { startFrameMediaRef }, token)
                : new List<string>();
            var uploadedEndFrame = endFrameMediaRef != null
                ? await UploadReferencesAsync(session, client, new List<string> { endFrameMediaRef }, token)
                : new List<string>();
            var uploadedSourceVideo = sourceVideoMediaRef != null
                ? await UploadReferencesAsync(session, client, new List<string> { sourceVideoMediaRef }, token)
                : new List<string>();

            var uploadedImages = await UploadReferencesAsync(session, client, referenceImageMediaRefs, token);
            var uploadedVideos = await UploadReferencesAsync(session, client, referenceVideoMediaRefs, token);
            var uploadedAudios = await UploadReferencesAsync(session, client, referenceAudioMediaRefs, token);

            var param = new VideoGenerationParams
            {
                Prompt = prompt,
                Duration = duration,
                AspectRatio = aspectRatio,
                Resolution = resolution,
                SourceVideoUrl = uploadedSourceVideo.FirstOrDefault(),
                StartFrameUrl = uploadedStartFrame.FirstOrDefault(),
                EndFrameUrl = uploadedEndFrame.FirstOrDefault(),
                ReferenceImageUrls = uploadedImages,
                ReferenceVideoUrls = uploadedVideos,
                ReferenceAudioUrls = uploadedAudios,
                GenerateAudio = true
            };

            var jobId = await client.SubmitGenerationAsync(modelId, param, null, token);
            await PollDownloadAndLandAsync(
                session,
                placeholderId,
                packagePath,
                jobId,
                TimeSpan.FromMinutes(5),
                token,
                service,
                (entry, _) =>
                {
                    entry.Duration = duration;
                    entry.SourceFps = session.ActiveTimeline.Fps;
                });
        });

        return GenerationAccepted(
            placeholderId,
            $"Generation started. Placeholder asset ID: {placeholderId}. Model: {modelId}, duration: {duration}s, aspect: {aspectRatio}");
    }

    private static object GenerateImage(JsonObject args)
    {
        if (IsImagesSubmit(args))
            return SubmitImageJob(args, "generate_image");
        var session = GetSession();
        _ = GetGenerationService();
        var prompt = args["prompt"]?.GetValue<string>() ?? throw new InvalidOperationException("prompt is required");
        var name = args["name"]?.GetValue<string>();
        var modelId = args["model"]?.GetValue<string>() ?? GetDefaultModel(GenerationKind.Image, "image-gen-v1");
        var aspectRatio = args["aspectRatio"]?.GetValue<string>() ?? "1:1";
        var resolution = args["resolution"]?.GetValue<string>();
        var quality = args["quality"]?.GetValue<string>();
        var folder = args["folder"]?.GetValue<string>();

        var referenceMediaRefs = ParseStringArray(args["referenceMediaRefs"]);

        var placeholderId = Guid.NewGuid().ToString().ToUpperInvariant();
        var placeholderName = name ?? (prompt.Length > 30 ? prompt.Substring(0, 30) : prompt);
        var filename = $"gen-{placeholderId.Substring(0, Math.Min(8, placeholderId.Length))}.png";

        var folderId = ResolveFolderId(session, folder);

        var placeholder = new MediaManifestEntry
        {
            Id = placeholderId,
            Name = placeholderName,
            Type = ClipType.image,
            Source = MediaSource.Project("media/" + filename),
            Duration = 0,
            FolderId = folderId,
            GenerationStatus = "preparing",
            GenerationInput = new GenerationInput
            {
                Prompt = prompt,
                Model = modelId,
                AspectRatio = aspectRatio,
                Resolution = resolution,
                Quality = quality,
                CreatedAt = DateTimeOffset.UtcNow
            }
        };

        lock (_sessionLock)
        {
            session.Manifest.Entries.Add(placeholder);
            session.MarkDirty();
            session.Save();
        }
        var packagePath = session.PackagePath
            ?? throw new InvalidOperationException("Open project has no package path.");

        RunGenerationBackground(session, placeholderId, async token =>
        {
            var service = GetGenerationService();
            var client = service.Client;

            UpdatePlaceholderStatus(session, placeholderId, "generating", jobId: null);

            var uploadedRefs = await UploadReferencesAsync(session, client, referenceMediaRefs, token);

            var param = new ImageGenerationParams
            {
                Prompt = prompt,
                AspectRatio = aspectRatio,
                Resolution = resolution,
                Quality = quality,
                ImageUrls = uploadedRefs,
                NumImages = 1
            };

            var jobId = await client.SubmitGenerationAsync(modelId, param, null, token);
            await PollDownloadAndLandAsync(
                session,
                placeholderId,
                packagePath,
                jobId,
                TimeSpan.FromMinutes(5),
                token,
                service,
                (entry, landedPath) =>
                {
                    entry.Duration = 0;
                    if (ImageDimensions.TryRead(landedPath, out var width, out var height))
                    {
                        entry.SourceWidth = width;
                        entry.SourceHeight = height;
                    }
                });
        });

        return GenerationAccepted(
            placeholderId,
            $"Generation started. Placeholder asset ID: {placeholderId}. Model: {modelId}, aspect: {aspectRatio}");
    }

    private static object GenerateAudio(JsonObject args)
    {
        if (TryRouteMediaSubmit(args, "audio", out var hosted))
            return hosted!;
        var session = GetSession();
        _ = GetGenerationService();
        var prompt = args["prompt"]?.GetValue<string>() ?? "";
        var name = args["name"]?.GetValue<string>();
        var modelId = args["model"]?.GetValue<string>() ?? GetDefaultModel(GenerationKind.Audio, "audio-gen-v1");
        var voice = args["voice"]?.GetValue<string>();
        var lyrics = args["lyrics"]?.GetValue<string>();
        var styleInstructions = args["styleInstructions"]?.GetValue<string>();
        var instrumental = args["instrumental"]?.GetValue<bool>() ?? false;
        var duration = args["duration"]?.GetValue<int>() ?? 10;
        var videoSourceMediaRef = args["videoSourceMediaRef"]?.GetValue<string>();
        var folder = args["folder"]?.GetValue<string>();

        var placeholderId = Guid.NewGuid().ToString().ToUpperInvariant();
        var placeholderName = name ?? (prompt.Length > 30 ? prompt.Substring(0, 30) : prompt);
        if (string.IsNullOrEmpty(placeholderName)) placeholderName = "Generated Audio";
        var filename = $"gen-{placeholderId.Substring(0, Math.Min(8, placeholderId.Length))}.mp3";

        var folderId = ResolveFolderId(session, folder);

        var placeholder = new MediaManifestEntry
        {
            Id = placeholderId,
            Name = placeholderName,
            Type = ClipType.audio,
            Source = MediaSource.Project("media/" + filename),
            Duration = duration,
            FolderId = folderId,
            GenerationStatus = "preparing",
            GenerationInput = new GenerationInput
            {
                Prompt = prompt,
                Model = modelId,
                Duration = duration,
                Voice = voice,
                Lyrics = lyrics,
                StyleInstructions = styleInstructions,
                Instrumental = instrumental,
                CreatedAt = DateTimeOffset.UtcNow
            }
        };

        lock (_sessionLock)
        {
            session.Manifest.Entries.Add(placeholder);
            session.MarkDirty();
            session.Save();
        }
        var packagePath = session.PackagePath
            ?? throw new InvalidOperationException("Open project has no package path.");

        RunGenerationBackground(session, placeholderId, async token =>
        {
            var service = GetGenerationService();
            var client = service.Client;

            UpdatePlaceholderStatus(session, placeholderId, "generating", jobId: null);

            string? videoUrl = null;
            if (!string.IsNullOrEmpty(videoSourceMediaRef))
            {
                var uploaded = await UploadReferencesAsync(session, client, new List<string> { videoSourceMediaRef }, token);
                videoUrl = uploaded.FirstOrDefault();
            }

            var param = new AudioGenerationParams
            {
                Prompt = prompt,
                Voice = voice,
                Lyrics = lyrics,
                StyleInstructions = styleInstructions,
                Instrumental = instrumental,
                DurationSeconds = duration,
                VideoUrl = videoUrl
            };

            var jobId = await client.SubmitGenerationAsync(modelId, param, null, token);
            await PollDownloadAndLandAsync(
                session,
                placeholderId,
                packagePath,
                jobId,
                TimeSpan.FromMinutes(5),
                token,
                service,
                (entry, _) =>
                {
                    entry.Duration = duration;
                    entry.HasAudio = true;
                });
        });

        return GenerationAccepted(
            placeholderId,
            $"Generation started. Placeholder asset ID: {placeholderId}. Model: {modelId}, audio");
    }

    // --- Helpers ---

    private static object GetMutationResult(
        ProjectSession session,
        TimelineSnapshot snapshot,
        List<string>? touched = null,
        Dictionary<string, object>? extra = null,
        List<string>? notes = null)
    {
        var after = TakeTimelineSnapshot(session.ActiveTimeline);
        var notesList = notes ?? new List<string>();

        var changed = new HashSet<string>(touched?.Where(t => after.Placements.ContainsKey(t)) ?? Enumerable.Empty<string>());
        foreach (var id in after.Placements.Keys)
        {
            if (!snapshot.Placements.ContainsKey(id))
            {
                changed.Add(id);
            }
        }

        var pureShifts = new Dictionary<string, (int From, int Delta)>();
        foreach (var kvp in after.Placements)
        {
            var id = kvp.Key;
            var p = kvp.Value;
            if (!snapshot.Placements.TryGetValue(id, out var b)) continue;
            if (b.SamePlace(p)) continue;
            if (changed.Contains(id)) continue;

            if (b.TrackId == p.TrackId && b.Duration == p.Duration)
            {
                pureShifts[id] = (b.Start, p.Start - b.Start);
            }
            else
            {
                changed.Add(id);
            }
        }

        var shifts = new List<Dictionary<string, object>>();
        var grouped = pureShifts.Keys
            .GroupBy(id => $"{after.Placements[id].Index}|{pureShifts[id].Delta}")
            .ToList();

        foreach (var groupKvp in grouped)
        {
            var ids = groupKvp.ToList();
            if (ids.Count >= 3) // shiftGroupMinimum = 3
            {
                var first = pureShifts[ids[0]];
                var trackIdx = after.Placements[ids[0]].Index;
                var minFrom = ids.Min(id => pureShifts[id].From);
                shifts.Add(new Dictionary<string, object>
                {
                    ["track"] = trackIdx,
                    ["fromFrame"] = minFrom,
                    ["by"] = first.Delta,
                    ["count"] = ids.Count
                });
            }
            else
            {
                foreach (var id in ids)
                {
                    changed.Add(id);
                }
            }
        }

        shifts = shifts
            .OrderBy(s => (int)s["track"])
            .ThenBy(s => (int)s["fromFrame"])
            .ToList();

        var gidByMember = new Dictionary<string, string>();
        var counts = new Dictionary<string, int>();
        foreach (var track in session.ActiveTimeline.Tracks)
        {
            foreach (var clip in track.Clips)
            {
                if (changed.Contains(clip.Id) && !string.IsNullOrEmpty(clip.CaptionGroupId))
                {
                    gidByMember[clip.Id] = clip.CaptionGroupId;
                    counts[clip.CaptionGroupId] = counts.GetValueOrDefault(clip.CaptionGroupId) + 1;
                }
            }
        }

        var collapsedGids = counts.Where(kvp => kvp.Value >= 3).Select(kvp => kvp.Key).ToHashSet();
        if (collapsedGids.Count > 0)
        {
            changed.RemoveWhere(id => gidByMember.TryGetValue(id, out var gid) && collapsedGids.Contains(gid));
        }

        var captionGroups = new List<Dictionary<string, object>>();
        if (collapsedGids.Count > 0)
        {
            var fold = BuildLinkFoldPlan(session.ActiveTimeline);
            var grades = new Dictionary<string, Dictionary<string, object>>();
            foreach (var track in session.ActiveTimeline.Tracks)
            {
                foreach (var clip in track.Clips)
                {
                    var colorObj = GetColorObject(clip.Effects);
                    if (colorObj != null) grades[clip.Id] = colorObj;
                }
            }

            for (int i = 0; i < session.ActiveTimeline.Tracks.Count; i++)
            {
                var track = session.ActiveTimeline.Tracks[i];
                var compactedClips = new List<Dictionary<string, object>>();
                foreach (var c in track.Clips)
                {
                    if (!fold.FoldedAudioIds.Contains(c.Id))
                    {
                        compactedClips.Add(GetCompactClip(c, fold, grades));
                    }
                }

                ProcessCaptionGroups(
                    compactedClips,
                    captionDetail: false,
                    startFrame: null,
                    endFrame: null,
                    out var loose,
                    out var trackCaptionGroups);

                foreach (var cg in trackCaptionGroups)
                {
                    if (cg.TryGetValue("captionGroupId", out var gidObj) && gidObj is string gid && collapsedGids.Contains(gid))
                    {
                        var cgCopy = new Dictionary<string, object>(cg)
                        {
                            ["track"] = i
                        };
                        captionGroups.Add(cgCopy);
                    }
                }
            }
        }

        var shapedClips = new List<Dictionary<string, object>>();
        if (changed.Count > 0)
        {
            var fold = BuildLinkFoldPlan(session.ActiveTimeline);
            var grades = new Dictionary<string, Dictionary<string, object>>();
            foreach (var track in session.ActiveTimeline.Tracks)
            {
                foreach (var clip in track.Clips)
                {
                    var colorObj = GetColorObject(clip.Effects);
                    if (colorObj != null) grades[clip.Id] = colorObj;
                }
            }

            var byId = new Dictionary<string, Dictionary<string, object>>();
            for (int i = 0; i < session.ActiveTimeline.Tracks.Count; i++)
            {
                var track = session.ActiveTimeline.Tracks[i];
                var compactedClips = new List<Dictionary<string, object>>();
                foreach (var c in track.Clips)
                {
                    if (!fold.FoldedAudioIds.Contains(c.Id))
                    {
                        compactedClips.Add(GetCompactClip(c, fold, grades));
                    }
                }

                ProcessCaptionGroups(
                    compactedClips,
                    captionDetail: true,
                    startFrame: null,
                    endFrame: null,
                    out var loose,
                    out var trackCaptionGroups);

                foreach (var clip in loose)
                {
                    var clipCopy = new Dictionary<string, object>(clip)
                    {
                        ["track"] = i
                    };
                    if (clipCopy.TryGetValue("id", out var idObj) && idObj is string id)
                    {
                        byId[id] = clipCopy;
                    }
                    if (clipCopy.TryGetValue("audio", out var audioObj) && audioObj is Dictionary<string, object> audioDict)
                    {
                        if (audioDict.TryGetValue("id", out var aidObj) && aidObj is string aid)
                        {
                            byId[aid] = clipCopy;
                        }
                    }
                }

                foreach (var cg in trackCaptionGroups)
                {
                    if (cg.TryGetValue("clips", out var clipsObj) && clipsObj is List<List<object>> rows)
                    {
                        foreach (var row in rows)
                        {
                            if (row.Count >= 4 && row[0] is string cid)
                            {
                                var gidVal = cg.TryGetValue("captionGroupId", out var val) ? val : "";
                                byId[cid] = new Dictionary<string, object>
                                {
                                    ["id"] = cid,
                                    ["track"] = i,
                                    ["frames"] = new[] { Convert.ToInt32(row[1]), Convert.ToInt32(row[2]) },
                                    ["textContent"] = row[3],
                                    ["captionGroupId"] = gidVal
                                };
                            }
                        }
                    }
                }
            }

            for (int i = 0; i < session.ActiveTimeline.Tracks.Count; i++)
            {
                var track = session.ActiveTimeline.Tracks[i];
                foreach (var clip in track.Clips)
                {
                    if (changed.Contains(clip.Id) && !byId.ContainsKey(clip.Id))
                    {
                        var entry = new Dictionary<string, object>
                        {
                            ["id"] = clip.Id,
                            ["track"] = i,
                            ["frames"] = new[] { clip.StartFrame, clip.StartFrame + clip.DurationFrames }
                        };
                        if (!string.IsNullOrEmpty(clip.TextContent)) entry["textContent"] = clip.TextContent;
                        if (!string.IsNullOrEmpty(clip.CaptionGroupId)) entry["captionGroupId"] = clip.CaptionGroupId;
                        byId[clip.Id] = entry;
                    }
                }
            }

            var seen = new HashSet<string>();
            foreach (var id in changed)
            {
                if (byId.TryGetValue(id, out var clipDict))
                {
                    var cid = clipDict["id"] as string ?? "";
                    if (seen.Add(cid))
                    {
                        shapedClips.Add(clipDict);
                    }
                }
            }

            shapedClips = shapedClips
                .OrderBy(c => (int)c["track"])
                .ThenBy(c => {
                    var frames = c["frames"] as int[];
                    if (frames == null && c["frames"] is Array arr && arr.Length >= 2)
                    {
                        frames = [Convert.ToInt32(arr.GetValue(0)), Convert.ToInt32(arr.GetValue(1))];
                    }
                    return frames?[0] ?? 0;
                })
                .ToList();
        }

        var removedClipIds = snapshot.Placements.Keys
            .Where(id => !after.Placements.ContainsKey(id))
            .OrderBy(id => id)
            .ToList();

        var createdTracks = new List<object>();
        for (int i = 0; i < after.TrackIds.Count; i++)
        {
            var trackId = after.TrackIds[i];
            if (!snapshot.TrackIds.Contains(trackId))
            {
                var t = session.ActiveTimeline.Tracks[i];
                createdTracks.Add(new Dictionary<string, object>
                {
                    ["index"] = i,
                    ["label"] = TrackLabel(session.ActiveTimeline, i),
                    ["type"] = t.Type.ToString().ToLowerInvariant()
                });
            }
        }

        var trackListChanged = snapshot.TrackIds.Any(tid => !after.TrackIds.Contains(tid))
            || after.TrackIds.Select((tid, idx) => (tid, idx)).Any(pair => {
                var oldIdx = snapshot.TrackIds.IndexOf(pair.tid);
                return oldIdx >= 0 && oldIdx != pair.idx;
            });

        if (trackListChanged)
        {
            notesList.Add("Track indices shifted — re-read get_timeline before the next index-based call.");
        }

        var payload = extra != null ? new Dictionary<string, object>(extra) : new Dictionary<string, object>();
        payload["status"] = "success";
        if (captionGroups.Count > 0) payload["captionGroups"] = captionGroups;

        var finalClips = shapedClips;
        if (finalClips.Count > 30) // mutationClipLimit = 30
        {
            payload["clipsNote"] = $"Showing 30 of {finalClips.Count} changed clips — re-read get_timeline for the rest.";
            finalClips = finalClips.Take(30).ToList();
        }
        if (finalClips.Count > 0) payload["clips"] = finalClips;
        if (shifts.Count > 0) payload["shifted"] = shifts;
        if (removedClipIds.Count > 0) payload["removedClipIds"] = removedClipIds;
        if (createdTracks.Count > 0) payload["createdTracks"] = createdTracks;
        if (notesList.Count > 0) payload["notes"] = notesList;

        return payload;
    }

    private static async Task<List<string>> UploadReferencesAsync(
        ProjectSession session,
        IGenerationClient client,
        List<string> mediaRefs,
        CancellationToken cancellationToken)
    {
        var references = ProjectHub.RunSerialized(() => mediaRefs
            .Select<string, (string Path, string ContentType, MediaManifestEntry Entry, string MediaRef)?>(mediaRef =>
            {
                var entry = session.Manifest.Entries.FirstOrDefault(e => e.Id == mediaRef);
                if (entry is null) return null;
                // WP-399: a file outside the project the user has not allowed is refused by name.
                var resolvedPath = UsablePath(session, entry, mediaRef);
                if (resolvedPath is null || !File.Exists(resolvedPath))
                    throw new FileNotFoundException($"Reference media file not found for {mediaRef}: {resolvedPath}");
                var contentType = entry.Type == ClipType.audio ? "audio/mpeg" :
                    entry.Type == ClipType.image ? "image/png" : "video/mp4";
                return (Path: resolvedPath, ContentType: contentType, Entry: entry, MediaRef: mediaRef);
            })
            .Where(reference => reference is not null)
            .Select(reference => reference!.Value)
            .ToList());

        // WP-399: every reference passes before any byte leaves, and an allowed external file must
        // still lead where it did when the user allowed it.
        foreach (var reference in references)
            TransferPath(session, reference.Entry, reference.MediaRef);

        var result = new List<string>();
        foreach (var reference in references)
        {
            var remoteUrl = await client.UploadReferenceAsync(
                reference.Path,
                reference.ContentType,
                cancellationToken);
            result.Add(remoteUrl);
        }
        return result;
    }

    private static void UpdatePlaceholderStatus(
        ProjectSession session,
        string placeholderId,
        string status,
        string? jobId,
        List<string>? resultUrls = null)
    {
        ProjectHub.RunSerialized(() =>
        {
            lock (_sessionLock)
            {
                var entry = session.Manifest.Entries.FirstOrDefault(e => e.Id == placeholderId);
                if (entry != null)
                {
                    ApplyGenerationPlaceholderStatus(entry, status, jobId, resultUrls);
                    session.MarkDirty();
                    session.Save();
                }
            }
        });
    }

    private static void CancelAllGenerationsLocked()
    {
        foreach (var cts in _generationCancellations.Values)
        {
            try { cts.Cancel(); } catch { /* ignore */ }
            cts.Dispose();
        }
        _generationCancellations.Clear();
    }

    private static CancellationToken RegisterGenerationCancel(string placeholderId)
    {
        lock (_genLock)
        {
            if (_generationCancellations.TryGetValue(placeholderId, out var existing))
            {
                try { existing.Cancel(); } catch { /* ignore */ }
                existing.Dispose();
            }

            var cts = new CancellationTokenSource();
            _generationCancellations[placeholderId] = cts;
            return cts.Token;
        }
    }

    private static void UnregisterGenerationCancel(string placeholderId)
    {
        lock (_genLock)
        {
            if (_generationCancellations.Remove(placeholderId, out var cts))
                cts.Dispose();
        }
    }

    private static void RunGenerationBackground(
        ProjectSession session,
        string placeholderId,
        Func<CancellationToken, Task> work)
    {
        var token = RegisterGenerationCancel(placeholderId);
        _ = Task.Run(async () =>
        {
            try
            {
                await work(token).ConfigureAwait(false);
            }
            catch (Exception) when (token.IsCancellationRequested)
            {
                var hosted = ProjectHub.RunSerialized(() =>
                    session.Manifest.Entries.FirstOrDefault(e => e.Id == placeholderId) is { } entry
                    && (IsImagesJob(entry) || IsMediaJob(entry)));
                if (hosted)
                {
                    try { await FinishHostedCancelAsync(session, placeholderId).ConfigureAwait(false); }
                    catch (Exception ex) when (HostedStatusReads.IsStillWorking(ex) || HostedStatusReads.IsSuspended(ex))
                    {
                        if (HostedStatusReads.IsSuspended(ex))
                            NoteSuspendedOnce();
                    }
                    return;
                }
                try
                {
                    await ConfirmImageCancellationAsync(session, placeholderId).ConfigureAwait(false);
                    await ConfirmMediaCancellationAsync(session, placeholderId).ConfigureAwait(false);
                    var quotedJob = ProjectHub.RunSerialized(() => session.Manifest.Entries.FirstOrDefault(e => e.Id == placeholderId)) is { } canceled && (IsImagesJob(canceled) || IsMediaJob(canceled));
                    UpdatePlaceholderStatus(session, placeholderId, quotedJob ? "failed: Cancellation requested" : "failed: Cancelled", jobId: null);
                }
                catch (Exception ex)
                {
                    UpdatePlaceholderStatus(session, placeholderId, "failed: Cancellation unconfirmed. " + GenerationHttpErrors.Describe(ex), jobId: null);
                }
            }
            catch (Exception ex) when (HostedTransportDeferred(session, placeholderId, ex))
            {
                if (HostedStatusReads.IsSuspended(ex))
                    NoteSuspendedOnce();
            }
            catch (Exception ex)
            {
                UpdatePlaceholderStatus(
                    session,
                    placeholderId,
                    "failed: " + GenerationHttpErrors.Describe(ex),
                    jobId: null);
            }
            finally
            {
                UnregisterGenerationCancel(placeholderId);
            }
        });
    }

    private static bool ShouldResumeGeneration(MediaManifestEntry entry)
    {
        var input = entry.GenerationInput;
        if (input is null)
            return false;
        var status = entry.GenerationStatus ?? "";
        if (status == DemoGenerationStatus)
            return false;
        if (status is "generating" or "downloading" or "preparing" or "rendering" or "recovering" or HostedJobPhases.CantCancelStatus)
            return !string.IsNullOrEmpty(input.BackendJobId) || input.ResultUrls is { Count: > 0 }
                || ((IsImagesOperation(input.Operation) || IsMediaOperation(input.Operation)) && !string.IsNullOrEmpty(input.QuoteId)
                    && Guid.TryParseExact(input.SubmitKey, "D", out _));
        return status.StartsWith("failed:", StringComparison.Ordinal)
               && input.ResultUrls is { Count: > 0 };
    }

    private static async Task PollDownloadAndLandAsync(
        ProjectSession session,
        string placeholderId,
        string packagePath,
        string jobId,
        TimeSpan timeout,
        CancellationToken token,
        GenerationService service,
        Action<MediaManifestEntry, string>? applyMetadata = null)
    {
        UpdatePlaceholderStatus(session, placeholderId, "generating", jobId);
        var completedJob = await service.PollJobAsync(jobId, TimeSpan.FromSeconds(1), timeout, token)
            .ConfigureAwait(false);
        if (completedJob.ResultUrls is not { Count: > 0 })
            throw new InvalidOperationException("Generation succeeded but returned no result URLs.");
        await LandFromUrlAsync(
                session,
                placeholderId,
                packagePath,
                completedJob.ResultUrls[0],
                jobId,
                completedJob.ResultUrls,
                token,
                service,
                applyMetadata)
            .ConfigureAwait(false);
    }

    private static async Task LandFromUrlAsync(
        ProjectSession session,
        string placeholderId,
        string packagePath,
        string remoteUrl,
        string? jobId,
        IReadOnlyList<string>? resultUrls,
        CancellationToken token,
        GenerationService service,
        Action<MediaManifestEntry, string>? applyMetadata = null)
    {
        UpdatePlaceholderStatus(
            session,
            placeholderId,
            "downloading",
            jobId,
            resultUrls?.ToList());
        var landedPath = await service.DownloadAndLandAssetAsync(remoteUrl, packagePath, placeholderId, token)
            .ConfigureAwait(false);
        var landedFilename = Path.GetFileName(landedPath);
        var pictureSize = await LandedPictureSizeAsync(landedPath, token).ConfigureAwait(false);
        ProjectHub.RunSerialized(() =>
        {
            lock (_sessionLock)
            {
                var entry = session.Manifest.Entries.FirstOrDefault(e => e.Id == placeholderId);
                if (entry is null)
                    return;
                var hadSize = HasSourceSize(entry);
                entry.Source = MediaSource.Project("media/" + landedFilename);
                entry.GenerationStatus = CompletedGenerationStatus();
                applyMetadata?.Invoke(entry, landedPath);
                // WP-405: the landed file's own size, never the canvas's.
                if (pictureSize is { } size && entry.Type is ClipType.video or ClipType.image && !HasSourceSize(entry))
                {
                    entry.SourceWidth = size.Width;
                    entry.SourceHeight = size.Height;
                }
                RefitClipsPlacedBeforeSize(session, entry, hadSize);
                session.MarkDirty();
                session.Save();
            }
        });
    }

    private static List<string> ParseStringArray(JsonNode? node)
    {
        if (node == null) return new List<string>();
        if (node is JsonArray array)
        {
            return array.Select(x => x?.ToString() ?? "").Where(s => !string.IsNullOrEmpty(s)).ToList();
        }
        var singleVal = node.ToString();
        if (!string.IsNullOrEmpty(singleVal))
        {
            return new List<string> { singleVal };
        }
        return new List<string>();
    }

    private static string? ResolveFolderId(ProjectSession session, string? path)
    {
        if (string.IsNullOrEmpty(path)) return null;

        var segments = path.Split('/')
            .Select(s => s.Trim())
            .Where(s => !string.IsNullOrEmpty(s))
            .ToList();

        if (segments.Count == 0) return null;

        string? parent = null;
        foreach (var segment in segments)
        {
            var matches = session.Manifest.Folders
                .Where(f => f.ParentFolderId == parent && string.Equals(f.Name, segment, StringComparison.OrdinalIgnoreCase))
                .ToList();

            string? nextId = null;
            if (matches.Count > 1)
            {
                var exact = matches.FirstOrDefault(f => f.Name == segment);
                if (exact != null)
                {
                    nextId = exact.Id;
                }
                else
                {
                    nextId = matches.First().Id;
                }
            }
            else if (matches.Count == 1)
            {
                nextId = matches[0].Id;
            }
            else
            {
                var newFolder = new MediaFolder
                {
                    Id = Guid.NewGuid().ToString().ToUpperInvariant(),
                    Name = segment,
                    ParentFolderId = parent
                };
                lock (_sessionLock)
                {
                    session.Manifest.Folders.Add(newFolder);
                    session.MarkDirty();
                    session.Save();
                }
                nextId = newFolder.Id;
            }
            parent = nextId;
        }

        return parent;
    }

    private static object GetProjectSnapshot(string status)
    {
        var session = ProjectHub.ActiveSession;
        if (session == null)
        {
            return new { status = status };
        }

        var packagePath = session.PackagePath ?? "";
        var name = string.IsNullOrEmpty(packagePath) 
            ? "Untitled Project" 
            : Path.GetFileNameWithoutExtension(packagePath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

        var timeline = session.ActiveTimeline;

        var timelineEntriesList = session.ProjectFile.Timelines.Select(t => new {
            id = t.Id,
            name = t.Name,
            fps = t.Fps,
            durationSeconds = (double)t.TotalFrames / Math.Max(1, t.Fps)
        }).ToList();

        return new
        {
            status = status,
            name = name,
            path = packagePath,
            fps = timeline.Fps,
            resolution = $"{timeline.Width}x{timeline.Height}",
            mediaCount = session.Manifest.Entries.Count,
            canGenerate = true,
            // WP-399: files outside the project the user has not allowed, from path text alone.
            blockedExternalMedia = session.ExternalMedia.Blocked(session.Manifest).Count,
            timelines = timelineEntriesList,
            openCount = 1
        };
    }

    /// <summary>
    /// The file a tool reads for its result, or null (WP-399): an external file must be one the user allowed
    /// and must still lead where it did then, so this opens an allowed external path once. Every MCP read
    /// goes back to the caller, often a hosted model. Text-only uses (a cache key) and code under the session
    /// gate that may meet many files (search_media) use <c>session.Resolver.ResolvePath</c> there and a
    /// <see cref="MediaResolver.ForTransfer"/> resolver after the gate.
    /// </summary>
    private static string? ResolveAssetPath(ProjectSession session, MediaManifestEntry entry)
    {
        return session.Resolver.ResolvePathForTransfer(entry);
    }

    /// <summary>WP-399: what every tool says about a file outside the project the user has not allowed.</summary>
    internal static string NotAllowedMessage(string mediaRef)
        => $"Media {mediaRef} is a file outside the project, and the user has not allowed Temno to use it. "
           + "Ask the user to review it in Temno's media panel and allow it if they recognise it.";

    internal static string TargetChangedMessage(string mediaRef)
        => $"Media {mediaRef} is a file outside the project that now leads somewhere other than where it did "
           + "when the user allowed it (a link or junction changed). Ask the user to review it again in Temno's media panel.";

    /// <summary>
    /// WP-399: the path of a file whose bytes this call hands on (an upload, a transcript, cues, frames),
    /// or a refusal that names why. An external file must be allowed and must still lead where it did
    /// when the user allowed it. Call it as close to the read as possible, outside the session gate:
    /// for an allowed external file it opens the path to resolve links.
    /// </summary>
    private static string? TransferPath(ProjectSession session, MediaManifestEntry entry, string mediaRef)
    {
        return session.Resolver.ExternalState(entry) switch
        {
            ExternalMediaState.NotAllowed => throw new InvalidOperationException(NotAllowedMessage(mediaRef)),
            ExternalMediaState.TargetChanged => throw new InvalidOperationException(TargetChangedMessage(mediaRef)),
            _ => session.Resolver.ResolvePath(entry),
        };
    }

    /// <summary>
    /// WP-399: <see cref="ResolveAssetPath"/> that refuses a blocked external entry by name instead of
    /// calling it offline. Path text only, so it is safe under the session gate.
    /// </summary>
    private static string? UsablePath(ProjectSession session, MediaManifestEntry entry, string mediaRef)
    {
        if (session.Resolver.IsBlocked(entry))
            throw new InvalidOperationException(NotAllowedMessage(mediaRef));
        return session.Resolver.ResolvePath(entry);
    }

    private static string Truncate(string text, int limit = 60)
    {
        return text.Length > limit ? text.Substring(0, limit) + "…" : text;
    }

    private static bool PathEquals(string? path1, string? path2)
    {
        if (path1 == null || path2 == null) return false;
        try
        {
            return string.Equals(Path.GetFullPath(path1), Path.GetFullPath(path2), StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return string.Equals(path1, path2, StringComparison.OrdinalIgnoreCase);
        }
    }
}
