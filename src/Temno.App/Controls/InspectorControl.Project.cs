using System.Text.Json;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Temno.App.Theme;
using Temno.App.Controls.Shared;
using Temno.App.Services;
using Temno.Core.Project;
using Temno.Core.Ui;
using Temno.App.ViewModels;
using Temno.Core.Editing;
using Temno.Core.Models;
using Temno.Core.Session;
using Temno.Mcp;
using Temno.Media.Text;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using ClipModel = Temno.Core.Models.Clip;
using TextAlign = Temno.Core.Models.TextAlignment;
using TransformModel = Temno.Core.Models.Transform;

namespace Temno.App.Controls;

public sealed partial class InspectorControl : UserControl
{
    private void WireProjectSettingsFields()
    {
        SettingsCustomFpsBox.Spec = new UnitFieldSpec
        {
            Unit = " fps",
            Minimum = CanvasSizePresets.MinProjectFps,
            Maximum = CanvasSizePresets.MaxProjectFps,
            Step = 1,
            Decimals = 0,
            UnitsPerPixel = 0.25,
            Default = 30,
        };
        SettingsCustomFpsBox.CommitRequested += (_, _) => CommitCustomProjectFps();
        SettingsCustomFpsBox.LostFocus += (_, _) => CommitCustomProjectFps();
        SettingsCustomFpsBox.ScrubCompleted += (_, _) => CommitCustomProjectFps();
    }

    private void CommitCustomProjectFps()
    {
        if (_projectSettingsSuppress || _session is null) return;
        if (SettingsCustomFpsBox.Visibility != Visibility.Visible) return;
        var fps = Math.Clamp(
            (int)Math.Round(SettingsCustomFpsBox.Value),
            CanvasSizePresets.MinProjectFps,
            CanvasSizePresets.MaxProjectFps);
        if (_session.ProjectFile.Timelines.All(t => t.Fps == fps)) return;
        var result = EditCommands.SetProjectSettings(
            _session, fps: fps, width: null, height: null, aspectRatio: null, quality: null);
        if (result.Ok)
        {
            RefreshProjectPanel();
            ProjectSettingsChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void RefreshProjectPanel()
    {
        _projectSettingsSuppress = true;
        try
        {
            if (_session is null)
            {
                ProjectNameText.Text = "—";
                ProjectPathText.Text = "—";
                ToolTipService.SetToolTip(ProjectPathTooltipHost, null);
                ProjectPathActions.Visibility = Visibility.Collapsed;
                ProjectDurationText.Text = "0:00";
                SettingsResolutionText.Text = "—";
                SettingsCustomFpsLabel.Visibility = Visibility.Collapsed;
                SettingsCustomFpsBox.Visibility = Visibility.Collapsed;
                ApplyProjectSummary(InspectorProjectSummary.Empty);
                return;
            }

            var path = _session.PackagePath ?? "";
            ProjectNameText.Text = string.IsNullOrEmpty(path)
                ? "Untitled Project"
                : System.IO.Path.GetFileNameWithoutExtension(path.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar));
            ProjectPathText.Text = string.IsNullOrEmpty(path) ? "—" : path;
            ToolTipService.SetToolTip(ProjectPathTooltipHost, string.IsNullOrEmpty(path) ? null : path);
            ToolTipService.SetToolTip(ProjectPathText, string.IsNullOrEmpty(path) ? null : path);
            var hasPath = !string.IsNullOrEmpty(path);
            ProjectPathActions.Visibility = hasPath ? Visibility.Visible : Visibility.Collapsed;
            RevealProjectPathBtn.IsEnabled = hasPath && (System.IO.Directory.Exists(path) || System.IO.File.Exists(path));
            CopyProjectPathLabel.Text = "Copy";

            var tl = _session.ActiveTimeline;
            var fps = Math.Max(1, tl.Fps);
            var totalSec = tl.TotalFrames / (double)fps;
            var ts = TimeSpan.FromSeconds(totalSec);
            ProjectDurationText.Text = ts.TotalHours >= 1 ? ts.ToString(@"h\:mm\:ss") : ts.ToString(@"m\:ss");
            SettingsResolutionText.Text = $"{tl.Width} × {tl.Height} · {tl.Fps} fps";

            var fpsTag = CanvasSizePresets.MatchProjectFpsTag(fps);
            SelectComboByTag(SettingsFpsBox, fpsTag);
            var customFps = fpsTag == "custom";
            SettingsCustomFpsLabel.Visibility = customFps ? Visibility.Visible : Visibility.Collapsed;
            SettingsCustomFpsBox.Visibility = customFps ? Visibility.Visible : Visibility.Collapsed;
            SettingsCustomFpsBox.Value = Math.Clamp(fps, CanvasSizePresets.MinProjectFps, CanvasSizePresets.MaxProjectFps);

            var aspectTag = CanvasSizePresets.MatchAspectTag(tl.Width, tl.Height);
            SettingsAspectCustomItem.Visibility = aspectTag is null ? Visibility.Visible : Visibility.Collapsed;
            SelectComboByTag(SettingsAspectBox, aspectTag ?? "custom");

            var qualityTag = CanvasSizePresets.MatchQualityTag(tl.Width, tl.Height);
            SettingsQualityCustomItem.Visibility = qualityTag is null ? Visibility.Visible : Visibility.Collapsed;
            SelectComboByTag(SettingsQualityBox, qualityTag ?? "custom");

            ApplyProjectSummary(InspectorProjectSummary.FromSession(_session));
        }
        finally
        {
            _projectSettingsSuppress = false;
        }
    }

    private void CopyProjectPath_Click(object sender, RoutedEventArgs e)
    {
        var path = _session?.PackagePath;
        if (string.IsNullOrEmpty(path)) return;
        CopyPathToClipboard(path, CopyProjectPathLabel);
    }

    private void RevealProjectPath_Click(object sender, RoutedEventArgs e)
    {
        var path = _session?.PackagePath;
        if (!string.IsNullOrEmpty(path) && (System.IO.Directory.Exists(path) || System.IO.File.Exists(path)))
            AppSession.RevealInExplorer(path);
    }

    private void CopyMediaPath_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_mediaAssetPath)) return;
        CopyPathToClipboard(_mediaAssetPath, CopyMediaPathLabel);
    }

    private void RevealMediaPath_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrEmpty(_mediaAssetPath) && (System.IO.File.Exists(_mediaAssetPath) || System.IO.Directory.Exists(_mediaAssetPath)))
            AppSession.RevealInExplorer(_mediaAssetPath);
    }

    private void CopyPathToClipboard(string path, TextBlock feedbackLabel)
    {
        try
        {
            var data = new DataPackage();
            data.SetText(path);
            Clipboard.SetContent(data);
            feedbackLabel.Text = "Copied";
            var timer = DispatcherQueue.CreateTimer();
            timer.Interval = TimeSpan.FromSeconds(1.5);
            timer.IsRepeating = false;
            timer.Tick += (s, _) =>
            {
                s.Stop();
                feedbackLabel.Text = "Copy";
            };
            timer.Start();
        }
        catch
        {
            // Clipboard access can be briefly contended by another process; ignore non-fatally.
        }
    }

    /// <summary>
    /// WP-148: the no-selection state's project-scope facts. Recomputed from the session on every
    /// refresh — see <see cref="InspectorProjectSummary"/> for why nothing here is cached.
    /// </summary>
    private void ApplyProjectSummary(InspectorProjectSummary summary)
    {
        ProjectTimelineCountText.Text = summary.TimelineLabel;

        var problem = summary.ProblemLabel;
        ProjectAttentionText.Text = problem ?? "";
        ProjectAttentionCard.Visibility = problem is null ? Visibility.Collapsed : Visibility.Visible;
    }
}
