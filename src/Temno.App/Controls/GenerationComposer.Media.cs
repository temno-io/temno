using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Temno.App.Services;
using Temno.App.Theme;
using Temno.Core.Models;
using Temno.Generation;
using Temno.Mcp;

namespace Temno.App.Controls;

public sealed partial class GenerationComposer
{
    private readonly Dictionary<string, FrameworkElement> _mediaSettingGroups = [];
    private HostedMediaProfile? MediaModel => _kind == GenerationKind.Image ? null : _mediaCatalog?.Get(_modelId ?? "");
    private bool HasHostedQuote => ImageModel is not null || MediaModel is not null;
    private HostedMediaOperationProfile? MediaOperation => MediaModel?.Operation(_operation);
    private bool MediaNeedsPrompt => MediaOperation?.PromptRequired == true;
    private bool MediaAcceptsPrompt => MediaOperation?.PromptMaxLength > 0;
    private string MediaPrompt => MediaAcceptsPrompt ? _prompt.Text.Trim() : "";

    private static string MediaOperationLabel(string operation) => operation switch
    {
        "text_to_video" => "Generate video", "image_to_video" => "Animate frames",
        "reference_to_video" => "Video from references", "video_edit" => "Edit video",
        "motion_control" => "Motion control", "lip_sync" => "Lip sync",
        "text_to_music" => "Create music", "video_to_music" => "Music for video",
        "text_to_sfx" => "Create sound effects", "video_to_sfx" => "Sound effects for video",
        "text_to_speech" => "Text to speech", "dubbing" => "Dub audio",
        "audio_isolation" => "Isolate voice", "text_to_audio" => "Generate audio",
        "upscale_video" => "Upscale video", "upscale_image" => "Upscale image",
        _ => operation.Replace('_', ' ')
    };

    private static string MediaRoleLabel(string role) => role switch
    {
        "first_frame" => "First frame", "last_frame" => "Last frame",
        "reference_image" => "Reference image", "source_video" => "Source video",
        "source_audio" => "Source audio", _ => role.Replace('_', ' ')
    };

    private void SeedMediaSource()
    {
        if (_pendingMediaSource is not { } source || _mediaCatalog is null) return;
        var asset = _project?.Manifest.Entries.FirstOrDefault(e => e.Id == source);
        if (asset is null) { _pendingMediaSource = null; return; }
        var wanted = _kind == GenerationKind.Upscale
            ? asset.Type == ClipType.image ? "upscale_image" : "upscale_video"
            : _kind == GenerationKind.Video ? asset.Type == ClipType.image ? "image_to_video" : "video_edit"
            : "audio_isolation";
        var candidates = _mediaCatalog.Models.Where(m => m.Category == _kind.ToString().ToLowerInvariant()
            && m.Operation(wanted)?.Inputs.Any(s => s.Kinds.Contains(asset.Type.ToString())) == true).ToList();
        var model = candidates.FirstOrDefault(m => m.Id == _modelId && _mediaCatalog.IsAvailable(m, wanted))
            ?? candidates.FirstOrDefault(m => _mediaCatalog.IsAvailable(m, wanted)) ?? candidates.FirstOrDefault();
        if (model is null) return;
        _modelId = model.Id;
        _operation = wanted;
        var slot = model.Operation(wanted)!.Inputs.First(s => s.Kinds.Contains(asset.Type.ToString()));
        _mediaReferences.Clear();
        _mediaReferences.Add((slot.Role, source));
        _pendingMediaSource = null;
    }

    private void RebuildMediaState()
    {
        var model = MediaModel!;
        _suppress = true;
        _settingsContent.Children.Clear();
        _settingReaders.Clear();
        _mediaSettingGroups.Clear();
        var operation = model.Operations.FirstOrDefault(o => o.Operation == _operation)
            ?? model.Operations.FirstOrDefault(o => _mediaCatalog!.IsAvailable(model, o.Operation))
            ?? model.Operations.FirstOrDefault();
        _operation = operation?.Operation ?? "";
        _operationButton.Visibility = Visibility.Visible;
        SetButtonText(_operationButton, MediaOperationLabel(_operation) + " ▾");
        SetButtonText(_models, model.DisplayName);
        ToolTipService.SetToolTip(_models, model.DisplayName);
        _settingsContent.Children.Add(Label(model.DisplayName, AppTheme.FontSize.Md));
        var settings = model.Settings.Where(s => s.Operations is null || s.Operations.Contains(_operation)).ToList();
        foreach (var option in settings)
        {
            FrameworkElement group;
            if (option.AllowedValues.Count == 0 && bool.TryParse(option.DefaultValue, out var initial))
            {
                var check = new CheckBox { Content = SettingLabel(option.Name), IsChecked = initial };
                AutomationProperties.SetName(check, SettingLabel(option.Name));
                _settingReaders[option.Name] = () => JsonValue.Create(check.IsChecked == true);
                check.Checked += (_, _) => InvalidateQuote();
                check.Unchecked += (_, _) => InvalidateQuote();
                ToolTipService.SetToolTip(check, option.Description);
                group = check;
            }
            else group = BuildSetting(new HostedImageSettingOption
                {
                    Name = option.Name, AllowedValues = option.ChoiceValues().ToList(),
                    DefaultValue = option.DefaultValue == "(omitted)" ? "" : option.DefaultValue,
                    Description = option.Description
                });
            _settingsContent.Children.Add(group);
            _mediaSettingGroups[option.Name] = group;
        }
        if (settings.Count == 0) _settingsContent.Children.Add(Label("This operation uses the model's standard settings.", AppTheme.FontSize.SmMd));
        if (operation is not null)
        {
            _mediaReferences.RemoveAll(r => !operation.Inputs.Any(s => s.Role == r.Role
                && _project?.Manifest.Entries.Any(e => e.Id == r.MediaRef && s.Kinds.Contains(e.Type.ToString())) == true));
            foreach (var slot in operation.Inputs)
            {
                var excess = _mediaReferences.Where(r => r.Role == slot.Role).Skip(slot.Max).ToArray();
                foreach (var item in excess) _mediaReferences.Remove(item);
            }
        }
        else _mediaReferences.Clear();
        _referencesButton.Visibility = operation?.Inputs.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        _prompt.Visibility = MediaAcceptsPrompt ? Visibility.Visible : Visibility.Collapsed;
        _prompt.MaxLength = Math.Max(1, operation?.PromptMaxLength ?? 2000);
        _prompt.PlaceholderText = _operation == "text_to_speech" ? "Write the words to speak"
            : "Describe the " + (_kind == GenerationKind.Audio ? "audio" : _operation == "upscale_image" ? "image" : "video")
              + (MediaNeedsPrompt ? "" : " (optional)");
        _suppress = false;
        RefreshMediaSources();
        UpdateSettingsSummary();
        InvalidateQuote();
        Message(operation is null || !_mediaCatalog!.IsAvailable(model, _operation)
            ? "This model operation is not available yet." : null);
        AutomationProperties.SetName(_submit, MediaOperationLabel(_operation));
    }

    private void ShowMediaOperations()
    {
        var model = MediaModel!;
        var body = new StackPanel { Spacing = AppTheme.Spacing.Xs };
        Flyout? flyout = null;
        foreach (var op in model.Operations)
        {
            var row = TextButton(MediaOperationLabel(op.Operation)
                + (_mediaCatalog!.IsAvailable(model, op.Operation) ? "" : " · unavailable"), "Select " + MediaOperationLabel(op.Operation));
            row.Click += (_, _) => { _operation = op.Operation; RebuildMediaState(); flyout?.Hide(); };
            body.Children.Add(row);
        }
        flyout = ShowPopover(_operationButton, body);
    }

    private void ShowMediaReferences()
    {
        var model = MediaModel;
        var operation = model?.Operations.FirstOrDefault(o => o.Operation == _operation);
        if (operation is null || _project is null) return;
        var body = new StackPanel { Spacing = AppTheme.Spacing.SmMd };
        if (_operation == "reference_to_video")
            body.Children.Add(Label("Choose at least one image or video reference.", AppTheme.FontSize.SmMd));
        if (_operation is "dubbing" or "audio_isolation")
        {
            body.Children.Add(Label("Source audio or video", AppTheme.FontSize.Md));
            var source = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
            source.Items.Add(new ComboBoxItem { Content = "Choose source…", Tag = "" });
            foreach (var asset in _project.Manifest.Entries.Where(e => string.IsNullOrEmpty(e.GenerationStatus)
                && operation.Inputs.Any(s => s.Kinds.Contains(e.Type.ToString()))))
                source.Items.Add(SourceItem(asset));
            source.SelectedItem = source.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == _mediaReferences.FirstOrDefault().MediaRef) ?? source.Items[0];
            AutomationProperties.SetName(source, "Source audio or video");
            source.SelectionChanged += (_, _) =>
            {
                _mediaReferences.Clear();
                if ((source.SelectedItem as ComboBoxItem)?.Tag is string id
                    && _project.Manifest.Entries.FirstOrDefault(e => e.Id == id) is { } asset)
                    _mediaReferences.Add((asset.Type == ClipType.audio ? "source_audio" : "source_video", id));
                RefreshMediaSources(); InvalidateQuote();
            };
            body.Children.Add(source);
            ShowPopover(_referencesButton, body);
            return;
        }
        foreach (var slot in operation.Inputs)
        {
            body.Children.Add(Label(MediaRoleLabel(slot.Role) + (slot.Min == 0 ? " (optional)" : " (required)"), AppTheme.FontSize.Md));
            var assets = _project.Manifest.Entries.Where(e => string.IsNullOrEmpty(e.GenerationStatus)
                && slot.Kinds.Contains(e.Type.ToString())).ToList();
            if (assets.Count == 0)
                body.Children.Add(Label("Import " + string.Join(" or ", slot.Kinds) + " media to choose a source.", AppTheme.FontSize.SmMd));
            if (slot.Max == 1)
            {
                var box = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
                box.Items.Add(new ComboBoxItem { Content = "Choose source…", Tag = "" });
                foreach (var asset in assets) box.Items.Add(SourceItem(asset));
                var current = _mediaReferences.FirstOrDefault(r => r.Role == slot.Role).MediaRef;
                box.SelectedItem = box.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == current) ?? box.Items[0];
                AutomationProperties.SetName(box, MediaRoleLabel(slot.Role));
                box.SelectionChanged += (_, _) =>
                {
                    _mediaReferences.RemoveAll(r => r.Role == slot.Role);
                    if ((box.SelectedItem as ComboBoxItem)?.Tag is string id && id.Length > 0)
                    {
                        if (_operation is "dubbing" or "audio_isolation") _mediaReferences.Clear();
                        _mediaReferences.Add((slot.Role, id));
                    }
                    RefreshMediaSources(); InvalidateQuote();
                };
                body.Children.Add(box);
            }
            else foreach (var asset in assets)
            {
                var check = new CheckBox { Content = SourceRow(asset), IsChecked = _mediaReferences.Contains((slot.Role, asset.Id)) };
                AutomationProperties.SetName(check, MediaRoleLabel(slot.Role) + " " + asset.Name);
                check.Checked += (_, _) =>
                {
                    if (_mediaReferences.Contains((slot.Role, asset.Id))) return;
                    if (_mediaReferences.Count >= 8)
                    { check.IsChecked = false; Message("Choose up to 8 sources for one generation.", true); return; }
                    if (_mediaReferences.Count(r => r.Role == slot.Role) >= slot.Max)
                    { check.IsChecked = false; Message($"Choose up to {slot.Max} {MediaRoleLabel(slot.Role).ToLowerInvariant()} sources.", true); return; }
                    _mediaReferences.Add((slot.Role, asset.Id)); RefreshMediaSources(); InvalidateQuote();
                };
                check.Unchecked += (_, _) => { _mediaReferences.Remove((slot.Role, asset.Id)); RefreshMediaSources(); InvalidateQuote(); };
                body.Children.Add(check);
            }
        }
        ShowPopover(_referencesButton, body);
    }

    private void RefreshMediaSources()
    {
        _sourceStrip.Children.Clear();
        foreach (var input in _mediaReferences.ToArray())
        {
            var asset = _project?.Manifest.Entries.FirstOrDefault(e => e.Id == input.MediaRef);
            if (asset is null) { _mediaReferences.Remove(input); continue; }
            var remove = TextButton(MediaRoleLabel(input.Role) + ": " + asset.Name + " ×", "Remove " + MediaRoleLabel(input.Role));
            remove.Click += (_, _) => { _mediaReferences.Remove(input); RefreshMediaSources(); InvalidateQuote(); };
            _sourceStrip.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = AppTheme.Spacing.Xs,
                Children = { SourceThumbnail(asset), remove } });
        }
        _sourceStrip.Visibility = _sourceStrip.Children.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        SetButtonText(_referencesButton, _mediaReferences.Count == 0 ? "+ Sources" : $"{_mediaReferences.Count} sources ▾");
    }

    private bool MediaInputsReady()
    {
        if (MediaModel is not { } model || !_mediaCatalog!.IsAvailable(model, _operation)) return false;
        if (MediaNeedsPrompt && string.IsNullOrWhiteSpace(_prompt.Text)) return false;
        var op = model.Operations.First(o => o.Operation == _operation);
        if (MediaPrompt.Length > 0 && MediaPrompt.Length < op.PromptMinLength) return false;
        if (_operation is "dubbing" or "audio_isolation" && _mediaReferences.Count != 1) return false;
        if (_mediaReferences.Count > 8) return false;
        if (_operation == "reference_to_video" && _mediaReferences.Count == 0) return false;
        if (_modelId == "seedance-2.5" && _operation == "reference_to_video"
            && !_mediaReferences.Any(r => r.Role != "source_audio")) return false;
        return op.Inputs.All(s => _mediaReferences.Count(r => r.Role == s.Role) >= s.Min
            && _mediaReferences.Count(r => r.Role == s.Role) <= s.Max);
    }

    private JsonObject ReadMediaSettings()
    {
        var settings = ReadSettings();
        foreach (var key in settings.Select(p => p.Key).ToArray())
        {
            var value = settings[key]?.ToString();
            if (value is null) continue;
            var option = MediaModel?.Settings.FirstOrDefault(s => s.Name == key
                && (s.Operations is null || s.Operations.Contains(_operation)));
            if (option?.AllowedValues.Count > 0) continue;
            bool optionalDuration = key == "duration" && option?.DefaultValue == "(omitted)";
            if (!optionalDuration && key is ("duration" or "count" or "seed" or "target_fps" or "num_speakers"))
            {
                if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number))
                    throw new InvalidOperationException(SettingLabel(key) + " must be a whole number.");
                settings[key] = number;
            }
            else if (optionalDuration || key is "stability" or "upscale_factor" or "noise_scale" or "speed" or "volume" or "pitch"
                     or "prompt_influence" or "creativity" or "temperature"
                     || decimal.TryParse(option?.DefaultValue, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
            {
                if (!decimal.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
                    throw new InvalidOperationException(SettingLabel(key) + " must be a number.");
                settings[key] = number;
            }
            else if (value is "true" or "false") settings[key] = value == "true";
        }
        if (settings["upscale_mode"]?.ToString() is { } mode)
            settings.Remove(mode == "target" ? "upscale_factor" : "target_resolution");
        return settings;
    }

    private void UpdateMediaSettingVisibility(JsonObject settings)
    {
        if (MediaModel is null || settings["upscale_mode"]?.ToString() is not { } mode) return;
        if (_mediaSettingGroups.TryGetValue("target_resolution", out var target))
            target.Visibility = mode == "target" ? Visibility.Visible : Visibility.Collapsed;
        if (_mediaSettingGroups.TryGetValue("upscale_factor", out var factor))
            factor.Visibility = mode == "target" ? Visibility.Collapsed : Visibility.Visible;
    }

    private async Task RequestMediaQuoteAsync()
    {
        if (_busy || !_ready || MediaModel is not { } model || _project is null) return;
        if (!MediaInputsReady()) { Message("Choose the required sources and enter any required text before checking the price."); return; }
        var revision = _quote.BeginQuote();
        var project = _project;
        var owner = AccountSessionService.Current.UserId;
        var version = _loadVersion;
        try
        {
            var args = new JsonObject { ["model"] = model.Id, ["operation"] = _operation, ["prompt"] = MediaPrompt,
                ["settings"] = ReadMediaSettings(), ["inputs"] = new JsonArray(_mediaReferences.Select(r => (JsonNode)new JsonObject
                    { ["role"] = r.Role, ["mediaRef"] = r.MediaRef }).ToArray()) };
            SetBusy(true);
            Message("Checking price…");
            var result = await Task.Run(() => ToolExecutor.ExecuteGenerationForProject(project, "quote_media", args, owner));
            if (version != _loadVersion || !ContextMatches(project, owner) || revision != _quote.Revision) return;
            if (result.IsError) throw new InvalidOperationException(ResultText(result));
            var quote = JsonNode.Parse(result.Content.FirstOrDefault()?.Text ?? "{}");
            if (!DateTimeOffset.TryParse(quote?["expiresAt"]?.GetValue<string>(), out var expiry)
                || !_quote.Accept(revision, quote?["quoteId"]?.GetValue<string>() ?? "", quote?["credits"]?.GetValue<int>() ?? -1, expiry, DateTimeOffset.UtcNow))
                throw new InvalidOperationException("The price expired. Check it again.");
            var outputNote = quote?["outputNote"]?.GetValue<string>();
            Message((string.IsNullOrWhiteSpace(outputNote) ? "" : outputNote + " ")
                + "Credits are reserved on submit and charged on delivery." + (_mediaCatalog?.DevelopmentPrices == true ? " Development prices." : ""));
        }
        catch (Exception ex) { if (version == _loadVersion && ContextMatches(project, owner)) Message(ex.Message, true); }
        finally { if (version == _loadVersion) SetBusy(false); }
    }
}
