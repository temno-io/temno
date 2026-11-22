using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Temno.App.Services;
using Temno.Core.Session;
using Temno.Generation;
using Temno.Mcp;

namespace Temno.App.Controls;

public sealed partial class AiEditControl : UserControl
{
    private ProjectSession? _session;
    private string? _mediaRef;
    public event EventHandler? MediaLibraryChanged;

    public AiEditControl() => InitializeComponent();

    public void Bind(ProjectSession? session, string? mediaRef, string kind, bool canUpscale, bool visible)
    {
        _session = session;
        _mediaRef = mediaRef;
        Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        var saved = !string.IsNullOrEmpty(session?.PackagePath);
        FakeBanner.Visibility = ToolExecutor.IsFakeGenerationActive() ? Visibility.Visible : Visibility.Collapsed;
        UnconfiguredLabel.Visibility = saved ? Visibility.Collapsed : Visibility.Visible;
        UnconfiguredLabel.Text = "Save the project before generating.";
        GenerateImageBtn.IsEnabled = GenerateVideoBtn.IsEnabled = PromptBox.IsEnabled = saved;
        EditImageBtn.Visibility = kind == "image" && mediaRef is not null ? Visibility.Visible : Visibility.Collapsed;
        EditImageBtn.IsEnabled = saved;
        UpscaleBtn.Visibility = canUpscale && mediaRef is not null ? Visibility.Visible : Visibility.Collapsed;
        UpscaleBtn.IsEnabled = saved;
        var entry = session?.Manifest.Entries.FirstOrDefault(e => e.Id == mediaRef);
        var status = entry?.GenerationStatus;
        CancelBtn.IsEnabled = status is "preparing" or "generating" or "downloading";
        RetryBtn.IsEnabled = status?.StartsWith("failed:", StringComparison.Ordinal) == true && entry?.GenerationInput?.BackendJobId is not null;
        StatusLabel.Text = status ?? "";
        ErrorLabel.Visibility = Visibility.Collapsed;
    }

    private void Open(GenerationKind kind, string? operation = null, string? source = null)
    {
        if (!ReferenceEquals(_session, AppSession.Current.Session)) return;
        GenerationPanelRequests.Open(new(kind, source, operation, PromptBox.Text));
    }

    private void GenerateImage_Click(object sender, RoutedEventArgs e) => Open(GenerationKind.Image);
    private void GenerateVideo_Click(object sender, RoutedEventArgs e) => Open(GenerationKind.Video);
    private void EditImage_Click(object sender, RoutedEventArgs e) => Open(GenerationKind.Image, HostedImageOperations.Edit, _mediaRef);
    private void Upscale_Click(object sender, RoutedEventArgs e) => Open(GenerationKind.Upscale, source: _mediaRef);
    private void Cancel_Click(object sender, RoutedEventArgs e) => ChangeJob(retry: false);
    private void Retry_Click(object sender, RoutedEventArgs e) => ChangeJob(retry: true);

    private void ChangeJob(bool retry)
    {
        if (_mediaRef is null || !ReferenceEquals(_session, AppSession.Current.Session)) return;
        try
        {
            var changed = retry ? ToolExecutor.RetryGeneration(_mediaRef) : ToolExecutor.CancelInFlightGeneration(_mediaRef);
            StatusLabel.Text = changed ? (retry ? "Resuming saved job…" : "Cancellation requested…") : "No pending job for this asset.";
            MediaLibraryChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex) { ErrorLabel.Text = ex.Message; ErrorLabel.Visibility = Visibility.Visible; }
    }
}
