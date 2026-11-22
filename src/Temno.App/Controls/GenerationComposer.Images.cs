using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
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

public sealed partial class GenerationComposer
{
    private void RebuildModelState()
    {
        if (MediaModel is not null) { RebuildMediaState(); return; }
        _prompt.Visibility = Visibility.Visible;
        _prompt.MaxLength = 0;
        _suppress = true;
        _settingsContent.Children.Clear();
        _settingReaders.Clear();
        var model = ImageModel;
        if (model is not null && !_imageCatalog!.IsAvailable(model, _operation))
            _operation = model.Operations.First(o => _imageCatalog.IsAvailable(model, o.Operation)).Operation;
        _operationButton.Visibility = model is null ? Visibility.Collapsed : Visibility.Visible;
        SetButtonText(_operationButton, _operation == HostedImageOperations.Edit ? "Edit ▾" : "Generate ▾");
        var label = ModelsForKind().FirstOrDefault(m => m.Id == _modelId).Name ?? "No available models";
        SetButtonText(_models, label);
        ToolTipService.SetToolTip(_models, label);
        _settingsContent.Children.Add(Label(label, AppTheme.FontSize.Md));
        if (model is not null)
        {
            var operation = model.Operation(_operation)!;
            var primary = new[] { "size", "resolution", "aspect_ratio", "quality", "count" };
            var options = model.Settings.Where(o => o.Operations is null || o.Operations.Contains(_operation)).ToList();
            foreach (var name in primary)
            {
                var option = options.FirstOrDefault(o => o.Name == name);
                if (option is null) continue;
                if (name == "count") option = new HostedImageSettingOption
                {
                    Name = name, DefaultValue = option.DefaultValue, Description = option.Description,
                    AllowedValues = Enumerable.Range(1, Math.Max(1, operation.MaxOutputs)).Select(n => n.ToString(CultureInfo.InvariantCulture)).ToList()
                };
                _settingsContent.Children.Add(BuildSetting(option));
            }
            var more = new StackPanel { Spacing = AppTheme.Spacing.SmMd };
            foreach (var option in options.Where(o => !primary.Contains(o.Name))) more.Children.Add(BuildSetting(option));
            if (more.Children.Count > 0) _settingsContent.Children.Add(new Expander
            { Header = "More options", Content = more, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch });
            if (operation.MaxInputs == 0) _references.Clear();
            if (!operation.SupportsMask) _mask = null;
        }
        else BuildLegacySettings();
        _referencesButton.Visibility = model is not null || _kind == GenerationKind.Upscale
            || (_kind == GenerationKind.Video && !ToolExecutor.IsHostedImagesConfigured())
            ? Visibility.Visible : Visibility.Collapsed;
        _suppress = false;
        RefreshSources();
        UpdateSettingsSummary();
        InvalidateQuote();
        Message(ModelsForKind().Count == 0 ? $"No {_kind.ToString().ToLowerInvariant()} models are available on this connection." : null);
        AutomationProperties.SetName(_submit, _operation == HostedImageOperations.Edit && model is not null
            ? "Submit image edit" : "Generate " + _kind.ToString().ToLowerInvariant());
    }

    private FrameworkElement BuildSetting(HostedImageSettingOption option)
    {
        var group = new StackPanel { Spacing = AppTheme.Spacing.Xs };
        group.Children.Add(Label(SettingLabel(option.Name), AppTheme.FontSize.SmMd));
        if (ResolutionChoices.For(option.Name, option.AllowedValues, option.DefaultValue, option.Description) is { } resolution)
        {
            AddResolutionPicker(group, option.Name, resolution);
            ToolTipService.SetToolTip(group, resolution.Hint);
            return group;
        }
        if (option.AllowedValues.Count > 0 && option.AllowedValues.Count <= 6)
        {
            var values = option.AllowedValues.ToList();
            if (!values.Contains(option.DefaultValue, StringComparer.Ordinal)) values.Insert(0, "");
            string? selected = values.Contains(option.DefaultValue) ? option.DefaultValue : values[0];
            var grid = new Grid { ColumnSpacing = AppTheme.Spacing.Xs, RowSpacing = AppTheme.Spacing.Xs };
            var columns = Math.Min(3, values.Count);
            for (var i = 0; i < columns; i++) grid.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
            for (var i = 0; i < (values.Count + columns - 1) / columns; i++) grid.RowDefinitions.Add(new() { Height = GridLength.Auto });
            var buttons = new List<ToggleButton>();
            foreach (var value in values)
            {
                var b = new ToggleButton { Content = Label(FriendlyValue(value), AppTheme.FontSize.SmMd),
                    Padding = AppTheme.Padding.SmXs, MinWidth = 0, HorizontalAlignment = HorizontalAlignment.Stretch,
                    IsChecked = value == selected, Tag = value };
                AutomationProperties.SetName(b, SettingLabel(option.Name) + ": " + FriendlyValue(value));
                b.Click += (_, _) =>
                {
                    selected = value;
                    foreach (var button in buttons) button.IsChecked = (string)button.Tag == value;
                    UpdateSettingsSummary();
                    InvalidateQuote();
                };
                Grid.SetRow(b, buttons.Count / columns);
                Grid.SetColumn(b, buttons.Count % columns);
                buttons.Add(b);
                grid.Children.Add(b);
            }
            _settingReaders[option.Name] = () => string.IsNullOrEmpty(selected) ? null
                : option.Name == "count" ? JsonValue.Create(int.Parse(selected, CultureInfo.InvariantCulture)) : JsonValue.Create(selected);
            group.Children.Add(grid);
        }
        else if (option.AllowedValues.Count > 0)
        {
            var box = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
            if (!option.AllowedValues.Contains(option.DefaultValue)) box.Items.Add(new ComboBoxItem { Content = "Automatic", Tag = "" });
            foreach (var value in option.AllowedValues) box.Items.Add(new ComboBoxItem { Content = FriendlyValue(value), Tag = value });
            box.SelectedItem = box.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == option.DefaultValue) ?? box.Items[0];
            AutomationProperties.SetName(box, SettingLabel(option.Name));
            _settingReaders[option.Name] = () => string.IsNullOrEmpty((box.SelectedItem as ComboBoxItem)?.Tag as string)
                ? null : JsonValue.Create((string)((ComboBoxItem)box.SelectedItem).Tag);
            box.SelectionChanged += (_, _) => { UpdateSettingsSummary(); InvalidateQuote(); };
            group.Children.Add(box);
        }
        else if (option.Name == "web_search")
        {
            var check = new CheckBox { Content = "Use web search", IsChecked = option.DefaultValue == "true" };
            _settingReaders[option.Name] = () => JsonValue.Create(check.IsChecked == true);
            check.Checked += (_, _) => InvalidateQuote();
            check.Unchecked += (_, _) => InvalidateQuote();
            group.Children.Add(check);
        }
        else
        {
            var field = new TextBox { Text = option.DefaultValue, FontSize = AppTheme.FontSize.SmMd };
            AutomationProperties.SetName(field, SettingLabel(option.Name));
            _settingReaders[option.Name] = () =>
            {
                var text = field.Text.Trim();
                if (text.Length == 0) return null;
                return option.Name switch
                {
                    "count" => int.TryParse(text, out var count) ? JsonValue.Create(count) : throw new InvalidOperationException("Number of images must be an integer."),
                    "strength" or "image_strength" or "style_strength" or "creativity" => decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
                        ? JsonValue.Create(number) : throw new InvalidOperationException(SettingLabel(option.Name) + " must be a number."),
                    _ => JsonValue.Create(text)
                };
            };
            field.TextChanged += (_, _) => { UpdateSettingsSummary(); InvalidateQuote(); };
            group.Children.Add(field);
        }
        ToolTipService.SetToolTip(group, option.Description);
        return group;
    }

    // WP-419: every resolution setting is a dropdown of the values the model takes. A free-size model adds
    // "Custom size…", which shows width and height fields checked against the same limits.
    private void AddResolutionPicker(StackPanel group, string name, ResolutionChoices resolution)
    {
        var box = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var choice in resolution.Choices) box.Items.Add(new ComboBoxItem { Content = choice.Label, Tag = choice.Value });
        if (resolution.AllowsCustom) box.Items.Add(new ComboBoxItem { Content = "Custom size…", Tag = ResolutionChoices.Custom });
        box.SelectedItem = box.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == resolution.Selected) ?? box.Items[0];
        AutomationProperties.SetName(box, SettingLabel(name));
        ToolTipService.SetToolTip(box, resolution.Hint);
        group.Children.Add(box);
        string Selected() => (box.SelectedItem as ComboBoxItem)?.Tag as string ?? "";
        if (!resolution.AllowsCustom)
        {
            _settingReaders[name] = () => resolution.Read(Selected()) is { } value ? JsonValue.Create(value) : null;
            box.SelectionChanged += (_, _) => { UpdateSettingsSummary(); InvalidateQuote(); };
            return;
        }
        var limits = resolution.Limits;
        var step = limits?.MultipleOf > 1 ? limits.MultipleOf : 16;
        var start = resolution.DefaultSize ?? (1024, 1024);
        UnitNumberField Field(string label, double value)
        {
            var spec = new UnitFieldSpec { Unit = "px", Minimum = limits?.MinSide ?? 1, Maximum = limits?.MaxSide ?? 16384,
                Step = step, UnitsPerPixel = step / 4.0, Default = value };
            var field = new UnitNumberField { Spec = spec, Value = value };
            AutomationProperties.SetName(field, label);
            ToolTipService.SetToolTip(field, label + ". " + resolution.Hint);
            return field;
        }
        var width = Field("Custom width", start.W);
        var height = Field("Custom height", start.H);
        var times = Label("×", AppTheme.FontSize.SmMd);
        times.VerticalAlignment = VerticalAlignment.Center;
        var custom = new Grid { ColumnSpacing = AppTheme.Spacing.Xs, Visibility = Visibility.Collapsed };
        custom.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        custom.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        custom.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(times, 1);
        Grid.SetColumn(height, 2);
        custom.Children.Add(width);
        custom.Children.Add(times);
        custom.Children.Add(height);
        group.Children.Add(custom);
        var previous = Selected();
        box.SelectionChanged += (_, _) =>
        {
            var selected = Selected();
            if (selected == ResolutionChoices.Custom && ResolutionChoices.ParseSize(previous) is { } seed)
            {
                width.Value = seed.W;
                height.Value = seed.H;
            }
            previous = selected;
            custom.Visibility = selected == ResolutionChoices.Custom ? Visibility.Visible : Visibility.Collapsed;
            UpdateSettingsSummary();
            InvalidateQuote();
        };
        width.ValueChanged += (_, _) => { UpdateSettingsSummary(); InvalidateQuote(); };
        height.ValueChanged += (_, _) => { UpdateSettingsSummary(); InvalidateQuote(); };
        _settingReaders[name] = () => resolution.Read(Selected(), width.Value, height.Value) is { } value ? JsonValue.Create(value) : null;
    }

    private static string SettingLabel(string name) => name switch
    {
        "output_format" => "File format", "aspect_ratio" => "Aspect ratio", "web_search" => "Web search",
        "count" => "Count", "size" => "Resolution", "quality" => "Quality", "resolution" => "Resolution",
        "target_resolution" => "Target resolution",
        "strength" => "Change strength", "image_strength" => "Reference strength", "style_strength" => "Style strength",
        "style" => "Style", "creativity" => "Creativity", "background" => "Background", "thinking" => "Reasoning",
        _ => name.Replace('_', ' ')
    };

    private static string FriendlyValue(string value) => value switch
    {
        "" => "Automatic", "1024x1024" => "Square", "1536x1024" or "1024x768" => "Landscape", "1024x1536" => "Portrait",
        "1920x1080" => "Landscape 1080p", "2560x1440" => "Landscape 2K", "3840x2160" => "Landscape 4K",
        "low" => "Low", "medium" => "Medium", "high" => "High", "xhigh" => "Extra high", "max" => "Max", "auto" => "Automatic",
        _ => value
    };

    private JsonObject ReadSettings()
    {
        var settings = new JsonObject();
        foreach (var entry in _settingReaders) { var value = entry.Value(); if (value is not null) settings[entry.Key] = value; }
        return settings;
    }

    private void UpdateSettingsSummary()
    {
        if (_suppress) return;
        try
        {
            var settings = ReadSettings();
            UpdateMediaSettingVisibility(settings);
            var labels = new[] { "size", "aspect_ratio", "resolution", "quality" }
                .Select(key => settings[key]?.ToString()).Where(v => !string.IsNullOrEmpty(v)).Select(v => FriendlyValue(v!)).Take(2);
            var summary = string.Join(" · ", labels);
            SetButtonText(_settings, string.IsNullOrEmpty(summary) ? "Options ▾" : summary + " ▾");
            ToolTipService.SetToolTip(_settings, "Generation settings: " + summary);
        }
        catch (InvalidOperationException) { SetButtonText(_settings, "Check options ▾"); }
    }

    private void ShowReferences()
    {
        if (MediaModel is not null) { ShowMediaReferences(); return; }
        if (_project is null) return;
        if (ImageModel is { } profile && profile.Operation(_operation)?.MaxInputs == 0
            && _imageCatalog!.IsAvailable(profile, HostedImageOperations.Edit))
        {
            _operation = HostedImageOperations.Edit;
            RebuildModelState();
        }
        var body = new StackPanel { Spacing = AppTheme.Spacing.Sm };
        var limit = ImageModel?.Operation(_operation)?.MaxInputs ?? (_kind is GenerationKind.Upscale or GenerationKind.Video ? 1 : 0);
        body.Children.Add(Label(_kind == GenerationKind.Upscale ? "Source to upscale" : $"Source images · up to {limit}", AppTheme.FontSize.Md));
        var assets = _project.Manifest.Entries.Where(e => string.IsNullOrEmpty(e.GenerationStatus)
            && (e.Type == ClipType.image || (_kind is GenerationKind.Upscale or GenerationKind.Video && e.Type == ClipType.video))).ToList();
        if (limit == 0) body.Children.Add(Label("This operation does not accept source images.", AppTheme.FontSize.SmMd));
        else foreach (var asset in assets)
        {
            var check = new CheckBox { Content = SourceRow(asset), IsChecked = _references.Contains(asset.Id), Tag = asset.Id };
            AutomationProperties.SetName(check, "Reference " + asset.Name);
            check.Checked += (_, _) =>
            {
                if (_references.Contains(asset.Id)) return;
                if (_references.Count >= limit) { check.IsChecked = false; Message($"This model accepts at most {limit} source images.", true); return; }
                _references.Add(asset.Id); RefreshSources(); InvalidateQuote();
            };
            check.Unchecked += (_, _) => { _references.Remove(asset.Id); RefreshSources(); InvalidateQuote(); };
            body.Children.Add(check);
        }
        if (assets.Count == 0) body.Children.Add(Label("Import an image into the project to use it as a reference.", AppTheme.FontSize.SmMd));
        if (ImageModel?.Operation(_operation)?.SupportsMask == true)
        {
            var box = new ComboBox { Header = "Mask (optional)", HorizontalAlignment = HorizontalAlignment.Stretch };
            box.Items.Add(new ComboBoxItem { Content = "No mask", Tag = "" });
            foreach (var asset in assets.Where(a => a.Type == ClipType.image)) box.Items.Add(SourceItem(asset));
            box.SelectedItem = box.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == _mask) ?? box.Items[0];
            box.SelectionChanged += (_, _) => { _mask = (box.SelectedItem as ComboBoxItem)?.Tag as string; RefreshSources(); InvalidateQuote(); };
            body.Children.Add(box);
        }
        ShowPopover(_referencesButton, body);
    }

    private void RefreshSources()
    {
        if (MediaModel is not null) { RefreshMediaSources(); return; }
        _sourceStrip.Children.Clear();
        foreach (var id in _references.ToArray())
        {
            var asset = _project?.Manifest.Entries.FirstOrDefault(e => e.Id == id);
            if (asset is null) { _references.Remove(id); continue; }
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = AppTheme.Spacing.Xs };
            row.Children.Add(SourceThumbnail(asset));
            var remove = TextButton(asset.Name + " ×", "Remove reference " + asset.Name);
            remove.Click += (_, _) => { _references.Remove(id); RefreshSources(); InvalidateQuote(); };
            row.Children.Add(remove);
            _sourceStrip.Children.Add(row);
        }
        if (!string.IsNullOrEmpty(_mask))
        {
            var removeMask = TextButton("Mask ×", "Remove generation mask");
            removeMask.Click += (_, _) => { _mask = null; RefreshSources(); InvalidateQuote(); };
            _sourceStrip.Children.Add(removeMask);
        }
        _sourceStrip.Visibility = _sourceStrip.Children.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        SetButtonText(_referencesButton, _references.Count == 0 ? "+ References" : $"{_references.Count} reference{(_references.Count == 1 ? "" : "s")} ▾");
    }

    private void InvalidateQuote(bool schedule = true)
    {
        if (_suppress) return;
        _quote.Invalidate();
        _debounce?.Stop();
        UpdateCreditDisplay();
        if (schedule && _ready && HasHostedQuote && !_busy && (MediaModel is not null ? MediaInputsReady() : _prompt.Text.Trim().Length > 0) && Visibility == Visibility.Visible)
            _debounce?.Start();
    }

    private bool ContextMatches(ProjectSession? project, string? owner) => project is not null
        && ReferenceEquals(project, AppSession.Current.Session) && ReferenceEquals(project, _project)
        && project.PackagePath == _packagePath
        && owner == AccountSessionService.Current.UserId;

    private async Task RequestQuoteAsync()
    {
        _debounce.Stop();
        if (MediaModel is not null) { await RequestMediaQuoteAsync(); return; }
        if (_busy || !_ready || ImageModel is not { } model || _project is null) return;
        var prompt = _prompt.Text.Trim();
        if (prompt.Length == 0) { Message("Enter a prompt to check its price."); return; }
        var operation = model.Operation(_operation)!;
        if (_references.Count < operation.MinInputs || _references.Count > operation.MaxInputs)
        { Message($"{(_operation == HostedImageOperations.Edit ? "Edit" : "This model")} needs {operation.MinInputs}–{operation.MaxInputs} source images.", true); return; }
        var revision = _quote.BeginQuote();
        var project = _project;
        var owner = AccountSessionService.Current.UserId;
        var version = _loadVersion;
        try
        {
            var settings = ReadSettings();
            var args = new JsonObject { ["model"] = model.Id, ["operation"] = _operation, ["prompt"] = prompt,
                ["settings"] = settings, ["referenceMediaRefs"] = new JsonArray(_references.Select(r => JsonValue.Create(r)).ToArray()) };
            if (!string.IsNullOrEmpty(_mask)) args["maskMediaRef"] = _mask;
            SetBusy(true);
            Message("Checking price…");
            var result = await Task.Run(() => ToolExecutor.ExecuteGenerationForProject(project, "quote_image", args, owner));
            if (version != _loadVersion || !ContextMatches(project, owner) || revision != _quote.Revision) return;
            if (result.IsError) throw new InvalidOperationException(ResultText(result));
            var quote = JsonNode.Parse(result.Content.FirstOrDefault()?.Text ?? "{}");
            if (!DateTimeOffset.TryParse(quote?["expiresAt"]?.GetValue<string>(), out var expiry)
                || !_quote.Accept(revision, quote?["quoteId"]?.GetValue<string>() ?? "", quote?["credits"]?.GetValue<int>() ?? -1, expiry, DateTimeOffset.UtcNow))
                throw new InvalidOperationException("The price expired. Check it again.");
            Message("Credits are reserved on submit and charged on delivery." + (_imageCatalog?.DevelopmentPrices == true ? " Development prices." : ""));
        }
        catch (Exception ex) { if (version == _loadVersion && ContextMatches(project, owner)) Message(ex.Message, true); }
        finally { if (version == _loadVersion) SetBusy(false); }
    }

    private async Task SubmitAsync()
    {
        if (_busy || !_ready || _project is null || string.IsNullOrEmpty(_modelId)) return;
        if (HasHostedQuote && !_quote.IsValid(DateTimeOffset.UtcNow)) { await RequestQuoteAsync(); return; }
        var project = _project;
        var owner = AccountSessionService.Current.UserId;
        var revision = _quote.Revision;
        var version = _loadVersion;
        SetBusy(true);
        try
        {
            await AccountSessionService.Current.RefreshBalanceAsync();
            if (!ContextMatches(project, owner) || revision != _quote.Revision)
            { Message("The project or account changed. Open the generation panel again.", true); return; }
            int? cost = HasHostedQuote ? _quote.Credits : null;
            if (cost is not null && AccountSessionService.Current.Balance is { } balance && !balance.CanCover(cost.Value))
                throw new InvalidOperationException($"Needs {cost} credits; {balance.Available} are available.");
            JsonObject args;
            string tool;
            if (ImageModel is not null)
            {
                if (!_quote.IsValid(DateTimeOffset.UtcNow)) { InvalidateQuote(false); throw new InvalidOperationException("Price expired. Check it again."); }
                tool = _operation;
                args = new JsonObject { ["quoteId"] = _quote.QuoteId, ["maxCredits"] = _quote.Credits, ["model"] = _modelId, ["prompt"] = _prompt.Text.Trim() };
            }
            else if (MediaModel is not null)
            {
                if (!_quote.IsValid(DateTimeOffset.UtcNow)) { InvalidateQuote(false); throw new InvalidOperationException("Price expired. Check it again."); }
                (tool, args) = GenerationComposerSubmission.BuildHostedMediaSubmit(
                    _kind, _quote.QuoteId!, _quote.Credits, _modelId, _operation, MediaPrompt, MediaAcceptsPrompt);
            }
            else (tool, args) = LegacySubmission();
            var result = ToolExecutor.ExecuteGenerationForProject(project, tool, args, owner);
            if (result.IsError) throw new InvalidOperationException(ResultText(result));
            InvalidateQuote(false);
            Message(ToolExecutor.IsFakeGenerationActive() ? "Demo generation started." : "Generation started. Follow it in Project activity.");
            Submitted?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex) { Message(ex.Message, true); }
        finally { if (version == _loadVersion) SetBusy(false); }
    }

    private void UpdateCreditDisplay()
    {
        if (_balance is null) return;
        var account = AccountSessionService.Current;
        SetButtonText(_balance, account.Balance is { OwnerTesting: true } ? "◉ Owner"
            : account.Balance is { } balance ? $"◉ {balance.Available}" : "◉ —");
        ToolTipService.SetToolTip(_balance, account.Balance is { OwnerTesting: true }
            ? "Owner testing · development credits are supplied automatically"
            : account.Balance is { } b ? $"{b.Available} credits available; {b.Reserved} reserved" : account.BalanceError ?? "Sign in to use hosted credits");
        var validQuote = _quote.IsValid(DateTimeOffset.UtcNow);
        int? credits = HasHostedQuote && validQuote ? _quote.Credits : null;
        SetButtonText(_cost, _busy ? "Checking…" : string.IsNullOrEmpty(_modelId) ? "—"
            : credits is { } cost ? $"{cost} credits" : HasHostedQuote ? "— credits" : "Provider billing");
        var enough = credits is null || account.Balance is not { } currentBalance || currentBalance.CanCover(credits.Value);
        _cost.Foreground = enough ? AppTheme.Text.SecondaryBrush : AppTheme.Status.ErrorBrush;
        _submit.IsEnabled = !_busy && _ready && !string.IsNullOrEmpty(_modelId)
            && (MediaModel is not null ? MediaInputsReady() : _prompt.Text.Trim().Length > 0 || _kind == GenerationKind.Upscale);
        _cost.IsEnabled = !_busy && HasHostedQuote && (MediaModel is null || MediaInputsReady());
        if (_cancelNote is not null)
            _cancelNote.Visibility = HasHostedQuote ? Visibility.Visible : Visibility.Collapsed;
        var cancelCopy = HasHostedQuote ? " " + Temno.Core.Generation.HostedJobPhases.CancelCopy : "";
        ToolTipService.SetToolTip(_submit, (credits is { } price
            ? (_operation == HostedImageOperations.Edit && ImageModel is not null ? "Edit" : "Generate") + $" for {price} credits"
            : HasHostedQuote ? "Check price" : "Generate with configured provider") + cancelCopy);
        ToolTipService.SetToolTip(_cost, (HasHostedQuote ? "Check generation price. " : "Check generation price") + (HasHostedQuote ? Temno.Core.Generation.HostedJobPhases.CancelCopy : ""));
    }

    private static string ResultText(ToolCallResult result)
    {
        var text = result.Content.FirstOrDefault()?.Text ?? "The request failed.";
        if (text.StartsWith("Error:", StringComparison.Ordinal)) text = text[6..].Trim();
        return text.Length > 300 ? text[..300] + "…" : text;
    }
}
