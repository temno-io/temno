using System.Text.Json.Nodes;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Navigation;
using Temno.App.Controls;
using Temno.App.Controls.Shared;
using Temno.App.Services;
using Temno.App.Theme;
using Temno.App.ViewModels;
using Temno.App.Views;
using Temno.Core.Captions;
using Temno.Core.Editing;
using Temno.Core.Export;
using Temno.Core.IO;
using Temno.Core.Models;
using Temno.Core.Project;
using Temno.Core.Updates;
using Temno.Core.Session;
using Temno.Core.Transcription;
using Temno.Core.Ui;
using Temno.Mcp;
using Temno.Media;
using Temno.Media.Audio;
using Temno.Media.Cache;
using Temno.Media.Compositing;
using Temno.Media.Export;
using Temno.Media.Hardware;
using Temno.Media.Search;
using Temno.Media.Transcription;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.System;

namespace Temno.App.Views.Editor;

public sealed partial class EditorPage : Page
{
    private void AudioSubTab_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.Tag is not string tab) return;
        var speech = tab != "Music";
        AudioSpeechBtn.IsChecked = speech;
        AudioMusicBtn.IsChecked = !speech;
        AudioSpeechPane.Visibility = speech ? Visibility.Visible : Visibility.Collapsed;
        AudioMusicPane.Visibility = speech ? Visibility.Collapsed : Visibility.Visible;
    }

    private void InitializeSilenceSettings()
    {
        _minimumPauseField = new UnitNumberField
        {
            Spec = MinimumPauseSpec,
            Value = MinimumPauseSpec.Default,
        };
        _speechPaddingField = new UnitNumberField
        {
            Spec = SpeechPaddingSpec,
            Value = SpeechPaddingSpec.Default,
        };

        SilenceSettingsHost.Children.Add(BindSilenceValueRow(
            "Pause",
            "Quiet runs shorter than this stay on the timeline.",
            _minimumPauseField));
        SilenceSettingsHost.Children.Add(BindSilenceValueRow(
            "Padding",
            "Keep this much audio at both speech-facing edges of a detected pause.",
            _speechPaddingField));
    }

    private static EditorRow BindSilenceValueRow(
        string label,
        string help,
        UnitNumberField field)
    {
        var row = new EditorRow
        {
            Label = label,
            LabelHelp = help,
            RowContent = field,
            CanReset = field.DiffersFromDefault,
        };
        AutomationProperties.SetName(field, label);
        ToolTipService.SetToolTip(field, help);
        field.ValueChanged += (_, _) => row.CanReset = field.DiffersFromDefault;
        row.ResetRequested += (_, _) => field.ResetToDefault();
        return row;
    }

    private SilenceDetectionOptions CurrentSilenceOptions() => new()
    {
        MinimumPauseSeconds = _minimumPauseField.Value / 1000.0,
        SpeechPaddingSeconds = _speechPaddingField.Value / 1000.0,
    };

    private async void MarkSilence_Toggled(object sender, RoutedEventArgs e)
    {
        if (!MarkSilenceSwitch.IsOn)
        {
            var sessionOff = AppSession.Current.Session;
            if (sessionOff is not null)
                EditCommands.ReplaceMarkersNamed(sessionOff, "Silence", []);
            RefreshAfterEdit();
            AudioSpeechStatus.Text = "Silence markers cleared";
            StatusText.Text = "Mark Silence off";
            return;
        }

        var session = AppSession.Current.Session;
        if (session is null)
        {
            AudioSpeechStatus.Text = "Open a project first.";
            ShowAudioError("No project", "Open a project first, then mark silence.");
            MarkSilenceSwitch.IsOn = false;
            return;
        }

        var generation = ++_audioBusyGeneration;
        AudioSpeechStatus.Text = "Scanning for silence…";
        StatusText.Text = "Mark Silence: scanning…";
        using var scanCts = new CancellationTokenSource();
        BeginAudioBusy("Scanning for silence…", () =>
        {
            scanCts.Cancel();
            _audioBusyGeneration++;
            EndAudioBusy();
            AudioSpeechStatus.Text = "Silence scan cancelled.";
            StatusText.Text = "Mark Silence cancelled";
            MarkSilenceSwitch.IsOn = false;
        });
        try
        {
            var options = CurrentSilenceOptions();
            var summary = await Task.Run(() => ScanSilenceSummary(session, options, scanCts.Token)).ConfigureAwait(true);
            if (generation != _audioBusyGeneration)
                return;
            EditCommands.ReplaceMarkersNamed(session, "Silence", summary.MarkerFrames);
            RefreshAfterEdit();
            if (summary.SectionCount == 0)
            {
                AudioSpeechStatus.Text =
                    "No quiet sections found on timeline media (or no decodable audio). " +
                    "Remove Silence will report the same if nothing can be cut.";
                StatusText.Text = "Mark Silence: none found";
            }
            else
            {
                var marked = summary.MarkerFrames.Count;
                var capped = summary.SectionCount > marked ? $" · {marked} markers (capped)" : $" · {marked} markers";
                AudioSpeechStatus.Text =
                    $"Found {summary.SectionCount} silence section(s) · ~{summary.TotalSeconds:0.0}s total " +
                    $"across {summary.TrackCount} track(s){capped}. Use Remove Silence to ripple-delete the primary track.";
                StatusText.Text = $"Mark Silence: {summary.SectionCount} section(s)";
            }
        }
        catch (OperationCanceledException)
        {
            if (generation != _audioBusyGeneration)
                return;
            AudioSpeechStatus.Text = "Silence scan cancelled.";
            StatusText.Text = "Mark Silence cancelled";
            MarkSilenceSwitch.IsOn = false;
        }
        catch (Exception ex)
        {
            if (generation != _audioBusyGeneration)
                return;
            AudioSpeechStatus.Text = "Silence scan failed: " + ex.Message;
            StatusText.Text = "Mark Silence failed";
            ShowAudioError("Silence scan failed", ex.Message + " Confirm the clip has decodable audio, then try again.");
            MarkSilenceSwitch.IsOn = false;
        }
        finally
        {
            if (generation == _audioBusyGeneration)
                EndAudioBusy();
        }
    }

    private async void RemoveSilence_Click(object sender, RoutedEventArgs e)
    {
        if (AppSession.Current.Session is null)
        {
            AudioSpeechStatus.Text = "Open a project first.";
            ShowAudioError("No project", "Open a project first, then remove silence.");
            return;
        }

        var generation = ++_audioBusyGeneration;
        AudioSpeechStatus.Text = "Removing silence…";
        StatusText.Text = "Remove Silence…";
        BeginAudioBusy("Removing silence…", () =>
        {
            _audioBusyGeneration++;
            EndAudioBusy();
            AudioSpeechStatus.Text = "Remove Silence cancelled. Work already started may finish.";
            StatusText.Text = "Remove Silence cancelled";
        });
        try
        {
            // Same one-undo path as Agent remove_silence, with Audio-tab-only timing options.
            var options = CurrentSilenceOptions();
            var result = await Task.Run(() => ToolExecutor.RemoveSilenceForEditor(options))
                .ConfigureAwait(true);
            if (generation != _audioBusyGeneration)
                return;
            var body = string.Join("\n", result.Content.Select(c => c.Text));
            if (result.IsError)
            {
                var cause = body.StartsWith("Error: ", StringComparison.Ordinal)
                    ? body["Error: ".Length..]
                    : body;
                AudioSpeechStatus.Text = cause;
                StatusText.Text = "Remove Silence: nothing removed";
                // WP-337: a refused linked cut says what to do; the hint is for a timeline with no dead air.
                var hint = cause.StartsWith("No dead air", StringComparison.Ordinal)
                    ? " Confirm a clip with quiet ranges is on the timeline."
                    : "";
                ShowAudioError("Nothing removed", cause + hint);
                return;
            }

            RefreshAfterEdit();
            AudioSpeechStatus.Text = SummarizeRemoveSilenceResult(body);
            StatusText.Text = "Remove Silence applied";
        }
        catch (Exception ex)
        {
            if (generation != _audioBusyGeneration)
                return;
            AudioSpeechStatus.Text = "Remove Silence failed: " + ex.Message;
            StatusText.Text = "Remove Silence failed";
            ShowAudioError("Remove Silence failed", ex.Message + " Undo if the timeline looks wrong, then try again.");
        }
        finally
        {
            if (generation == _audioBusyGeneration)
                EndAudioBusy();
        }
    }

    private static SilenceScanSummary ScanSilenceSummary(
        ProjectSession session,
        SilenceDetectionOptions options,
        CancellationToken cancellationToken = default)
    {
        var timeline = session.ActiveTimeline;
        var fps = Math.Max(1, timeline.Fps);
        var detector = new SilenceDetector();
        var sectionCount = 0;
        var totalSeconds = 0.0;
        var tracksWithSilence = new HashSet<int>();
        var markerFrames = new List<int>();
        const int markerCap = 50;

        for (var trackIndex = 0; trackIndex < timeline.Tracks.Count; trackIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var clip in timeline.Tracks[trackIndex].Clips)
            {
                if (clip.MediaType is not (ClipType.video or ClipType.audio)) continue;
                var entry = session.Manifest.Entries.FirstOrDefault(e => e.Id == clip.MediaRef);
                if (entry is null || entry.HasAudio == false) continue;
                var path = session.Resolver.ResolvePath(entry);
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) continue;

                IReadOnlyList<SilenceRange> silence;
                try
                {
                    silence = detector.DetectAsync(path, options).GetAwaiter().GetResult();
                }
                catch
                {
                    continue;
                }

                var speed = Math.Max(clip.Speed, 0.0001);
                var visibleStart = (double)clip.TrimStartFrame;
                var visibleEnd = visibleStart + clip.DurationFrames * speed;
                foreach (var range in silence)
                {
                    var srcStart = Math.Max(range.Start * fps, visibleStart);
                    var srcEnd = Math.Min(range.End * fps, visibleEnd);
                    if (srcEnd <= srcStart) continue;
                    sectionCount++;
                    totalSeconds += (srcEnd - srcStart) / fps / speed;
                    tracksWithSilence.Add(trackIndex);
                    if (markerFrames.Count < markerCap)
                    {
                        var frame = (int)Math.Round(
                            clip.StartFrame + (srcStart - visibleStart) / speed,
                            MidpointRounding.AwayFromZero);
                        markerFrames.Add(Math.Max(0, frame));
                    }
                }
            }
        }

        // WP-172: carriers hold no manifest entry, so the loop above never sees nested audio.
        // The scanner plans the audible inner files (same HasAudio/offline policy via the
        // resolver below) and maps each hit back to parent frames.
        var nestedPlan = NestedSilenceScanner.Plan(
            timeline,
            SessionTimelineLookup.For(session),
            mediaRef =>
            {
                var nestedEntry = session.Manifest.Entries.FirstOrDefault(e => e.Id == mediaRef);
                if (nestedEntry is null || nestedEntry.HasAudio == false) return null;
                var nestedPath = session.Resolver.ResolvePath(nestedEntry);
                return string.IsNullOrEmpty(nestedPath) || !File.Exists(nestedPath) ? null : nestedPath;
            });
        if (nestedPlan.Count > 0)
        {
            var nestedHits = new NestedSilenceScanner()
                .ScanAsync(nestedPlan, options, cancellationToken).GetAwaiter().GetResult();
            foreach (var hit in nestedHits)
            {
                cancellationToken.ThrowIfCancellationRequested();
                sectionCount++;
                totalSeconds += hit.Seconds;
                tracksWithSilence.Add(hit.ParentTrackIndex);
                if (markerFrames.Count < markerCap)
                    markerFrames.Add(Math.Max(0, hit.ParentRange.Start));
            }
        }

        return new SilenceScanSummary(sectionCount, totalSeconds, tracksWithSilence.Count, markerFrames);
    }

    private static string SummarizeRemoveSilenceResult(string body)
    {
        // Tool JSON often includes sectionsRemoved / removedFrames — surface a short status.
        try
        {
            if (JsonNode.Parse(body) is JsonObject obj)
            {
                var sections = obj["sectionsRemoved"]?.GetValue<int>()
                    ?? obj["result"]?["sectionsRemoved"]?.GetValue<int>();
                var frames = obj["removedFrames"]?.GetValue<int>()
                    ?? obj["result"]?["removedFrames"]?.GetValue<int>();
                if (sections is { } s)
                {
                    var framePart = frames is { } f ? $" · {f} frames removed" : "";
                    return $"Removed {s} silence section(s){framePart}.";
                }
            }
        }
        catch
        {
            // fall through
        }

        return string.IsNullOrWhiteSpace(body)
            ? "Silence removed."
            : body.Length > 280 ? body[..280] + "…" : body;
    }

    private readonly record struct SilenceScanSummary(
        int SectionCount,
        double TotalSeconds,
        int TrackCount,
        IReadOnlyList<int> MarkerFrames);

    private void MusicGenerate_Click(object sender, RoutedEventArgs e)
        => OnGenerationPanelRequested(new GenerationPanelRequest(Temno.Generation.GenerationKind.Audio));

    private void MediaGenerate_Click(object sender, RoutedEventArgs e)
    {
        GenerationPanel.Toggle();
    }

    private void GenerationPanel_Submitted(object? sender, EventArgs e) => RefreshAfterEdit();

    private async void OnGenerationPanelRequested(GenerationPanelRequest request)
    {
        ShowPanel(ShellPanel.Media, forceVisible: true);
        SetMediaRailTab("Media");
        await GenerationPanel.OpenAsync(request);
    }


    // --- File operations: import media, save, save as, export ---

    private static nint MainWindowHandle()
        => WinRT.Interop.WindowNative.GetWindowHandle(
            App.MainAppWindow ?? throw new InvalidOperationException("No main window is available."));
}
