using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Temno.App.Theme;
using Temno.Media.Export;

namespace Temno.App.Controls;

public sealed class ExportQueueView : UserControl
{
    private readonly StackPanel _rows = new() { Spacing = AppTheme.Spacing.Md };
    private readonly TextBlock _empty = new() { Text = "No exports yet", Foreground = AppTheme.Text.TertiaryBrush };
    private readonly Dictionary<string, JobRow> _jobs = new();

    public ExportQueueView()
    {
        Content = new Border
        {
            Background = AppTheme.Background.RaisedBrush,
            CornerRadius = new CornerRadius(AppTheme.Radius.Sm),
            Padding = AppTheme.Padding.Lg,
            Child = _rows,
        };
    }

    public void Refresh(IEnumerable<ExportJob> jobs)
    {
        var visible = jobs.OrderByDescending(job => job.CreatedAt).Take(10).ToArray();
        if (visible.Length == 0)
        {
            if (!_rows.Children.Contains(_empty)) _rows.Children.Add(_empty);
            return;
        }
        _rows.Children.Remove(_empty);
        foreach (var job in visible)
        {
            if (!_jobs.TryGetValue(job.Id, out var row))
            {
                row = new JobRow(job.OutputPath);
                _jobs.Add(job.Id, row);
                _rows.Children.Insert(0, row.Panel);
            }
            row.Update(job);
        }
        foreach (var id in _jobs.Keys.Except(visible.Select(job => job.Id)).ToArray())
        {
            _rows.Children.Remove(_jobs[id].Panel);
            _jobs.Remove(id);
        }
    }

    private sealed class JobRow
    {
        public StackPanel Panel { get; } = new() { Spacing = AppTheme.Spacing.Sm };
        private readonly TextBlock _state = new() { FontWeight = Microsoft.UI.Text.FontWeights.SemiBold };
        private readonly ProgressBar _progress = new() { Minimum = 0, Maximum = 100 };
        private readonly TextBlock _error = new() { TextWrapping = TextWrapping.Wrap, Foreground = AppTheme.Status.ErrorBrush };

        public JobRow(string path)
        {
            Panel.Children.Add(_state);
            var name = new TextBlock { Text = Path.GetFileName(path), TextTrimming = TextTrimming.CharacterEllipsis,
                Foreground = AppTheme.Text.SecondaryBrush, FontSize = AppTheme.FontSize.SmMd };
            ToolTipService.SetToolTip(name, path);
            Panel.Children.Add(name);
            Panel.Children.Add(_progress);
            Panel.Children.Add(_error);
        }

        public void Update(ExportJob job)
        {
            _state.Text = job.State switch
            {
                ExportJobState.Waiting => "Queued",
                ExportJobState.Rendering => $"Rendering · {Math.Round(job.Progress * 100):0}%",
                ExportJobState.Completed => "Export complete",
                ExportJobState.Cancelled => "Export cancelled",
                _ => "Export failed",
            };
            _progress.Value = job.Progress * 100;
            _progress.IsIndeterminate = job.State == ExportJobState.Waiting;
            _progress.Visibility = job.State.IsPending() ? Visibility.Visible : Visibility.Collapsed;
            _error.Text = job.Error ?? "";
            _error.Visibility = string.IsNullOrWhiteSpace(job.Error) ? Visibility.Collapsed : Visibility.Visible;
        }
    }
}
