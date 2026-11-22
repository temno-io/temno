using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Temno.App.Controls.Shared;
using Temno.App.Services;
using Temno.App.Theme;
using Temno.Core.Models;
using Temno.Core.Session;
using Temno.Core.Ui;
using Temno.Generation;
using Temno.Mcp;

namespace Temno.App.Controls;

public sealed partial class GenerationComposer : UserControl
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };
    private readonly GenerationDraftQuote _quote = new();
    private readonly Dictionary<GenerationKind, ToggleButton> _tabs = [];
    private readonly Dictionary<string, Func<JsonNode?>> _settingReaders = [];
    private readonly List<string> _references = [];
    private readonly StackPanel _root = new() { Spacing = AppTheme.Spacing.Xs };
    private readonly Grid _header = new();
    private readonly StackPanel _typeTabs = new() { Orientation = Orientation.Horizontal, Spacing = AppTheme.Spacing.Xxs };
    private readonly StackPanel _utilities = new() { Orientation = Orientation.Horizontal, Spacing = AppTheme.Spacing.Xxs, HorizontalAlignment = HorizontalAlignment.Right };
    private readonly StackPanel _sourceStrip = new() { Orientation = Orientation.Horizontal, Spacing = AppTheme.Spacing.Xs };
    private readonly TextBox _prompt = new();
    private readonly TextBlock _message = new();
    private readonly Button _models = new();
    private readonly Button _settings = new();
    private readonly Button _operationButton = new();
    private readonly Button _referencesButton = new();
    private readonly Button _cost = new();
    private readonly Button _submit = new();
    private readonly Button _balance = new();
    private readonly Button _retry = new();
    private readonly StackPanel _settingsContent = new() { Spacing = AppTheme.Spacing.SmMd };
    private readonly List<Flyout> _popovers = [];
    private readonly DispatcherQueueTimer _debounce;
    private readonly DispatcherQueueTimer _statusTimer;
    private ProjectSession? _project;
    private string? _packagePath;
    private HostedImageCatalog? _imageCatalog;
    private HostedMediaCatalog? _mediaCatalog;
    private readonly List<(string Role, string MediaRef)> _mediaReferences = [];
    private string? _pendingMediaSource;
    private List<CatalogEntry> _catalog = [];
    private GenerationKind _kind = GenerationKind.Image;
    private string? _modelId;
    private string _operation = HostedImageOperations.Generate;
    private string? _mask;
    private string? _owner;
    private bool _busy;
    private bool _loading;
    private bool _suppress;
    private bool _ready;
    private int _loadVersion;
    private Flyout? _activity;
    private StackPanel? _activityContent;
    private bool _activityOpen;
    private string? _activitySignature;
    private TextBlock? _cancelNote;

    public event EventHandler? Submitted;

    public GenerationComposer()
    {
        Visibility = Visibility.Collapsed;
        HorizontalAlignment = HorizontalAlignment.Stretch;
        _suppress = true;
        BuildUi();
        _suppress = false;
        _debounce = DispatcherQueue.CreateTimer();
        _debounce.Interval = TimeSpan.FromMilliseconds(650);
        _debounce.IsRepeating = false;
        _debounce.Tick += async (_, _) => await RequestQuoteAsync();
        _statusTimer = DispatcherQueue.CreateTimer();
        _statusTimer.Interval = TimeSpan.FromSeconds(1);
        _statusTimer.Tick += (_, _) =>
        {
            if (_quote.QuoteId is not null && !_quote.IsValid(DateTimeOffset.UtcNow))
            {
                InvalidateQuote(schedule: false);
                Message("Price expired. Check the price again before generating.");
            }
            UpdateCreditDisplay();
            if (_activityOpen) BuildActivity();
        };
        Loaded += (_, _) => AccountSessionService.Current.Changed += AccountChanged;
        Unloaded += (_, _) =>
        {
            AccountSessionService.Current.Changed -= AccountChanged;
            _debounce.Stop();
            _statusTimer.Stop();
            ClosePopovers();
            _loadVersion++;
        };
        SizeChanged += (_, _) => ArrangeHeader();
    }

    private void BuildUi()
    {
        _header.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        _header.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        _header.RowDefinitions.Add(new() { Height = GridLength.Auto });
        _header.RowDefinitions.Add(new() { Height = GridLength.Auto });
        foreach (var (kind, icon) in new[] { (GenerationKind.Image, Icons.Image), (GenerationKind.Video, Icons.Video),
                     (GenerationKind.Audio, Icons.MusicNote2), (GenerationKind.Upscale, Icons.Sparkle) })
        {
            var tab = new ToggleButton { Padding = AppTheme.Padding.SmXs, MinWidth = 0, BorderThickness = new Thickness(0),
                CornerRadius = AppTheme.CornerRadius.Sm, IsChecked = kind == _kind };
            var face = new StackPanel { Spacing = AppTheme.Spacing.Xxs, HorizontalAlignment = HorizontalAlignment.Center };
            face.Children.Add(new FluentIcon { Icon = icon, Width = AppTheme.IconSize.Xs, Height = AppTheme.IconSize.Xs,
                Fill = AppTheme.Text.SecondaryBrush, HorizontalAlignment = HorizontalAlignment.Center });
            face.Children.Add(Label(kind.ToString(), AppTheme.FontSize.Xs));
            tab.Content = face;
            AutomationProperties.SetName(tab, "Generate " + kind.ToString().ToLowerInvariant() + " tab");
            tab.Click += (_, _) => SelectKind(kind);
            _tabs[kind] = tab;
            _typeTabs.Children.Add(tab);
        }
        _header.Children.Add(_typeTabs);
        ConfigureButton(_balance, "Credits", "Refresh generation credit balance");
        _balance.Foreground = AppTheme.Accent.ReadableBrush;
        _balance.Click += async (_, _) => { await AccountSessionService.Current.RefreshBalanceAsync(); UpdateCreditDisplay(); };
        _utilities.Children.Add(_balance);
        var history = IconButton(Icons.ArrowClockwise, "Project generation activity");
        history.Click += (_, _) => ShowActivity(history);
        _utilities.Children.Add(history);
        var close = IconButton(Icons.Dismiss, "Close generation panel");
        close.Click += (_, _) => Hide();
        _utilities.Children.Add(close);
        Grid.SetColumn(_utilities, 1);
        _header.Children.Add(_utilities);
        _root.Children.Add(_header);

        var refsRow = new Grid();
        refsRow.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        refsRow.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        ConfigureButton(_operationButton, "Generate ▾", "Generation operation");
        _operationButton.Click += (_, _) => ShowOperations();
        refsRow.Children.Add(_operationButton);
        ConfigureButton(_referencesButton, "+ References", "Choose generation references");
        _referencesButton.HorizontalAlignment = HorizontalAlignment.Right;
        _referencesButton.Click += (_, _) => ShowReferences();
        Grid.SetColumn(_referencesButton, 1);
        refsRow.Children.Add(_referencesButton);
        _root.Children.Add(refsRow);
        _root.Children.Add(new ScrollViewer { Content = _sourceStrip, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled });

        var composer = new StackPanel();
        _prompt.AcceptsReturn = true;
        _prompt.TextWrapping = TextWrapping.Wrap;
        _prompt.Height = AppTheme.GenerationPanel.PromptHeight;
        _prompt.MinHeight = 0;
        _prompt.Padding = AppTheme.Padding.SmMd;
        _prompt.BorderThickness = new Thickness(0);
        _prompt.Background = AppTheme.Background.RaisedBrush;
        _prompt.FontSize = AppTheme.FontSize.Md;
        _prompt.PlaceholderText = "Describe the image";
        AutomationProperties.SetName(_prompt, "Generation prompt");
        _prompt.TextChanged += (_, _) => InvalidateQuote();
        _prompt.KeyDown += (_, e) =>
        {
            // Keep typing/edit shortcuts inside the composer, away from the timeline.
            e.Handled = true;
            if (e.Key == Windows.System.VirtualKey.Escape) _prompt.Focus(FocusState.Programmatic);
        };
        composer.Children.Add(_prompt);
        var modelRow = new Grid { Padding = AppTheme.Padding.Xs, ColumnSpacing = AppTheme.Spacing.Xxs };
        modelRow.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        modelRow.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        ConfigureButton(_models, "Loading models…", "Choose generation model");
        _models.HorizontalAlignment = HorizontalAlignment.Stretch;
        _models.HorizontalContentAlignment = HorizontalAlignment.Left;
        _models.Click += (_, _) => ShowModels();
        modelRow.Children.Add(_models);
        ConfigureButton(_settings, "Options ▾", "Generation settings");
        _settings.Click += (_, _) => ShowSettings();
        Grid.SetColumn(_settings, 1);
        modelRow.Children.Add(_settings);
        composer.Children.Add(modelRow);
        var submitRow = new Grid { Padding = AppTheme.Padding.Xs, ColumnSpacing = AppTheme.Spacing.Xs };
        submitRow.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        submitRow.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        ConfigureButton(_cost, "— credits", "Check generation price");
        _cost.HorizontalAlignment = HorizontalAlignment.Right;
        _cost.Click += async (_, _) => await RequestQuoteAsync();
        submitRow.Children.Add(_cost);
        ConfigureButton(_submit, "↑", "Generate image");
        _submit.Width = AppTheme.GenerationPanel.SubmitSize;
        _submit.Height = AppTheme.GenerationPanel.SubmitSize;
        _submit.Padding = new Thickness(0);
        _submit.CornerRadius = AppTheme.CornerRadius.Capsule;
        _submit.Background = AppTheme.Accent.SelectedBrush;
        _submit.Foreground = AppTheme.Accent.ReadableBrush;
        _submit.Click += async (_, _) => await SubmitAsync();
        Grid.SetColumn(_submit, 1);
        submitRow.Children.Add(_submit);
        composer.Children.Add(submitRow);
        _cancelNote = Label(Temno.Core.Generation.HostedJobPhases.CancelCopy, AppTheme.FontSize.Xs);
        _cancelNote.Margin = new Thickness(AppTheme.Spacing.SmMd, 0, AppTheme.Spacing.SmMd, AppTheme.Spacing.Sm);
        _cancelNote.TextWrapping = TextWrapping.WrapWholeWords;
        _cancelNote.Visibility = Visibility.Collapsed;
        composer.Children.Add(_cancelNote);
        _root.Children.Add(new Border { Child = composer, Background = AppTheme.Background.RaisedBrush,
            CornerRadius = AppTheme.CornerRadius.Md, BorderBrush = AppTheme.Border.PrimaryBrush,
            BorderThickness = new Thickness(AppTheme.BorderWidth.Thin) });
        _message.FontSize = AppTheme.FontSize.Xs;
        _message.Foreground = AppTheme.Text.SecondaryBrush;
        _message.TextWrapping = TextWrapping.WrapWholeWords;
        _message.Visibility = Visibility.Collapsed;
        AutomationProperties.SetLiveSetting(_message, AutomationLiveSetting.Polite);
        _root.Children.Add(_message);
        ConfigureButton(_retry, "Retry loading models", "Retry loading generation models");
        _retry.Visibility = Visibility.Collapsed;
        _retry.Click += async (_, _) => await LoadCatalogAsync();
        _root.Children.Add(_retry);
        Content = new Border { Background = AppTheme.Background.SurfaceBrush, BorderBrush = AppTheme.Border.PrimaryBrush,
            BorderThickness = new Thickness(0, AppTheme.BorderWidth.Thin, 0, 0), Padding = AppTheme.Padding.SmMd,
            Child = new ScrollViewer { Content = _root, MaxHeight = AppTheme.GenerationPanel.MaxHeight,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Auto } };
    }

    private void ArrangeHeader()
    {
        var narrow = ActualWidth < AppTheme.GenerationPanel.HeaderBreakpoint;
        Grid.SetRow(_utilities, narrow ? 1 : 0);
        Grid.SetColumn(_utilities, narrow ? 0 : 1);
        Grid.SetColumnSpan(_utilities, narrow ? 2 : 1);
        Grid.SetColumnSpan(_typeTabs, narrow ? 2 : 1);
    }

    public void SyncProject(ProjectSession? project)
    {
        if (ReferenceEquals(project, _project) && _packagePath == project?.PackagePath) return;
        Hide();
        _loadVersion++;
        _project = project;
        _packagePath = project?.PackagePath;
        _ready = false;
        _busy = false;
        _loading = false;
        _quote.SetContext(project, AccountSessionService.Current.UserId);
        _prompt.Text = "";
        _references.Clear();
        _sourceImages.Clear();
        _mask = null;
        _modelId = null;
        _imageCatalog = null;
        _mediaCatalog = null;
        _mediaReferences.Clear();
        _pendingMediaSource = null;
        _catalog = [];
    }

    public async Task OpenAsync(GenerationPanelRequest? request = null)
    {
        SyncProject(AppSession.Current.Session);
        Visibility = Visibility.Visible;
        _statusTimer.Start();
        if (request is not null && _busy && !_loading)
        {
            Message("Please wait for the current request, then choose the image again.");
            return;
        }
            if (request is not null)
        {
            InvalidateQuote(schedule: false);
            if (_kind != request.Kind) { _references.Clear(); _mediaReferences.Clear(); _mask = null; _modelId = null; }
            _kind = request.Kind;
            _operation = request.Operation ?? HostedImageOperations.Generate;
            if (request.MediaRef is not null) { _references.Clear(); _references.Add(request.MediaRef); }
            _pendingMediaSource = request.Kind == GenerationKind.Image ? null : request.MediaRef;
            if (request.Prompt is not null) _prompt.Text = request.Prompt;
            if (_operation == HostedImageOperations.Edit && _imageCatalog is { } images
                && (ImageModel is not { } selected || !images.IsAvailable(selected, _operation)))
                _modelId = images.Models.FirstOrDefault(m => images.IsAvailable(m, _operation))?.Id;
        }
        if (_project?.PackagePath is null) { Message("Save this project before generating.", true); SetBusy(false); return; }
        if (!_ready || _owner != AccountSessionService.Current.UserId) await LoadCatalogAsync();
        else if (request is not null) SelectKind(_kind, clearReferences: false);
        _prompt.Focus(FocusState.Programmatic);
    }

    public void Toggle()
    {
        if (Visibility == Visibility.Visible) Hide();
        else _ = OpenAsync();
    }

    private void Hide()
    {
        ClosePopovers();
        Visibility = Visibility.Collapsed;
        _debounce?.Stop();
        _statusTimer?.Stop();
    }

    private async Task LoadCatalogAsync()
    {
        var version = ++_loadVersion;
        _ready = false;
        _loading = true;
        _owner = AccountSessionService.Current.UserId;
        _quote.SetContext(_project, _owner);
        SetBusy(true);
        SetButtonText(_models, "Loading models…");
        Message("Loading available models…");
        _retry.Visibility = Visibility.Collapsed;
        try
        {
            await AccountSessionService.Current.RefreshBalanceAsync();
            // Hosted generation is quote-only (WP-329): a hosted catalog that fails to load leaves the
            // panel unavailable with Retry, never on a fixed offer from list_models.
            var imageTask = ToolExecutor.IsHostedImagesConfigured() ? ToolExecutor.GetImageCatalogAsync() : null;
            var mediaTask = ToolExecutor.IsHostedMediaConfigured() ? ToolExecutor.GetMediaCatalogAsync() : null;
            var catalogTask = Task.Run(() => ToolExecutor.Execute("list_models", new JsonObject()));
            var images = imageTask is null ? null : await imageTask;
            var media = mediaTask is null ? null : await mediaTask;
            var response = await catalogTask;
            if (version != _loadVersion || _owner != AccountSessionService.Current.UserId) return;
            _imageCatalog = images;
            _mediaCatalog = media;
            var node = response.IsError ? null : JsonNode.Parse(response.Content.FirstOrDefault()?.Text ?? "{}");
            _catalog = node?["models"]?.Deserialize<List<CatalogEntry>>(Json) ?? [];
            if (_catalog.Count == 0 && _imageCatalog is null)
                throw new InvalidOperationException(ToolExecutor.IsGenerationEndpointMissing()
                    ? ToolExecutor.GenerationEndpointMissingMessage
                    : AccountSessionService.Current.IsSignedIn
                    ? "No models are available on this connection. Check Settings → Generation."
                    : "Sign in to use hosted generation, or configure a provider in Settings → Generation.");
            _ready = true;
            Message(null);
            SelectKind(_kind, clearReferences: false);
        }
        catch (Exception ex)
        {
            if (version != _loadVersion) return;
            Message(ex.Message, true);
            SetButtonText(_models, "Models unavailable");
            _retry.Visibility = Visibility.Visible;
        }
        finally
        {
            if (version == _loadVersion)
            {
                _loading = false;
                SetBusy(false);
                if (_owner != AccountSessionService.Current.UserId && Visibility == Visibility.Visible) _ = LoadCatalogAsync();
                else if (_ready) InvalidateQuote();
            }
        }
    }

    private void AccountChanged(object? sender, EventArgs e) => DispatcherQueue.TryEnqueue(() =>
    {
        _quote.SetContext(_project, AccountSessionService.Current.UserId);
        if (_owner != AccountSessionService.Current.UserId)
        {
            _ready = false;
            if (Visibility == Visibility.Visible && !_loading) _ = LoadCatalogAsync();
        }
        UpdateCreditDisplay();
    });

    private HostedImageProfile? ImageModel => _kind == GenerationKind.Image ? _imageCatalog?.Get(_modelId ?? "") : null;
    private CatalogEntry? LegacyModel => _catalog.FirstOrDefault(m => m.Id == _modelId && m.Kind == _kind);
    private List<(string Id, string Name)> ModelsForKind()
        => _kind == GenerationKind.Image && _imageCatalog is not null
            ? _imageCatalog.Models.Where(m => m.Operations.Any(o => _imageCatalog.IsAvailable(m, o.Operation)))
                .Select(m => (m.Id, m.DisplayName)).ToList()
            : _kind != GenerationKind.Image && _mediaCatalog is not null
                ? _mediaCatalog.Models.Where(m => m.Category == _kind.ToString().ToLowerInvariant())
                    .Select(m => (m.Id, m.DisplayName)).ToList()
            : _catalog.Where(m => m.Kind == _kind && m.ImageProfile is null).Select(m => (m.Id, m.DisplayName)).ToList();

    private void SelectKind(GenerationKind kind, bool clearReferences = true)
    {
        if (_busy && !_loading) return;
        var changed = _kind != kind;
        if (!changed && clearReferences && _ready && !_loading) { _tabs[kind].IsChecked = true; return; }
        _kind = kind;
        if (changed && clearReferences) { _references.Clear(); _mediaReferences.Clear(); _mask = null; _operation = HostedImageOperations.Generate; }
        foreach (var tab in _tabs) tab.Value.IsChecked = tab.Key == kind;
        var models = ModelsForKind();
        if (!models.Any(m => m.Id == _modelId)) _modelId = _mediaCatalog?.Models
            .FirstOrDefault(m => m.Category == kind.ToString().ToLowerInvariant()
                && m.Operations.Any(o => _mediaCatalog.IsAvailable(m, o.Operation)))?.Id ?? models.FirstOrDefault().Id;
        if (kind == GenerationKind.Image && _operation == HostedImageOperations.Edit && _imageCatalog is { } images
            && (ImageModel is not { } selected || !images.IsAvailable(selected, _operation)))
            _modelId = images.Models.FirstOrDefault(m => images.IsAvailable(m, _operation))?.Id;
        _prompt.PlaceholderText = kind == GenerationKind.Upscale ? "No prompt needed for upscaling" : "Describe the " + kind.ToString().ToLowerInvariant();
        SeedMediaSource();
        RebuildModelState();
        if (models.Count == 0 && _ready) Message($"No {kind.ToString().ToLowerInvariant()} models are available on this connection.");
    }

    private void ShowModels()
    {
        var body = new StackPanel { Spacing = AppTheme.Spacing.Xs };
        body.Children.Add(Label("Choose " + _kind.ToString().ToLowerInvariant() + " model", AppTheme.FontSize.Md));
        var search = new TextBox { PlaceholderText = "Search models", FontSize = AppTheme.FontSize.SmMd };
        AutomationProperties.SetName(search, "Search generation models");
        body.Children.Add(search);
        var rows = new StackPanel { Spacing = AppTheme.Spacing.Xxs };
        body.Children.Add(rows);
        Flyout? flyout = null;
        void Populate()
        {
            rows.Children.Clear();
            foreach (var model in ModelsForKind().Where(m => m.Name.Contains(search.Text, StringComparison.OrdinalIgnoreCase)))
            {
                var row = TextButton((_modelId == model.Id ? "✓  " : "") + model.Name, "Select " + model.Name);
                row.HorizontalAlignment = HorizontalAlignment.Stretch;
                row.HorizontalContentAlignment = HorizontalAlignment.Left;
                row.Click += (_, _) => { _modelId = model.Id; RebuildModelState(); flyout?.Hide(); };
                rows.Children.Add(row);
            }
            if (rows.Children.Count == 0) rows.Children.Add(Label("No available models match.", AppTheme.FontSize.SmMd));
        }
        search.TextChanged += (_, _) => Populate();
        Populate();
        flyout = ShowPopover(_models, body);
    }

    private void ShowOperations()
    {
        if (MediaModel is not null) { ShowMediaOperations(); return; }
        if (ImageModel is not { } model) return;
        var body = new StackPanel { Spacing = AppTheme.Spacing.Xs };
        Flyout? flyout = null;
        foreach (var operation in model.Operations.Where(o => _imageCatalog!.IsAvailable(model, o.Operation)))
        {
            var row = TextButton(operation.Operation == HostedImageOperations.Edit ? "Edit image" : "Generate image", operation.Operation);
            row.Click += (_, _) => { _operation = operation.Operation; RebuildModelState(); flyout?.Hide(); };
            body.Children.Add(row);
        }
        flyout = ShowPopover(_operationButton, body);
    }

    private void ShowSettings() => ShowPopover(_settings, _settingsContent);

    private Flyout ShowPopover(FrameworkElement anchor, FrameworkElement body)
    {
        ClosePopovers();
        if (body.Parent is ScrollViewer prior) prior.Content = null;
        var scroll = new ScrollViewer
        {
            Content = body,
            Width = AppTheme.GenerationPanel.PopoverWidth,
            MaxHeight = AppTheme.GenerationPanel.PopoverHeight,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        var flyout = new Flyout
        {
            Content = scroll,
            FlyoutPresenterStyle = new Style(typeof(FlyoutPresenter))
            {
                Setters = { new Setter(PaddingProperty, AppTheme.Padding.SmMd),
                    new Setter(BackgroundProperty, AppTheme.Background.SurfaceBrush),
                    new Setter(BorderBrushProperty, AppTheme.Border.PrimaryBrush),
                    new Setter(CornerRadiusProperty, AppTheme.CornerRadius.Md) }
            }
        };
        _popovers.Add(flyout);
        flyout.Closed += (_, _) => { if (flyout.Content is ScrollViewer s) s.Content = null; _popovers.Remove(flyout); };
        WindowPopout.ShowFlyoutAt(
            flyout,
            anchor,
            scroll,
            PopoutSide.Top,
            PopoutAlignment.Left,
            maxHeightCap: AppTheme.GenerationPanel.PopoverHeight,
            preferredWidth: AppTheme.GenerationPanel.PopoverWidth);
        return flyout;
    }

    private void ClosePopovers() { foreach (var f in _popovers.ToArray()) f.Hide(); }

    private static TextBlock Label(string text, double size) => new()
    {
        Text = text, FontSize = size, Foreground = AppTheme.Text.SecondaryBrush,
        TextWrapping = TextWrapping.WrapWholeWords
    };
    private static void SetButtonText(Button button, string text)
    {
        if (button.Content is TextBlock label) { if (label.Text != text) label.Text = text; return; }
        button.Content = new TextBlock { Text = text, FontSize = AppTheme.FontSize.SmMd,
            TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = AppTheme.GenerationPanel.CompactLabelWidth };
    }
    private static void ConfigureButton(Button button, string text, string name)
    {
        SetButtonText(button, text);
        button.MinWidth = 0;
        button.MinHeight = AppTheme.GenerationPanel.ToolbarHeight;
        button.Padding = AppTheme.Padding.XsXxs;
        button.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        button.BorderThickness = new Thickness(0);
        AutomationProperties.SetName(button, name);
        ToolTipService.SetToolTip(button, name);
    }
    private static Button TextButton(string text, string name) { var b = new Button(); ConfigureButton(b, text, name); return b; }
    private static Button IconButton(IconDefinition icon, string name)
    {
        var b = TextButton("", name);
        b.Content = new FluentIcon { Icon = icon, Width = AppTheme.IconSize.Xs, Height = AppTheme.IconSize.Xs, Fill = AppTheme.Text.SecondaryBrush };
        return b;
    }
    private void Message(string? message, bool error = false)
    {
        _message.Text = message ?? "";
        _message.Foreground = error ? AppTheme.Status.ErrorBrush : AppTheme.Text.SecondaryBrush;
        _message.Visibility = string.IsNullOrWhiteSpace(message) ? Visibility.Collapsed : Visibility.Visible;
    }
    private void SetBusy(bool busy)
    {
        _busy = busy;
        _prompt.IsEnabled = !busy;
        _models.IsEnabled = _settings.IsEnabled = _operationButton.IsEnabled = _referencesButton.IsEnabled = !busy && _ready;
        _sourceStrip.IsHitTestVisible = !busy;
        foreach (var tab in _tabs.Values) tab.IsEnabled = !busy;
        UpdateCreditDisplay();
    }
}
