using System.Text.Json.Nodes;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Temno.App.Controls.Shared;
using Temno.App.Services;
using Temno.App.Theme;
using Temno.Core.Account;
using Temno.Core.Agent;
using Temno.Core.Agent.Skills;
using Temno.Core.Ui;
using Temno.Mcp;
using Windows.System;
using Windows.UI.Core;

namespace Temno.App.Controls;

public sealed partial class AgentPanelControl : UserControl
{
    public event EventHandler? CloseRequested;
    private void ClosePanel_Click(object sender, RoutedEventArgs e) => CloseRequested?.Invoke(this, EventArgs.Empty);

    public event EventHandler? ToolsMutated;
    public event EventHandler? OpenSettingsRequested;
    public event EventHandler? OpenAccountRequested;

    private readonly AgentChatController _chat = new();
    private int _sendGeneration;
    private bool _busy;
    private bool _sendingTool;
    private bool _chatTab = true;
    private bool _syncingHosted;
    private bool _hostedRenderDeferred;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _busyDelay;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _chatRefresh;
    private int _chatRefreshQueued;
    private bool _loaded;
    private const int BusyOverlayDelayMs = 200;

    public AgentPanelControl()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        HostedModelBox.DropDownClosed += (_, _) => OnHostedDropDownClosed();
        HostedEffortBox.DropDownClosed += (_, _) => OnHostedDropDownClosed();
        foreach (var box in new[] { HostedModelBox, HostedEffortBox })
        {
            box.Loaded += (_, _) => KeepDropDownInEditor(box);
            EventHandler<object> keepInPanel = (_, _) => KeepOpenListInPanel(box);
            box.DropDownOpened += (_, _) => { box.LayoutUpdated -= keepInPanel; box.LayoutUpdated += keepInPanel; };
            box.DropDownClosed += (_, _) => box.LayoutUpdated -= keepInPanel;
        }
        SizeChanged += (_, _) => FitHostedDropDowns();
        // WP-251: a disabled header button dims (SetActionEnabled); WinUI's grey disabled chip would
        // make it read as pressed.
        foreach (var action in new[] { NewChatButton, HistoryButton })
        {
            action.Resources["ButtonBackgroundDisabled"] = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            action.Resources["ButtonBorderBrushDisabled"] = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        }
        AppendToolsSystem(StartupBanner());
        // WP-400: exports, files outside the project, downloads and paid generations wait for this dialog.
        _chat.ToolConsent = new DialogConsent(this);
        ApplyTabChrome();
        ApplyChatCapability();
    }

    public bool IsChatConfigured => _chat.IsConfigured;

    /// <summary>WP-129: recipes prefill Chat, never Tools, and never send.</summary>
    public void ShowChatTab()
    {
        _chatTab = true;
        ChatErrorBar.IsOpen = false;
        ApplyTabChrome();
        ApplyChatCapability();
    }

    public void PrefillChat(string text)
    {
        ShowChatTab();
        InputBox.Text = text ?? "";
        InputBox.Focus(FocusState.Programmatic);
        var caret = InputBox.Text.Length;
        InputBox.Select(caret, 0);
    }

    public void FocusUnconfiguredChatCta()
    {
        ShowChatTab();
        OpenChatSettingsBtn.Focus(FocusState.Programmatic);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _loaded = true;
        _chat.Attach();
        _chat.SessionUpdated += OnChatSessionUpdated;
        _chat.ToolsMutated += OnChatToolsMutated;
        ApplyChatCapability();
        RenderChat();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _loaded = false;
        _chatRefresh?.Stop();
        Interlocked.Exchange(ref _chatRefreshQueued, 0);
        _chat.SessionUpdated -= OnChatSessionUpdated;
        _chat.ToolsMutated -= OnChatToolsMutated;
        _chat.Dispose();
    }

    private void OnChatSessionUpdated(object? sender, EventArgs e)
    {
        if (Interlocked.Exchange(ref _chatRefreshQueued, 1) != 0) return;
        if (!DispatcherQueue.TryEnqueue(() =>
        {
            if (!_loaded)
            {
                Interlocked.Exchange(ref _chatRefreshQueued, 0);
                return;
            }
            if (_chatRefresh is null)
            {
                _chatRefresh = DispatcherQueue.CreateTimer();
                _chatRefresh.Interval = TimeSpan.FromMilliseconds(100);
                _chatRefresh.IsRepeating = false;
                _chatRefresh.Tick += (_, _) =>
                {
                    Interlocked.Exchange(ref _chatRefreshQueued, 0);
                    if (!_loaded) return;
                    if (!_sendingTool && _busy != _chat.IsBusy) SetBusy(_chat.IsBusy);
                    if (_chat.TakeWithdrawnDraft() is { } withdrawn) RestoreDraft(withdrawn);
                    ApplyChatCapability();
                    RenderChat();
                };
            }
            _chatRefresh.Start();
        })) Interlocked.Exchange(ref _chatRefreshQueued, 0);
    }

    public void RefreshChatCapability()
    {
        _chat.ReloadCapability();
        ApplyChatCapability();
        RenderChat();
    }

    private void OnChatToolsMutated(object? sender, EventArgs e) =>
        DispatcherQueue.TryEnqueue(() => ToolsMutated?.Invoke(this, EventArgs.Empty));

    private void ChatTab_Click(object sender, RoutedEventArgs e)
    {
        _chatTab = true;
        ChatErrorBar.IsOpen = false;
        ApplyTabChrome();
        ApplyChatCapability();
    }

    private void ToolsTab_Click(object sender, RoutedEventArgs e)
    {
        if (!_sendingTool) SetBusy(_chat.IsBusy);
        _chatTab = false;
        ChatErrorBar.IsOpen = false;
        ApplyTabChrome();
    }

    private void ApplyTabChrome()
    {
        ChatPane.Visibility = _chatTab ? Visibility.Visible : Visibility.Collapsed;
        ToolsPane.Visibility = _chatTab ? Visibility.Collapsed : Visibility.Visible;
        ChatTabButton.Foreground = _chatTab ? AppTheme.Text.PrimaryBrush : AppTheme.Text.TertiaryBrush;
        ToolsTabButton.Foreground = _chatTab ? AppTheme.Text.TertiaryBrush : AppTheme.Text.PrimaryBrush;
        // WP-189: structural selected state — the accent bar under the tab plus the automation name.
        ChatTabIndicator.Visibility = _chatTab ? Visibility.Visible : Visibility.Collapsed;
        ToolsTabIndicator.Visibility = _chatTab ? Visibility.Collapsed : Visibility.Visible;
        AutomationProperties.SetName(ChatTabButton, _chatTab ? "Chat tab, selected" : "Chat tab");
        AutomationProperties.SetName(ToolsTabButton, _chatTab ? "Tools tab" : "Tools tab, selected");

        InputBox.PlaceholderText = _chatTab
            ? "Message the editor agent"
            : "Tool: get_timeline | undo | redo | help";
        ToolTipService.SetToolTip(
            InputBox,
            _chatTab ? "Message the editor agent" : "Tool command — not a chat model");
        AutomationProperties.SetName(InputBox, _chatTab ? "Chat message" : "Tools command");
        AutomationProperties.SetName(SendButton, _busy
            ? (_chatTab ? "Cancel chat" : "Cancel tool command")
            : (_chatTab ? "Send chat message" : "Send tool command"));

        ApplyComposerEnabled();
        UpdateEmptyChat();
        ApplyChatActions();
    }

    private void ApplyChatCapability()
    {
        var configured = _chat.IsConfigured;
        MissingKeyState.Visibility = configured ? Visibility.Collapsed : Visibility.Visible;
        ChatScroll.Visibility = configured ? Visibility.Visible : Visibility.Collapsed;
        if (!configured)
            ChatErrorBar.IsOpen = false;
        ApplyComposerEnabled();
        UpdateEmptyChat();
        RenderHostedBar();
        RenderPendingTurn();
        ApplyChatActions();
    }

    /// <summary>
    /// WP-251: New chat and Chat history. Both wait while a reply runs or a hosted turn is
    /// unresolved, because that turn is recovered or cancelled in its own chat.
    /// </summary>
    private void ApplyChatActions()
    {
        if (ChatActions is null)
            return;
        ChatActions.Visibility = _chatTab ? Visibility.Visible : Visibility.Collapsed;
        var ready = _chat.IsConfigured && !_busy;
        SetActionEnabled(NewChatButton, ready && _chat.CanStartNewChat);
        SetActionEnabled(HistoryButton, ready && _chat.CanSwitchChat);
    }

    // DESIGN §4: an unavailable control stays visible, greyed at Opacity.Strong.
    private static void SetActionEnabled(Button button, bool enabled)
    {
        button.IsEnabled = enabled;
        button.Opacity = enabled ? AppTheme.Opacity.Opaque : AppTheme.Opacity.Strong;
    }

    private void NewChat_Click(object sender, RoutedEventArgs e)
    {
        if (!_chat.NewChat())
            return;
        AfterChatMoved();
        InputBox.Focus(FocusState.Programmatic);
    }

    private void AfterChatMoved()
    {
        ChatErrorBar.IsOpen = false;
        ApplyChatCapability();
        RenderChat();
    }

    private Flyout? _historyFlyout;
    private Guid? _armedDelete;

    private void History_Click(object sender, RoutedEventArgs e)
    {
        _armedDelete = null;
        var body = new StackPanel { Spacing = AppTheme.Spacing.Xxs };
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
            // The app's one popover size, shared with the generation composer.
            Content = scroll,
            FlyoutPresenterStyle = new Style(typeof(FlyoutPresenter))
            {
                Setters =
                {
                    new Setter(PaddingProperty, AppTheme.Padding.SmMd),
                    new Setter(BackgroundProperty, AppTheme.Background.SurfaceBrush),
                    new Setter(BorderBrushProperty, AppTheme.Border.PrimaryBrush),
                    new Setter(CornerRadiusProperty, AppTheme.CornerRadius.Md),
                },
            },
        };
        flyout.Closed += (_, _) =>
        {
            _armedDelete = null;
            if (ReferenceEquals(_historyFlyout, flyout)) _historyFlyout = null;
        };
        _historyFlyout = flyout;
        FillHistory(body);
        WindowPopout.ShowFlyoutAt(
            flyout,
            HistoryButton,
            scroll,
            PopoutSide.Bottom,
            PopoutAlignment.Right,
            maxHeightCap: AppTheme.GenerationPanel.PopoverHeight,
            preferredWidth: AppTheme.GenerationPanel.PopoverWidth);
    }

    private void FillHistory(StackPanel body)
    {
        body.Children.Clear();
        body.Children.Add(new TextBlock
        {
            Text = "Chats in this project",
            Padding = AppTheme.Padding.SmMdXxs,
            FontSize = AppTheme.FontSize.Sm,
            Foreground = AppTheme.Text.TertiaryBrush,
        });
        Button? focus = null;
        var now = DateTimeOffset.Now;
        foreach (var entry in _chat.History())
            body.Children.Add(HistoryRow(body, entry, now, ref focus));
        focus?.Focus(FocusState.Programmatic);
    }

    private FrameworkElement HistoryRow(StackPanel body, ChatHistoryEntry entry, DateTimeOffset now, ref Button? focus)
    {
        var when = ChatSessionLibrary.RelativeTime(entry.UpdatedAt, now);
        var open = new Button
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Padding = AppTheme.Padding.SmMdSm,
            CornerRadius = AppTheme.CornerRadius.Sm,
            BorderThickness = new Thickness(0),
            // The open chat carries the R3 tonal selection, not only a heavier title.
            Background = entry.IsCurrent ? AppTheme.Accent.SelectedBrush : new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            Content = new StackPanel
            {
                Spacing = AppTheme.Spacing.Xxs,
                Children =
                {
                    new TextBlock
                    {
                        Text = entry.Title,
                        FontSize = AppTheme.FontSize.Md,
                        FontWeight = entry.IsCurrent ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal,
                        Foreground = AppTheme.Text.PrimaryBrush,
                        TextTrimming = TextTrimming.CharacterEllipsis,
                        MaxLines = 1,
                    },
                    new TextBlock
                    {
                        Text = when,
                        FontSize = AppTheme.FontSize.Sm,
                        Foreground = AppTheme.Text.TertiaryBrush,
                    },
                },
            },
        };
        // WinUI's hover and press states replace Background; the open chat keeps its tint under the pointer.
        if (entry.IsCurrent)
            KeepBackground(open, AppTheme.Accent.SelectedBrush);
        AutomationProperties.SetName(open, entry.IsCurrent ? $"{entry.Title}, {when}, open now" : $"{entry.Title}, {when}");
        ToolTipService.SetToolTip(open, entry.IsCurrent ? "This chat is open" : "Open this chat");
        open.Click += (_, _) =>
        {
            _historyFlyout?.Hide();
            if (!entry.IsCurrent && _chat.OpenChat(entry.Id))
                AfterChatMoved();
        };

        var row = new Grid { ColumnSpacing = AppTheme.Spacing.Xxs };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Children.Add(open);
        if (!entry.CanDelete)
            return row;

        // Deleting removes the saved file, so it takes a second click on the same row.
        var armed = _armedDelete == entry.Id;
        var delete = new Button
        {
            VerticalAlignment = VerticalAlignment.Center,
            Padding = armed ? AppTheme.Padding.SmXxs : AppTheme.Padding.Xs,
            CornerRadius = AppTheme.CornerRadius.XsSm,
            BorderThickness = new Thickness(0),
            Background = armed ? AppTheme.Status.ErrorBrush : new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            Content = armed
                ? new TextBlock { Text = "Delete", FontSize = AppTheme.FontSize.Sm, Foreground = AppTheme.Text.PrimaryBrush }
                : new FluentIcon
                {
                    Icon = Icons.Delete,
                    Width = AppTheme.IconSize.Xs,
                    Height = AppTheme.IconSize.Xs,
                    Fill = AppTheme.Text.TertiaryBrush,
                },
        };
        // The confirm step appears under the pointer, so it must stay red while hovered and pressed.
        if (armed)
            KeepBackground(delete, AppTheme.Status.ErrorBrush);
        Grid.SetColumn(delete, 1);
        AutomationProperties.SetName(delete, armed ? $"Confirm delete chat {entry.Title}" : $"Delete chat {entry.Title}");
        ToolTipService.SetToolTip(delete, armed ? "Click again to delete this chat for good" : "Delete from history");
        delete.Click += (_, _) =>
        {
            if (armed)
            {
                DeleteFromHistory(entry);
            }
            else
            {
                _armedDelete = entry.Id;
            }
            FillHistory(body);
        };
        if (armed)
            focus = delete;
        row.Children.Add(delete);
        return row;
    }

    private static void KeepBackground(Button button, Brush brush)
    {
        button.Resources["ButtonBackgroundPointerOver"] = brush;
        button.Resources["ButtonBackgroundPressed"] = brush;
    }

    private void DeleteFromHistory(ChatHistoryEntry entry)
    {
        _armedDelete = null;
        try
        {
            _chat.DeleteChat(entry.Id);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ChatErrorBar.Title = "Could not delete the chat";
            ChatErrorBar.Message = "Its saved file is in use or read-only. Close anything using the project folder and try again.";
            ChatErrorBar.IsOpen = true;
        }
    }

    private void ApplyComposerEnabled()
    {
        if (_busy)
        {
            InputBox.IsEnabled = false;
            SendButton.IsEnabled = true;
            return;
        }

        var chatReady = !_chatTab || _chat.IsConfigured;
        InputBox.IsEnabled = chatReady;
        // WP-400: a message this PC did not send is resolved with Run or Discard first; the draft can still be typed.
        SendButton.IsEnabled = chatReady && !(_chatTab && _chat.HasForeignPendingTurn);
    }

    private void OpenSettings_Click(object sender, RoutedEventArgs e)
        => OpenSettingsRequested?.Invoke(this, EventArgs.Empty);

    private void OpenAccount_Click(object sender, RoutedEventArgs e)
        => OpenAccountRequested?.Invoke(this, EventArgs.Empty);

    private void FocusComposer_Click(object sender, RoutedEventArgs e)
        => InputBox.Focus(FocusState.Programmatic);

    /// <summary>WP-189: the Connect card's second path. The Tools console needs no model.</summary>
    private void UseTools_Click(object sender, RoutedEventArgs e)
    {
        _chatTab = false;
        ChatErrorBar.IsOpen = false;
        ApplyTabChrome();
        InputBox.Focus(FocusState.Programmatic);
    }

    /// <summary>WP-189: suggestion chips prefill the composer and never send (WP-129 rule).</summary>
    private void SuggestionChip_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string text } && !string.IsNullOrWhiteSpace(text))
            PrefillChat(text);
    }

    private static string StartupBanner()
    {
        if (App.McpHttpRunning && App.McpEndpoint is not null)
            return $"Tools CLI ready. Same tools as HTTP MCP at {App.McpEndpoint} — not a chat model. Connect Cursor, Claude, Codex, Grok CLI, or Gemini CLI (Help → MCP Instructions).\nTry: help · get_timeline · get_projects · undo · redo";

        if (!App.McpPreferenceEnabled)
            return "Tools CLI ready. HTTP MCP is off (Settings → MCP Server). In-process tools still work.\nTry: help · get_timeline · get_projects · undo · redo";

        if (App.McpStartError is { } err)
            return $"Tools CLI ready. In-process MCP tools for this editor. HTTP {McpDefaults.Host}:{McpDefaults.Port} failed to bind ({err}).\nTry: help · get_timeline · get_projects · undo · redo";

        return "Tools CLI ready. In-process MCP tools for this editor — not a chat model.\nTry: help · get_timeline · get_projects · undo · redo";
    }

    private void Send_Click(object sender, RoutedEventArgs e)
    {
        if (_busy)
            CancelInFlight();
        else if (_chatTab)
            SendChat();
        else
            SendTool();
    }

    // WP-257: Preview, because a multi-line TextBox consumes Enter before KeyDown. Shift+Enter falls through to a new line.
    private void InputBox_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter || InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift)
                .HasFlag(CoreVirtualKeyStates.Down))
            return;
        e.Handled = true;
        if (_busy)
            return;
        if (_chatTab)
            SendChat();
        else
            SendTool();
    }

    private void CancelInFlight()
    {
        _sendGeneration++;
        _sendingTool = false;
        _chat.Cancel();
        SetBusy(_chatTab && _chat.IsBusy);
        if (_chatTab)
            ShowChatError("Cancelled.");
        else
            AppendToolsAssistant("Cancelled.", error: true);
    }

    // WP-257: the multi-line TextBox reports line breaks as \r; the chat, the estimate and the Tools parser see \n.
    private string Draft => (InputBox.Text ?? "").ReplaceLineEndings("\n");

    private async void SendChat()
    {
        var text = Draft.Trim();
        if (string.IsNullOrEmpty(text) || _busy)
            return;

        ChatErrorBar.IsOpen = false;
        if (!_chat.IsConfigured)
            return;
        if (_chat.HasForeignPendingTurn)
        {
            ShowChatError("Run or discard the message above before you send another.");
            return;
        }

        // Sending while a turn is pending reconnects it. Keep a new draft for its own later turn.
        var beforeSend = _chat.Session;
        var resuming = _chat.HasPendingHostedTurn;
        if (!resuming) InputBox.Text = "";
        var generation = ++_sendGeneration;
        SetBusy(true);
        try
        {
            var hold = HostedAgentConversation.AutomaticHold;
            while (true)
            {
                try
                {
                    await _chat.SendAsync(text, hold).ConfigureAwait(true);
                    break;
                }
                catch (HostedApiException ex) when (!resuming && hold == HostedAgentConversation.AutomaticHold
                    && ex.Code == "hold_confirmation_required" && ex.SuggestedHoldCredits is { } suggested)
                {
                    // WP-254: nothing was held. Ask once; "Continue" resends the same text with the hold named.
                    // Only for this text: a resumed older message comes back to the composer instead (below).
                    // "Don't send" needs no error: the finally returns the text to the composer.
                    if (generation != _sendGeneration)
                        return;
                    RenderChat();
                    if (!await ConfirmHoldAsync(ex.Message).ConfigureAwait(true) || generation != _sendGeneration
                        || !_loaded || _chat.Session.Id != beforeSend.Id)
                        return;
                    SetBusy(true);
                    hold = suggested;
                }
            }
            if (generation != _sendGeneration)
                return;
            ApplyChatCapability();
            RenderChat();
        }
        catch (AnthropicClientException ex) when (ex.Kind == AnthropicClientErrorKind.MissingApiKey)
        {
            if (generation != _sendGeneration)
                return;
            ApplyChatCapability();
        }
        catch (HostedApiException ex)
        {
            if (generation != _sendGeneration)
                return;
            RenderChat();
            if (resuming && ex.WithdrawnText is { Length: > 0 } withdrawn)
            {
                // The resumed message was never admitted and left the chat; do not lose it.
                RestoreDraft(withdrawn);
                ShowChatError("Your earlier message was not sent and is back in the composer. " + ex.Message);
            }
            else
                ShowChatError(ex.Message);
        }
        catch (OperationCanceledException)
        {
            if (generation != _sendGeneration)
                return;
        }
        catch (Exception ex)
        {
            if (generation != _sendGeneration)
                return;
            RenderChat();
            ShowChatError(ex.Message);
        }
        finally
        {
            if (generation == _sendGeneration)
            {
                var afterSend = _chat.Session;
                if (string.IsNullOrEmpty(InputBox.Text) && (afterSend.Id != beforeSend.Id
                    || afterSend.Messages.Count == beforeSend.Messages.Count))
                    InputBox.Text = text;
                SetBusy(_chat.IsBusy);
            }
            if (_chat.UsesHosted)
                RefreshBalanceAfterTurn();
        }
    }

    // WP-400: the card for a pending hosted turn this PC did not send. It shows what Run would send, and how.
    private void RenderPendingTurn()
    {
        if (PendingTurnBar is null)
            return;
        var pending = _chatTab ? _chat.ForeignPendingTurn() : null;
        PendingTurnBar.IsOpen = pending is not null;
        if (pending is null)
            return;
        PendingTurnBar.Message = $"{pending.Model} · {pending.Effort} · {pending.Hold}";
        PendingTurnMessage.Text = pending.Message.Length > 0 ? "“" + pending.Message + "”" : "It holds no message text.";
        var note = string.Join(" ", new[] { pending.Note, pending.RunBlocked }.Where(s => !string.IsNullOrEmpty(s)));
        PendingTurnNote.Text = note;
        PendingTurnNote.Visibility = note.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        RunPendingButton.IsEnabled = pending.CanRun && !_busy;
        DiscardPendingButton.IsEnabled = !_busy;
    }

    private async void RunPending_Click(object sender, RoutedEventArgs e)
    {
        if (_busy)
            return;
        ChatErrorBar.IsOpen = false;
        var generation = ++_sendGeneration;
        var beforeRun = _chat.Session;
        SetBusy(true);
        try
        {
            try
            {
                await _chat.RunPendingAsync().ConfigureAwait(true);
            }
            catch (HostedApiException ex) when (ex.Code == "hold_confirmation_required"
                && ex.SuggestedHoldCredits is { } suggested && ex.WithdrawnText is { Length: > 0 } withdrawn)
            {
                // Run quotes as a new message does (WP-254): nothing was held. Ask once; Continue sends that text with
                // the hold named, and Don't send leaves it in the composer.
                if (generation != _sendGeneration)
                    return;
                RenderChat();
                if (!await ConfirmHoldAsync(ex.Message).ConfigureAwait(true) || generation != _sendGeneration
                    || !_loaded || _chat.Session.Id != beforeRun.Id)
                {
                    if (generation == _sendGeneration && _loaded)
                        RestoreDraft(withdrawn);
                    return;
                }
                SetBusy(true);
                await _chat.SendAsync(withdrawn, suggested).ConfigureAwait(true);
            }
            if (generation != _sendGeneration)
                return;
            ApplyChatCapability();
            RenderChat();
        }
        catch (HostedApiException ex)
        {
            if (generation != _sendGeneration)
                return;
            RenderChat();
            if (ex.WithdrawnText is { Length: > 0 } withdrawn)
            {
                RestoreDraft(withdrawn);
                ShowChatError("The message was not sent and is back in the composer. " + ex.Message);
            }
            else
                ShowChatError(ex.Message);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (generation != _sendGeneration)
                return;
            RenderChat();
            ShowChatError(ex.Message);
        }
        finally
        {
            if (generation == _sendGeneration)
            {
                SetBusy(_chat.IsBusy);
                ApplyChatCapability();
            }
            if (_chat.UsesHosted)
                RefreshBalanceAfterTurn();
        }
    }

    private async void DiscardPending_Click(object sender, RoutedEventArgs e)
    {
        if (_busy)
            return;
        ChatErrorBar.IsOpen = false;
        var generation = ++_sendGeneration;
        SetBusy(true);
        try
        {
            await _chat.DiscardPendingAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            if (generation == _sendGeneration)
                ShowChatError(ex.Message);
        }
        finally
        {
            if (generation == _sendGeneration)
                SetBusy(_chat.IsBusy);
            ApplyChatCapability();
            RenderChat();
        }
    }

    /// <summary>
    /// WP-400: asks on the UI thread, once per call. The answer is an approval for exactly that request; closing,
    /// Don't allow, a timeout, Cancel or a project switch all answer no. A dialog that cannot open is a no too.
    /// </summary>
    private sealed class DialogConsent(AgentPanelControl panel) : IAgentToolConsent
    {
        public async Task<AgentToolApproval?> AskAsync(AgentToolConsentRequest request, CancellationToken cancellationToken)
        {
            var answer = new TaskCompletionSource<AgentToolApproval?>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!panel.DispatcherQueue.TryEnqueue(async () =>
                {
                    try { answer.TrySetResult(await panel.AskConsentAsync(request, cancellationToken).ConfigureAwait(true)); }
                    catch (Exception ex) { answer.TrySetException(ex); }
                }))
                throw new InvalidOperationException("the agent panel is closed");
            using (cancellationToken.Register(() => answer.TrySetCanceled(cancellationToken)))
                return await answer.Task.ConfigureAwait(false);
        }
    }

    private async Task<AgentToolApproval?> AskConsentAsync(AgentToolConsentRequest request, CancellationToken cancellationToken)
    {
        if (!_loaded || XamlRoot is null)
            throw new InvalidOperationException("the agent panel is closed");
        cancellationToken.ThrowIfCancellationRequested();
        var dialog = AppDialog.Create(XamlRoot);
        dialog.Title = request.Title;
        dialog.Content = ConsentContent(request);
        dialog.PrimaryButtonText = request.Action;
        dialog.CloseButtonText = "Don't allow";
        dialog.DefaultButton = ContentDialogButton.Close;
        using var hide = cancellationToken.Register(() => DispatcherQueue.TryEnqueue(dialog.Hide));
        var result = await dialog.ShowAsync();
        return result == ContentDialogResult.Primary && !cancellationToken.IsCancellationRequested
            ? new AgentToolApproval(request.Id) : null;
    }

    private static StackPanel ConsentContent(AgentToolConsentRequest request)
    {
        var body = new StackPanel { Spacing = AppTheme.Spacing.Md, MaxWidth = AppTheme.GenerationPanel.PopoverWidth };
        body.Children.Add(new TextBlock
        {
            Text = "The agent asked to do this. It runs only if you allow it, and only this once.",
            FontSize = AppTheme.FontSize.SmMd,
            Foreground = AppTheme.Text.SecondaryBrush,
            TextWrapping = TextWrapping.WrapWholeWords,
        });
        foreach (var (label, value) in request.Details)
        {
            body.Children.Add(new StackPanel
            {
                Spacing = AppTheme.Spacing.Xxs,
                Children =
                {
                    new TextBlock { Text = label, FontSize = AppTheme.FontSize.Sm, Foreground = AppTheme.Text.TertiaryBrush },
                    new TextBlock
                    {
                        Text = value,
                        FontSize = AppTheme.FontSize.SmMd,
                        Foreground = AppTheme.Text.PrimaryBrush,
                        TextWrapping = TextWrapping.Wrap,
                        IsTextSelectionEnabled = true,
                    },
                },
            });
        }
        if (request.Note is { Length: > 0 } note)
            body.Children.Add(new TextBlock
            {
                Text = note,
                FontSize = AppTheme.FontSize.Sm,
                Foreground = AppTheme.Text.TertiaryBrush,
                TextWrapping = TextWrapping.WrapWholeWords,
            });
        return body;
    }

    // Puts withdrawn text back ahead of anything typed since, on its own line.
    private void RestoreDraft(string withdrawn)
    {
        var current = Draft.Trim();
        if (current.Contains(withdrawn, StringComparison.Ordinal))
            return;
        InputBox.Text = current.Length == 0 ? withdrawn : withdrawn + "\n" + current;
    }

    // The hold is released or settled when a hosted turn ends; show the balance that results.
    private async void RefreshBalanceAfterTurn()
    {
        try { await AccountSessionService.Current.RefreshBalanceAsync().ConfigureAwait(true); }
        catch (Exception) { /* The account control reports balance errors; this is a courtesy refresh. */ }
        UpdateHostedEstimate();
    }

    private async Task<bool> ConfirmHoldAsync(string message)
    {
        if (XamlRoot is null)
            return false;
        var dialog = AppDialog.Create(XamlRoot);
        dialog.Title = "Hold more credits?";
        dialog.Content = message;
        dialog.PrimaryButtonText = "Continue";
        dialog.CloseButtonText = "Don't send";
        dialog.DefaultButton = ContentDialogButton.Close;
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    private async void SendTool()
    {
        var text = Draft.Trim();
        if (string.IsNullOrEmpty(text) || _busy) return;
        InputBox.Text = "";
        AppendToolsUser(text);

        string tool;
        JsonObject args;
        try
        {
            (tool, args) = ParseCommand(text);
        }
        catch (Exception ex)
        {
            AppendToolsAssistant(ex.Message, error: true);
            ShowChatError(ex.Message);
            return;
        }
        if (tool == "help")
        {
            AppendToolsAssistant(
                "Commands (tool dispatcher, not an LLM):\n" +
                "  help\n" +
                "  get_timeline\n" +
                "  get_projects\n" +
                "  undo / redo\n" +
                "  tool <name> {json-args}\n" +
                "Any bare tool name with no args is invoked empty.\n" +
                "Cancel stops waiting for the current tool; work already started may finish in the background.");
            return;
        }

        var generation = ++_sendGeneration;
        _sendingTool = true;
        SetBusy(true);
        try
        {
            var result = await Task.Run(() => ToolExecutor.Execute(tool, args)).ConfigureAwait(true);
            if (generation != _sendGeneration)
                return;
            var body = string.Join("\n", result.Content.Select(c => c.Text));
            if (result.IsError)
            {
                AppendToolsAssistant("Error:\n" + body, error: true);
                ShowChatError(body);
            }
            else
                AppendToolsAssistant(body);

            if (IsMutationTool(tool))
                ToolsMutated?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            if (generation != _sendGeneration)
                return;
            AppendToolsAssistant(ex.Message, error: true);
            ShowChatError(ex.Message);
        }
        finally
        {
            if (generation == _sendGeneration)
            {
                _sendingTool = false;
                SetBusy(_chat.IsBusy);
            }
        }
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        SendButton.Content = busy ? "Cancel" : "Send";
        ApplyTabChrome();
        ApplyComposerEnabled();
        ToolTipService.SetToolTip(
            SendButton,
            busy ? "Stop waiting for this request" : (_chatTab ? "Send chat message" : "Send tool command"));
        if (busy)
            ArmBusyOverlay();
        else
            HideBusyOverlay();
        UpdateEmptyChat();
        RenderPendingTurn();
    }

    private void ArmBusyOverlay()
    {
        _busyDelay ??= DispatcherQueue.CreateTimer();
        _busyDelay.Interval = TimeSpan.FromMilliseconds(BusyOverlayDelayMs);
        _busyDelay.IsRepeating = false;
        _busyDelay.Tick -= OnBusyDelayTick;
        _busyDelay.Tick += OnBusyDelayTick;
        _busyDelay.Start();
    }

    private void OnBusyDelayTick(Microsoft.UI.Dispatching.DispatcherQueueTimer sender, object args)
    {
        if (!_busy)
            return;
        ChatBusyLabel.Text = _chatTab ? "Waiting…" : "Running tool…";
        ChatBusyOverlay.Visibility = Visibility.Visible;
        ChatBusyRing.IsActive = true;
    }

    private void HideBusyOverlay()
    {
        _busyDelay?.Stop();
        ChatBusyOverlay.Visibility = Visibility.Collapsed;
        ChatBusyRing.IsActive = false;
    }

    private void UpdateEmptyChat()
    {
        if (EmptyChatState is null)
            return;
        var show = _chatTab && _chat.IsConfigured && !_busy && ChatHost.Children.Count == 0;
        EmptyChatState.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
    }

    private static bool IsMutationTool(string name)
    {
        return name is not (
            "get_timeline" or "get_projects" or "get_project" or "list_tools" or
            "inspect_timeline" or "help");
    }

    private static (string Tool, JsonObject Args) ParseCommand(string text)
    {
        if (text.Equals("undo", StringComparison.OrdinalIgnoreCase))
            return ("undo", []);
        if (text.Equals("redo", StringComparison.OrdinalIgnoreCase))
            return ("redo", []);

        if (text.StartsWith("tool ", StringComparison.OrdinalIgnoreCase))
        {
            var rest = text[5..].Trim();
            var space = rest.IndexOfAny([' ', '\t', '\n']);
            if (space < 0) return (rest, []);
            var name = rest[..space];
            var json = rest[(space + 1)..].Trim();
            var node = string.IsNullOrEmpty(json) ? [] : JsonNode.Parse(json) as JsonObject ?? [];
            return (name, node);
        }

        var brace = text.IndexOf('{');
        if (brace > 0)
        {
            var name = text[..brace].Trim();
            var json = text[brace..];
            var node = JsonNode.Parse(json) as JsonObject ?? [];
            return (name, node);
        }

        return (text.Split(' ', 2)[0], []);
    }

    private void RenderChat()
    {
        ChatHost.Children.Clear();
        var snapshot = _chat.Session;
        var skillReads = new HashSet<string>(StringComparer.Ordinal);
        foreach (var message in snapshot.Messages)
        {
            if (message.Role == AgentMessageRole.system)
                continue;
            foreach (var block in message.Blocks)
            {
                if (block.Kind == AgentContentBlock.KindText && !string.IsNullOrEmpty(block.Text))
                {
                    AppendChatBubble(
                        ChatHost,
                        block.Text,
                        user: message.Role == AgentMessageRole.user,
                        error: false,
                        receipt: false);
                }
                else if (block.Kind == AgentContentBlock.KindToolUse)
                {
                    // WP-309: a skill the agent reads is named, so the user sees which playbook it follows.
                    var skill = block.Name == SkillIndex.Tool ? SkillId(block.Input) : null;
                    if (skill is not null && block.Id is { } useId)
                        skillReads.Add(useId);
                    AppendChatBubble(
                        ChatHost,
                        skill is not null ? "Reading skill " + skill : "tool " + (block.Name ?? ""),
                        user: false,
                        error: false,
                        receipt: true);
                }
                else if (block.Kind == AgentContentBlock.KindToolResult)
                {
                    var text = block.Content?.FirstOrDefault(c => c.Kind == AgentToolResultBlock.KindText)?.Text ?? "";
                    if (block.IsError != true && block.ToolUseId is { } resultId && skillReads.Contains(resultId))
                        text = "skill loaded";
                    if (text.Length > 280)
                        text = text[..280] + "…";
                    AppendChatBubble(
                        ChatHost,
                        (block.IsError == true ? "tool error: " : "tool result: ") + text,
                        user: false,
                        error: block.IsError == true,
                        receipt: true);
                }
            }
        }

        _ = DispatcherQueue.TryEnqueue(() => ChatScroll.ChangeView(null, ChatScroll.ScrollableHeight, null));
        UpdateEmptyChat();
    }

    private static string? SkillId(string? input)
    {
        try
        {
            return JsonNode.Parse(string.IsNullOrWhiteSpace(input) ? "{}" : input)?["id"] is JsonValue value
                && value.TryGetValue<string>(out var id) && SkillDocument.IsValidName(id) ? id : null;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    private void ShowChatError(string text)
    {
        if (string.Equals(text, "Cancelled.", StringComparison.Ordinal))
        {
            ChatErrorBar.IsOpen = false;
            return;
        }

        ChatErrorBar.Title = "Request failed";
        var cause = text.Length > 280 ? text[..280] + "…" : text;
        // A hosted cause is already the user's message (HostedApiErrors); the old suffix was a developer note (WP-254 Q11).
        ChatErrorBar.Message = _chatTab
            ? cause + (_chat.UsesHosted ? "" : " Open Settings → AI Chat to verify the key and endpoint.")
            : cause + " Try help, or check the tool name and JSON arguments.";
        ChatErrorBar.IsOpen = true;
    }

    private void AppendToolsUser(string text) => AppendChatBubble(ToolsHost, text, user: true, error: false, receipt: false);
    private void AppendToolsAssistant(string text, bool error = false) =>
        AppendChatBubble(ToolsHost, text, user: false, error: error, receipt: false);
    private void AppendToolsSystem(string text) =>
        AppendChatBubble(ToolsHost, text, user: false, error: false, receipt: false);

    private void AppendChatBubble(Panel host, string text, bool user, bool error, bool receipt)
    {
        var bg = error
            ? AppTheme.Status.ErrorBrush
            : user
                ? AppTheme.Background.ProminentBrush
                : AppTheme.Background.RaisedBrush;

        var foreground = error
            ? AppTheme.Text.PrimaryBrush
            : receipt
                ? AppTheme.Text.TertiaryBrush
                : AppTheme.Text.SecondaryBrush;

        var border = new Border
        {
            Background = bg,
            CornerRadius = new CornerRadius(AppTheme.Radius.Sm),
            Padding = AppTheme.Padding.MdSmMd,
            HorizontalAlignment = user ? HorizontalAlignment.Right : HorizontalAlignment.Left,
            MaxWidth = 320,
            Child = new TextBlock
            {
                Text = text,
                TextWrapping = TextWrapping.WrapWholeWords,
                FontSize = receipt ? AppTheme.FontSize.Sm : AppTheme.FontSize.SmMd,
                Foreground = foreground,
                FontFamily = new FontFamily(receipt || !user ? "Consolas" : "Segoe UI"),
                IsTextSelectionEnabled = true,
            },
        };
        host.Children.Add(border);
        if (ReferenceEquals(host, ToolsHost))
            _ = DispatcherQueue.TryEnqueue(() => ToolsScroll.ChangeView(null, ToolsScroll.ScrollableHeight, null));
    }

    private void RenderHostedBar()
    {
        if (HostedChatBar is null)
            return;
        // WP-249: an open list is mid-interaction. Clearing its items or moving its selection then —
        // a session update landing while the user holds it open — fails WinUI fast with 0x8000FFFF.
        // Skip the render and run it once the list closes.
        if (HostedModelBox.IsDropDownOpen || HostedEffortBox.IsDropDownOpen)
        {
            _hostedRenderDeferred = true;
            return;
        }

        var show = _chatTab && _chat.UsesHosted && _chat.IsConfigured;
        HostedChatBar.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (!show)
            return;

        var editable = !_chat.IsBusy && !_chat.HasPendingHostedTurn;
        HostedModelBox.IsEnabled = editable;
        HostedEffortBox.IsEnabled = editable;

        _syncingHosted = true;
        try
        {
            var profile = _chat.SelectedProfile();
            // Each creator's name heads its models; a header row is never selectable.
            var models = new List<HostedPickerEntry>();
            foreach (var (creator, group) in HostedModelNames.Grouped(_chat.Catalog?.Models ?? []))
            {
                models.Add(new(null, creator, Header: true));
                models.AddRange(group.Select(model => new HostedPickerEntry(model.Id, HostedModelNames.Label(model))));
            }
            SyncHostedItems(HostedModelBox, models, profile?.Id, StringComparison.Ordinal);

            if (profile is not null)
            {
                var efforts = profile.Efforts
                    .Select(effort => new HostedPickerEntry(effort, AgentChatController.EffortLabel(effort)))
                    .ToList();
                // A saved effort this model lacks (Kimi has no Medium) shows the one Send will use instead; a new
                // project's first chat shows the Settings effort, else High.
                SyncHostedItems(HostedEffortBox, efforts, _chat.EffortFor(profile), StringComparison.OrdinalIgnoreCase);

                HostedReasoningSwitch.IsOn = profile.OffEfforts.Length == 0
                    || (_chat.Session.Hosted?.ReasoningEnabled ?? true);
                HostedReasoningSwitch.IsEnabled = editable && profile.OffEfforts.Length > 0;
            }
            // A switch locked On says nothing. Show it only for a model whose OpenRouter listing lets reasoning turn off.
            HostedReasoningRow.Visibility = profile is { OffEfforts.Length: > 0 } ? Visibility.Visible : Visibility.Collapsed;

            UpdateHostedEstimate();
            HostedChargeText.Text = !string.IsNullOrWhiteSpace(_chat.HostedStatus)
                ? _chat.HostedStatus
                : _chat.LastChargeCredits is { } charge
                    ? $"Last settled charge {charge} credits."
                    : "Unused credits return when a request finishes. Cancel can still charge credits already used.";
        }
        finally
        {
            _syncingHosted = false;
        }
    }

    // WP-254: no cap to type. The composer shows the likely charge for the draft and the balance; the quote
    // then states what it holds, and settlement what it returned.
    private void UpdateHostedEstimate()
    {
        if (HostedChatBar is null || HostedChatBar.Visibility != Visibility.Visible)
            return;
        if (!string.IsNullOrWhiteSpace(_chat.CatalogError))
        {
            HostedEstimateText.Text = _chat.CatalogError;
            return;
        }
        if (_chat.Catalog is { } catalog && catalog.BillingPolicy?.EstimatedHolds != true)
        {
            // Passive: nothing has been attempted. Send fetches the catalog again before refusing.
            HostedEstimateText.Text = "Hosted chat is unavailable until the service update finishes.";
            return;
        }
        var available = AccountSessionService.Current.Balance is { } credits
            ? $"{HostedApiErrors.Credits(credits.Available)} credits available."
            : AccountSessionService.Current.BalanceError ?? "Balance not loaded yet.";
        var draft = Draft;
        HostedEstimateText.Text = !string.IsNullOrWhiteSpace(draft) && _chat.DraftEstimate(draft) is { } estimate
            ? $"Est. {HostedEstimate.Range(estimate.Low, estimate.High)} credits. {available}"
            : available;
    }

    private void InputBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_chatTab)
            UpdateHostedEstimate();
    }

    private void HostedModel_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingHosted)
            return;
        // A creator header carries no model. Should one ever be selected, put the chat's model back.
        if (HostedModelBox.SelectedItem is ComboBoxItem { Tag: null })
        {
            DispatcherQueue.TryEnqueue(RenderHostedBar);
            return;
        }
        CommitHostedChoices();
    }

    private void HostedEffort_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingHosted)
            return;
        CommitHostedChoices();
    }

    private void HostedReasoning_Toggled(object sender, RoutedEventArgs e)
    {
        if (_syncingHosted)
            return;
        CommitHostedChoices();
    }

    private void CommitHostedChoices()
    {
        var model = (HostedModelBox.SelectedItem as ComboBoxItem)?.Tag as string;
        var effort = (HostedEffortBox.SelectedItem as ComboBoxItem)?.Tag as string;
        _chat.ApplyHostedChoices(model, effort, HostedReasoningSwitch.IsOn);
        // WP-249: this runs inside a ComboBox's own SelectionChanged. Re-rendering here rebuilt that
        // ComboBox's items from inside the event and crashed the app (0x8000FFFF, reproduced by
        // picking a model or an effort). Render after the event has finished.
        DispatcherQueue.TryEnqueue(RenderHostedBar);
    }

    private void OnHostedDropDownClosed()
    {
        if (!_hostedRenderDeferred)
            return;
        _hostedRenderDeferred = false;
        DispatcherQueue.TryEnqueue(RenderHostedBar);
    }

    /// <summary>
    /// WP-249. Brings <paramref name="box"/> to exactly <paramref name="entries"/> and the entry
    /// tagged <paramref name="selectedTag"/>, touching it only where it differs. The bar used to
    /// clear and refill both lists on every render — each session update, and each choice from
    /// inside its own SelectionChanged — replacing the very item being selected.
    /// </summary>
    private static void SyncHostedItems(
        ComboBox box, IReadOnlyList<HostedPickerEntry> entries, string? selectedTag, StringComparison comparison)
    {
        var same = box.Items.Count == entries.Count;
        for (var i = 0; same && i < entries.Count; i++)
        {
            same = box.Items[i] is ComboBoxItem item
                   && string.Equals(item.Tag as string, entries[i].Tag, StringComparison.Ordinal)
                   && string.Equals(item.Tag is null ? AutomationProperties.GetName(item) : item.Content as string, entries[i].Label, StringComparison.Ordinal)
                   && (item.Tag is null) == entries[i].Header;
        }

        if (!same)
        {
            box.Items.Clear();
            foreach (var entry in entries)
                box.Items.Add(entry.Header ? HostedPickerHeader(entry.Label, box.Items.Count == 0) : new ComboBoxItem { Content = entry.Label, Tag = entry.Tag });
        }

        ComboBoxItem? selected = null;
        foreach (ComboBoxItem item in box.Items)
        {
            if (item.Tag is string && string.Equals(item.Tag as string, selectedTag, comparison))
            {
                selected = item;
                break;
            }
        }

        if (!ReferenceEquals(box.SelectedItem, selected))
            box.SelectedItem = selected;
    }

    // A creator's name over its models, set as a section label rather than a row: semibold caption text, a divider
    // above every group but the first, and a short row whose label sits close to the models it names (owner,
    // 2026-11-06: a plain grey row read like a model). Disabled, so the pointer and the keyboard pass over it; the
    // text sets its own colour so it reads as a label, not a dimmed item. Its sides keep the item padding, so the
    // label lines up with the model names.
    private static ComboBoxItem HostedPickerHeader(string creator, bool first)
    {
        var sides = Application.Current.Resources.TryGetValue("ComboBoxItemThemePadding", out var value) && value is Thickness item
            ? item : new Thickness(AppTheme.Spacing.Md, AppTheme.Spacing.None, AppTheme.Spacing.Md, AppTheme.Spacing.None);
        var label = new StackPanel { Spacing = AppTheme.Spacing.SmMd };
        if (!first)
            // Border.Divider matches the list's own surface; the muted decorative tone shows on it.
            label.Children.Add(new Border { Height = AppTheme.BorderWidth.Thin, Background = AppTheme.Text.MutedBrush });
        label.Children.Add(new TextBlock
        {
            Text = creator,
            FontSize = AppTheme.FontSize.Sm,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = AppTheme.Text.TertiaryBrush,
        });
        var header = new ComboBoxItem
        {
            IsEnabled = false,
            IsTabStop = false,
            MinHeight = 0,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(sides.Left, first ? AppTheme.Spacing.Xs : AppTheme.Spacing.SmMd, sides.Right, AppTheme.Spacing.Xxs),
            Content = label,
        };
        AutomationProperties.SetName(header, creator);
        AutomationProperties.SetHeadingLevel(header, Microsoft.UI.Xaml.Automation.Peers.AutomationHeadingLevel.Level2);
        return header;
    }

    private sealed record HostedPickerEntry(string? Tag, string Label, bool Header = false);

    // The model list has grown to six creators and eleven models. WinUI opened it as its own window and placed the
    // selected row over the picker, so the list overhung the Chat panel and could run past the editor window. It now
    // renders inside the window, is no taller than this panel (it scrolls when the panel is shorter), and once WinUI
    // has placed it, it slides back inside the panel if it pokes out above or below.
    private static void KeepDropDownInEditor(ComboBox box)
    {
        // The hosted bar starts collapsed, so its pickers have no template yet when they load.
        box.ApplyTemplate();
        if (box.IsDropDownOpen || FindPopup(box) is not { } popup || popup.ShouldConstrainToRootBounds)
            return;
        popup.ShouldConstrainToRootBounds = true;
    }

    private void FitHostedDropDowns()
    {
        if (ActualHeight <= 0)
            return;
        var height = Math.Max(AppTheme.AgentPanel.DropDownMinHeight, ActualHeight - 2 * AppTheme.Spacing.Md);
        foreach (var box in new[] { HostedModelBox, HostedEffortBox })
            if (!box.IsDropDownOpen)
                box.MaxDropDownHeight = height;
    }

    // WinUI sets the list's offset whenever the picker arranges; this runs after each layout pass while it is open
    // and moves the list only when it crosses the panel's edges, so a second pass finds nothing to do.
    private void KeepOpenListInPanel(ComboBox box)
    {
        if (!box.IsDropDownOpen || FindPopup(box) is not { Child: FrameworkElement list } popup || list.ActualHeight <= 0)
            return;
        var top = list.TransformToVisual(this).TransformPoint(default).Y;
        var bottom = top + list.ActualHeight;
        double edge = AppTheme.Spacing.Md, shift = 0;
        if (bottom > ActualHeight - edge)
            shift = ActualHeight - edge - bottom;
        if (top + shift < edge)
            shift = edge - top;
        if (Math.Abs(shift) >= 1)
            popup.VerticalOffset += shift;
    }

    private static Microsoft.UI.Xaml.Controls.Primitives.Popup? FindPopup(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is Microsoft.UI.Xaml.Controls.Primitives.Popup popup)
                return popup;
            if (FindPopup(child) is { } found)
                return found;
        }
        return null;
    }
}
