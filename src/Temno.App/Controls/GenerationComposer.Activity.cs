using System.Text.Json.Nodes;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Temno.App.Services;
using Temno.App.Theme;
using Temno.Core.Models;
using Temno.Core.Session;
using Temno.Generation;
using Temno.Mcp;

namespace Temno.App.Controls;

public sealed partial class GenerationComposer
{
    // BYOK and demo settings. Hosted generation is quote-only (WP-329): a hosted kind without a
    // catalog model has nothing to offer here.
    private void BuildLegacySettings()
    {
        if (LegacyModel is null || ToolExecutor.IsHostedImagesConfigured())
        {
            _settingsContent.Children.Add(Label("This category has no available models on the configured connection.", AppTheme.FontSize.SmMd));
            return;
        }
        if (_kind == GenerationKind.Video || _kind == GenerationKind.Audio)
            _settingsContent.Children.Add(BuildSetting(new() { Name = "duration", DefaultValue = _kind == GenerationKind.Video ? "5" : "30", Description = "Duration in seconds" }));
        if (_kind == GenerationKind.Image || _kind == GenerationKind.Video)
            _settingsContent.Children.Add(BuildSetting(new() { Name = "aspect_ratio", AllowedValues = ["16:9", "1:1", "9:16"], DefaultValue = "16:9" }));
        if (_kind == GenerationKind.Audio)
        {
            _settingsContent.Children.Add(BuildSetting(new() { Name = "voice", Description = "Voice supported by the selected model" }));
            _settingsContent.Children.Add(BuildSetting(new() { Name = "lyrics", Description = "Optional lyrics, when supported" }));
        }
        if (_kind == GenerationKind.Upscale)
            _settingsContent.Children.Add(Label("Choose a source image or video from References. The configured upscaler uses its own defaults.", AppTheme.FontSize.SmMd));
    }

    private (string, JsonObject) LegacySubmission()
    {
        var firstRefType = _references.Count > 0
            ? _project?.Manifest.Entries.FirstOrDefault(e => e.Id == _references[0])?.Type
            : null;
        return GenerationComposerSubmission.BuildLegacySubmit(
            _kind, _modelId ?? "", _prompt.Text, ReadSettings(), _references, firstRefType);
    }

    private void ShowActivity(Button anchor)
    {
        _activityContent = new StackPanel { Spacing = AppTheme.Spacing.SmMd };
        _activitySignature = null;
        BuildActivity();
        _activity = ShowPopover(anchor, _activityContent);
        _activityOpen = true;
        _activity.Closed += (_, _) => _activityOpen = false;
    }

    private void BuildActivity()
    {
        if (_activityContent is null) return;
        var entries = _project?.Manifest.Entries.Where(e => e.GenerationInput is not null).Reverse().Take(30).ToArray() ?? [];
        var signature = string.Join("|", entries.Select(e => $"{e.Id}:{e.GenerationStatus}:{e.GenerationInput?.ChargedCredits}"));
        if (_activitySignature == signature) return;
        _activitySignature = signature;
        _activityContent.Children.Clear();
        _activityContent.Children.Add(Label("Project activity", AppTheme.FontSize.MdLg));
        if (entries.Length == 0) _activityContent.Children.Add(Label("No generations yet", AppTheme.FontSize.SmMd));
        foreach (var entry in entries)
        {
            var row = new StackPanel { Spacing = AppTheme.Spacing.Xs };
            row.Children.Add(Label(entry.Name, AppTheme.FontSize.Md));
            var status = entry.GenerationStatus;
            var settled = entry.GenerationInput?.ChargedCredits;
            row.Children.Add(Label((string.IsNullOrEmpty(status) ? "Ready" : status)
                + (settled is not null ? $" · {settled} credits" : ""), AppTheme.FontSize.Xs));
            var pending = status is "preparing" or "generating" or "downloading" or "rendering" or "recovering";
            var cantCancel = status == Temno.Core.Generation.HostedJobPhases.CantCancelStatus;
            if (!cantCancel && (pending || status?.StartsWith("failed:", StringComparison.Ordinal) == true))
            {
                var action = TextButton(pending ? "Cancel" : "Resume existing job", (pending ? "Cancel " : "Resume ") + entry.Name);
                action.Click += (_, _) =>
                {
                    if (!ContextMatches(_project, _owner)) return;
                    try
                    {
                        var worked = ProjectHub.RunSerialized(() => pending ? ToolExecutor.CancelInFlightGeneration(entry.Id) : ToolExecutor.RetryGeneration(entry.Id));
                        Message(worked ? (pending ? "Cancellation requested." : "Resuming the saved job.") : "There is no recoverable job for this item.");
                        Submitted?.Invoke(this, EventArgs.Empty);
                    }
                    catch (Exception ex) { Message(ex.Message, true); }
                    BuildActivity();
                };
                row.Children.Add(action);
            }
            _activityContent.Children.Add(new Border { Padding = AppTheme.Padding.SmMd, Child = row,
                Background = AppTheme.Background.RaisedBrush, CornerRadius = AppTheme.CornerRadius.Sm });
        }
    }
}
