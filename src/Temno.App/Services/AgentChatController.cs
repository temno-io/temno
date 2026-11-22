using System.Text.Json;
using System.Text.Json.Nodes;
using Temno.Core.Account;
using Temno.Core.Agent;
using Temno.Core.IO;
using Temno.Mcp;

namespace Temno.App.Services;

public sealed class AgentChatController : IDisposable
{
    public const int MaxIterations = AgentConversation.DefaultMaxIterations;
    private HostedAgentConversation _hosted;
    // WP-251: the project's other chats. Its Current is always _hosted.Session.
    private ChatSessionLibrary _library;
    private readonly object _libraryGate = new();
    private readonly SemaphoreSlim _operation = new(1, 1);
    private CancellationTokenSource? _sendCts;
    private Task _cancelTask = Task.CompletedTask;
    private string? _packagePath;
    private bool _attached;
    private bool _switching;
    private HostedAgentCatalog? _catalog;
    private string? _catalogError;
    // WP-422: the Settings model and effort a new project's first chat starts on, cached because DraftEstimate runs per
    // keystroke and reads no preferences. Refreshed when Settings saves (ReloadCapability).
    private volatile string _hostedModelChoice = ReadHostedModelChoice();
    private volatile string _hostedEffortChoice = ReadHostedEffortChoice();

    private static string ReadHostedModelChoice() => UserPreferences.Load().Agent.NewChatModel ?? "";
    private static string ReadHostedEffortChoice() => UserPreferences.Load().Agent.NewChatEffort ?? "";

    // UI consumers never receive the mutable worker-owned message graph.
    public ChatSession Session => ChatSessionStore.Snapshot(_hosted.Session);
    public HostedAgentCatalog? Catalog => _catalog;
    public string? CatalogError => _catalogError;
    public string? HostedStatus { get; private set; }
    public long? LastChargeCredits => Session.Hosted?.LastChargeCredits;
    public bool HasPendingHostedTurn => _hosted.HasPendingTurn;
    public event EventHandler? SessionUpdated;
    public event EventHandler? ToolsMutated;
    public bool IsBusy => _operation.CurrentCount == 0 || !_cancelTask.IsCompleted || _switching;
    public bool HasByokKey => !string.IsNullOrWhiteSpace(UserPreferences.Load().ResolveAgentApiKey());
    public bool HostedAvailable => HostedRuntime.IsSignedIn && HostedEndpoints.GatewayConfigured();
    public bool PreferByok => UserPreferences.Load().Agent.PreferByok;
    public bool UsesHosted => HostedAvailable && !PreferByok;
    public bool IsConfigured => HasByokKey || UsesHosted;

    public AgentChatController()
    {
        _library = ChatSessionLibrary.Open([], out _);
        _hosted = new HostedAgentConversation(_library.Current);
    }

    /// <summary>WP-251: this project's chats, newest first, with the open one marked.</summary>
    public IReadOnlyList<ChatHistoryEntry> History()
    {
        lock (_libraryGate)
            return _library.Entries();
    }

    /// <summary>False while a reply runs, a hosted turn is unresolved, or this chat is already empty.</summary>
    public bool CanStartNewChat
    {
        get
        {
            lock (_libraryGate)
                return !IsBusy && _library.CanStartNew;
        }
    }

    /// <summary>False while a reply runs or a hosted turn is unresolved: that turn is finished here.</summary>
    public bool CanSwitchChat
    {
        get
        {
            lock (_libraryGate)
                return !IsBusy && _library.CanLeaveCurrent;
        }
    }

    /// <summary>
    /// WP-251: saves this chat and opens an empty one with the same hosted choices: the model, effort and reasoning the
    /// user last used. The Settings model (WP-422) only sets where a new project's first chat starts.
    /// </summary>
    public bool NewChat() => Move(library => library.StartNew());

    /// <summary>WP-251: opens an earlier chat from this project's history.</summary>
    public bool OpenChat(Guid id)
    {
        if (!Move(library => library.Select(id))) return false;
        _ = ResumeHostedIfNeededAsync();
        return true;
    }

    /// <summary>WP-251: deletes an earlier chat and its file. Never the open chat or an unresolved one.</summary>
    public bool DeleteChat(Guid id)
    {
        if (_switching) return false;
        lock (_libraryGate)
        {
            if (!_library.CanDelete(id)) return false;
            // The file goes first: if it cannot be removed, the chat stays listed.
            if (!string.IsNullOrEmpty(_packagePath)) ChatSessionStore.Delete(_packagePath, id);
            _library.Delete(id);
        }
        SessionUpdated?.Invoke(this, EventArgs.Empty);
        return true;
    }

    private bool Move(Func<ChatSessionLibrary, ChatSwitch?> move)
    {
        if (IsBusy || !_operation.Wait(0)) return false;
        try
        {
            // Saved before the move, so a failed save leaves the panel where it was.
            Persist();
            ChatSwitch moved;
            lock (_libraryGate)
            {
                if (move(_library) is not { } result) return false;
                moved = result;
                _hosted = new HostedAgentConversation(_library.Current);
            }
            if (moved.DiscardLeft) DeleteChatFile(moved.Left.Id);
            HostedStatus = null;
        }
        finally { _operation.Release(); }
        SessionUpdated?.Invoke(this, EventArgs.Empty);
        return true;
    }

    private void DeleteChatFile(Guid id)
    {
        if (string.IsNullOrEmpty(_packagePath)) return;
        try { ChatSessionStore.Delete(_packagePath, id); }
        // An empty chat left behind is discarded again at the next open.
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    public void Attach()
    {
        if (_attached) return;
        _attached = true;
        AppSession.Current.SessionChanged += OnSessionChanged;
        AccountSessionService.Current.Changed += OnAccountChanged;
        _ = SwitchProjectAsync();
        _ = RefreshCatalogAsync();
    }

    public void Dispose()
    {
        if (_attached)
        {
            AppSession.Current.SessionChanged -= OnSessionChanged;
            AccountSessionService.Current.Changed -= OnAccountChanged;
            _attached = false;
        }
        Cancel();
        Persist();
    }

    public void Cancel()
    {
        if (!_cancelTask.IsCompleted) return;
        _cancelTask = CancelCurrentAsync();
    }

    private async Task CancelCurrentAsync()
    {
        var hosted = _hosted;
        var path = _packagePath;
        _sendCts?.Cancel();
        try
        {
            // A turn this install did not start is never cancelled on its own: only Discard sends anything for it.
            if (hosted.HasPendingTurn && hosted.PendingTurnStartedHere)
            {
                // Even after sign-out, save cancel intent before authentication can fail.
                var client = TryHostedClient() ?? new HostedAgentClient(
                    HostedEndpoints.ResolveGatewayUrl(), () => HostedRuntime.AccessToken);
                HostedStatus = "Cancelling the hosted turn; final usage may still be pending.";
                await hosted.CancelAsync(client, persist: () => PersistTo(path, hosted.Session)).ConfigureAwait(true);
                HostedStatus = hosted.HasPendingTurn
                    ? "Cancellation outcome pending. Reconnect to recover this turn."
                    : "Cancelled. Credits already used can still be charged.";
            }
        }
        catch (Exception ex)
        {
            HostedStatus = "Cancellation outcome pending. " + HostedApiErrors.Describe(ex);
        }
        finally { SessionUpdated?.Invoke(this, EventArgs.Empty); }
    }

    /// <param name="explicitHold">0 lets the gateway estimate the hold (WP-254); a larger value is a hold the user confirmed.</param>
    public async Task SendAsync(string text, long explicitHold = HostedAgentConversation.AutomaticHold)
    {
        if (IsBusy || !await _operation.WaitAsync(0).ConfigureAwait(true))
            throw new HostedApiException("Chat is already running or changing projects. Wait or cancel it.");
        using var sendCts = new CancellationTokenSource();
        _sendCts = sendCts;
        try
        {
            if (!IsConfigured)
                throw new AnthropicClientException(AnthropicClientErrorKind.MissingApiKey, AgentSetup.MissingKeyError);
            if (UsesHosted)
                await SendHostedAsync(text, explicitHold, sendCts.Token).ConfigureAwait(true);
            else
            {
                if (_hosted.HasPendingTurn)
                    throw new HostedApiException("Resolve the pending hosted turn before switching to BYOK.");
                await SendByokAsync(text, sendCts.Token).ConfigureAwait(true);
            }
        }
        finally
        {
            _sendCts = null;
            _operation.Release();
            Persist();
            SessionUpdated?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>WP-400: an unresolved hosted turn this install did not start. It runs only on Run.</summary>
    public bool HasForeignPendingTurn => _hosted.HasPendingTurn && !_hosted.PendingTurnStartedHere;

    /// <summary>What the panel shows for a turn that waits for Run or Discard.</summary>
    public sealed record PendingTurnSummary(string Model, string Effort, string Hold, string? Note, string Message,
        bool CanRun, string? RunBlocked);

    public PendingTurnSummary? ForeignPendingTurn()
    {
        if (!HasForeignPendingTurn) return null;
        var hosted = Session.Hosted ?? new HostedChatMetadata();
        var admitted = HostedAgentConversation.MayBeAdmitted(hosted);
        // The chat's own model and effort, as its pickers show them and as Run sends them; never the file's submission.
        var profile = SelectedProfile();
        var model = profile is not null ? HostedModelNames.Label(profile) : hosted.Model is { Length: > 0 } id ? id : "No model named";
        var effort = EffortLabel(hosted.Effort is { Length: > 0 } e && profile?.Efforts.Contains(e, StringComparer.OrdinalIgnoreCase) != false
            ? e : profile is null ? "medium" : HostedModelNames.StartingEffort(profile));
        var cap = hosted.Submission?.SpendingCapCredits > 0 ? hosted.Submission!.SpendingCapCredits : hosted.SpendingCapCredits ?? 0;
        var hold = admitted ? "Started elsewhere" : "Automatic hold";
        string? note = admitted
            ? "It was already sent from another PC. Run reconnects to it."
            : cap > 0
                ? $"Its file asks for a {HostedApiErrors.Credits(cap)}-credit cap. Run holds automatically instead, as a new message does."
                : null;
        static bool Visible(AgentMessage m) => m.Role == AgentMessageRole.user && m.Origin is null
            && m.Blocks.Exists(b => b.Kind == AgentContentBlock.KindText && !string.IsNullOrWhiteSpace(b.Text));
        var text = Session.Messages.LastOrDefault(m => m.Id == hosted.UserMessageId && Visible(m))
            ?? Session.Messages.LastOrDefault(Visible);
        var message = string.Join(" ", text?.Blocks.Where(b => b.Kind == AgentContentBlock.KindText).Select(b => b.Text) ?? []).Trim();
        if (message.Length > 160) message = message[..157] + "…";
        string? blocked = !HostedAvailable ? "Sign in to hosted chat to run it."
            : admitted && hosted.AccountId != HostedRuntime.Session?.UserId ? "It was started by another account, so it can only be discarded here."
            : !admitted && profile is null && _catalog is not null ? "Its model is not offered now. Discard it and send the message again."
            : null;
        return new PendingTurnSummary(model, effort, hold, note, message, blocked is null, blocked);
    }

    public static string EffortLabel(string effort) => effort switch
    {
        "minimal" => "Minimal",
        "low" => "Low",
        "medium" => "Medium",
        "high" => "High",
        "xhigh" => "xHigh",
        "max" => "Max",
        _ => effort,
    };

    /// <summary>
    /// WP-400: the user's Run for a turn this install did not start. It is sent as a new message would be: the
    /// visible message, this chat's tools, an automatic hold (or <paramref name="explicitHold"/> once the user has
    /// confirmed one), and a retry key recorded here. A hold confirmation comes back as for a new send.
    /// </summary>
    public async Task RunPendingAsync(long explicitHold = HostedAgentConversation.AutomaticHold)
    {
        if (IsBusy || !await _operation.WaitAsync(0).ConfigureAwait(true))
            throw new HostedApiException("Chat is already running or changing projects. Wait or cancel it.");
        using var sendCts = new CancellationTokenSource();
        _sendCts = sendCts;
        try
        {
            var client = TryHostedClient()
                ?? throw new HostedApiException("Sign in to hosted chat to run this turn.", code: "agent_not_ready");
            if (_catalog?.BillingPolicy?.EstimatedHolds != true) await RefreshCatalogAsync().ConfigureAwait(true);
            if (_catalog is null || !_catalog.Configured || !_catalog.AcceptTurns)
                throw new HostedApiException(_catalogError ?? "Hosted chat is not configured.", code: "agent_not_ready");
            HostedAgentConversation.RequireEstimatedHolds(_catalog);
            // The chat's own model, as its picker shows it (WP-400 review: never the file's frozen submission).
            var hosted = Session.Hosted;
            var profile = SelectedProfile()
                ?? (hosted is not null && HostedAgentConversation.MayBeAdmitted(hosted) ? PickProfile(_catalog, null) : null)
                ?? throw new HostedApiException("The model this chat names is not offered now. Discard it and send the message again.");
            var toolNames = _catalog.SelectTools(InAppTools().Select(t => t.Name));
            if (toolNames.Length == 0)
                throw new HostedApiException("The hosted tool catalog is unavailable. Refresh the account connection and try again.");
            await _hosted.RunPendingAsync(client, profile, toolNames, Runner(), explicitHold, Progress(), Persist,
                sendCts.Token, Gate()).ConfigureAwait(true);
        }
        finally
        {
            _sendCts = null;
            _operation.Release();
            Persist();
            SessionUpdated?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// WP-400: the user's Discard for a turn this install did not start. A turn never sent ends here at no charge;
    /// one the Gateway may hold is cancelled there for the signed-in account that started it, else dropped here.
    /// </summary>
    public async Task DiscardPendingAsync()
    {
        if (IsBusy || !await _operation.WaitAsync(0).ConfigureAwait(true))
            throw new HostedApiException("Chat is already running or changing projects. Wait or cancel it.");
        try
        {
            var outcome = await _hosted.DiscardPendingAsync(TryHostedClient(), Persist).ConfigureAwait(true);
            var charge = _hosted.Session.Hosted?.LastChargeCredits;
            HostedStatus = outcome switch
            {
                HostedAgentConversation.DiscardOutcome.Withdrawn => "Discarded. It was never sent, so no credits were charged.",
                HostedAgentConversation.DiscardOutcome.Cancelled => "Discarded and cancelled. " + (charge is > 0
                    ? $"Final charge {charge} credits." : charge == 0 ? "No credits charged." : "Final charge is pending."),
                HostedAgentConversation.DiscardOutcome.Forgotten =>
                    "Discarded here. It was started elsewhere, so any charge stays with the account that started it.",
                _ => null,
            };
        }
        finally
        {
            _operation.Release();
            Persist();
            SessionUpdated?.Invoke(this, EventArgs.Empty);
        }
    }

    public void ApplyHostedChoices(string? modelId, string? effort, bool reasoningEnabled)
    {
        if (IsBusy || _hosted.HasPendingTurn) return;
        lock (_hosted.Session.SyncRoot)
        {
            var hosted = _hosted.Session.Hosted ??= new HostedChatMetadata();
            if (!string.IsNullOrWhiteSpace(modelId)) hosted.Model = modelId;
            if (!string.IsNullOrWhiteSpace(effort)) hosted.Effort = effort;
            hosted.ReasoningEnabled = reasoningEnabled;
            Persist();
        }
        SessionUpdated?.Invoke(this, EventArgs.Empty);
    }

    public HostedAgentProfile? SelectedProfile() => PickProfile(_catalog, Session.Hosted?.Model);

    // The Settings effort belongs to the Settings model: when that model is not served and a chat falls back to the
    // default, the effort chosen for the other model is not carried over.
    private string EffortChoiceFor(HostedAgentProfile profile) =>
        _hostedModelChoice.Length == 0 || string.Equals(profile.Id, _hostedModelChoice, StringComparison.Ordinal) ? _hostedEffortChoice : "";

    /// <summary>
    /// The effort this chat shows and sends on <paramref name="profile"/>: its saved one, else, for a new project's first
    /// chat, the Settings effort, else High (<see cref="HostedModelNames.EffortFor"/>).
    /// </summary>
    public string EffortFor(HostedAgentProfile profile)
    {
        lock (_hosted.Session.SyncRoot)
        {
            var hosted = _hosted.Session.Hosted;
            return HostedModelNames.EffortFor(profile, hosted?.Effort, hosted?.Model is { Length: > 0 }, EffortChoiceFor(profile));
        }
    }

    // A saved model wins; a chat with none takes the Settings choice when the Gateway serves it qualified, else Haiku 5.5.
    private HostedAgentProfile? PickProfile(HostedAgentCatalog? catalog, string? modelId) =>
        HostedModelNames.Pick(catalog?.Models ?? [], modelId, _hostedModelChoice);

    /// <summary>
    /// WP-422: the Gateway's models for Settings' picker, from a catalog fetch (never a quote). Null when signed out
    /// or the fetch fails: Settings then shows the saved choice by name and fills the list on the next open.
    /// </summary>
    public static async Task<IReadOnlyList<HostedAgentProfile>?> LoadCatalogModelsAsync()
    {
        var client = TryHostedClient();
        if (client is null) return null;
        try { return (await client.GetCatalogAsync().ConfigureAwait(true)).Models; }
        catch (Exception) { return null; }
    }

    public async Task RefreshCatalogAsync()
    {
        var client = TryHostedClient();
        if (client is null)
        {
            _catalog = null;
            _catalogError = null;
            SessionUpdated?.Invoke(this, EventArgs.Empty);
            return;
        }
        try
        {
            _catalog = await client.GetCatalogAsync().ConfigureAwait(true);
            _catalogError = _catalog.AcceptTurns && _catalog.Configured ? null : "Hosted chat is not configured.";
        }
        catch (Exception ex)
        {
            _catalog = null;
            _catalogError = HostedApiErrors.Describe(ex);
        }
        SessionUpdated?.Invoke(this, EventArgs.Empty);
    }

    public void ReloadCapability()
    {
        _hostedModelChoice = ReadHostedModelChoice();
        _hostedEffortChoice = ReadHostedEffortChoice();
        SessionUpdated?.Invoke(this, EventArgs.Empty);
    }

    private async Task SendByokAsync(string text, CancellationToken ct)
    {
        // BYOK works on a private copy; tool events can render the published session safely.
        var conversation = new AgentConversation(Session);
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
        var client = AnthropicClient.FromPreferences(UserPreferences.Load(), http);
        try
        {
            await conversation.SendUserAsync(text, client, InAppTools(), Runner(),
                maxIterations: MaxIterations, cancellationToken: ct, gate: Gate()).ConfigureAwait(true);
        }
        finally
        {
            lock (_libraryGate)
            {
                _library.ReplaceCurrent(conversation.Session);
                _hosted = new HostedAgentConversation(conversation.Session);
            }
        }
    }

    /// <summary>
    /// WP-254: the composer's likely charge for sending <paramref name="draft"/> now, or null when the catalog
    /// cannot price it. The history's size is cached per session revision so typing stays cheap.
    /// </summary>
    /// Called per keystroke: it reads the live session under its lock (no snapshot copy, no preferences read).
    public (long Low, long High)? DraftEstimate(string draft)
    {
        var catalog = _catalog;
        if (catalog is null) return null;
        HostedAgentProfile profile;
        string effort;
        bool reasoning;
        long historyBytes;
        lock (_hosted.Session.SyncRoot)
        {
            var session = _hosted.Session;
            if (PickProfile(catalog, session.Hosted?.Model) is not { } picked) return null;
            profile = picked;
            effort = HostedModelNames.EffortFor(profile, session.Hosted?.Effort, session.Hosted?.Model is { Length: > 0 }, EffortChoiceFor(profile));
            reasoning = profile.OffEfforts.Length == 0 || (session.Hosted?.ReasoningEnabled ?? true);
            var key = (session.Id, session.Messages.Count, session.UpdatedAt, profile.Id);
            if (_estimateKey != key)
            {
                _estimateHistoryBytes = HostedEstimate.HistoryBytes(session.Messages, profile);
                _estimateKey = key;
            }
            historyBytes = _estimateHistoryBytes;
        }
        if (!ReferenceEquals(_estimateToolsCatalog, catalog))
        {
            _estimateTools = catalog.SelectTools(InAppTools().Select(t => t.Name));
            _estimateToolsCatalog = catalog;
        }
        return HostedEstimate.ForDraft(catalog, profile, effort, reasoning, historyBytes, draft, _estimateTools);
    }
    private (Guid, int, DateTimeOffset, string)? _estimateKey;
    private long _estimateHistoryBytes;
    private HostedAgentCatalog? _estimateToolsCatalog;
    private IReadOnlyCollection<string> _estimateTools = [];

    /// <summary>
    /// The text of a message a background resume withdrew (never admitted, nothing charged), once. The panel puts it
    /// back in the composer so it is not lost.
    /// </summary>
    public string? TakeWithdrawnDraft() => Interlocked.Exchange(ref _withdrawnDraft, null);
    private string? _withdrawnDraft;

    private async Task SendHostedAsync(string text, long explicitHold, CancellationToken ct)
    {
        var client = TryHostedClient()
            ?? throw new HostedApiException("Hosted chat is not configured.", code: "agent_not_ready");
        if (_hosted.HasPendingTurn)
        {
            // A turn this install started reconnects; any other one waits for Run or Discard (WP-400).
            await _hosted.ResumeAsync(client, Runner(), Progress(), Persist, ct, Gate()).ConfigureAwait(true);
            return;
        }
        // A catalog fetched before the gateway gained estimated holds is stale: fetch it again before refusing.
        if (_catalog?.BillingPolicy?.EstimatedHolds != true) await RefreshCatalogAsync().ConfigureAwait(true);
        if (_catalog is null || !_catalog.Configured || !_catalog.AcceptTurns)
            throw new HostedApiException(_catalogError ?? "Hosted chat is not configured.", code: "agent_not_ready");
        // A gateway without estimated holds (older, or rolled back) would read 0 as an invalid cap: send nothing.
        HostedAgentConversation.RequireEstimatedHolds(_catalog);
        var profile = SelectedProfile() ?? throw new HostedApiException("The hosted catalog has no qualified models.");
        lock (_hosted.Session.SyncRoot)
        {
            var hosted = _hosted.Session.Hosted!;
            if (string.IsNullOrWhiteSpace(hosted.Effort) || !profile.Efforts.Contains(hosted.Effort, StringComparer.OrdinalIgnoreCase))
                hosted.Effort = HostedModelNames.EffortFor(profile, hosted.Effort, hosted.Model is { Length: > 0 }, EffortChoiceFor(profile));
            if (profile.OffEfforts.Length == 0)
                hosted.ReasoningEnabled = true;
        }
        var toolNames = _catalog.SelectTools(InAppTools().Select(t => t.Name));
        if (toolNames.Length == 0)
            throw new HostedApiException("The hosted tool catalog is unavailable. Refresh the account connection and try again.");
        await _hosted.SendUserAsync(text, client, profile, toolNames,
            Runner(), explicitHold, Progress(), Persist, ct, Gate()).ConfigureAwait(true);
    }

    private IProgress<HostedTurnProgress> Progress() => new InlineProgress(p =>
    {
        if (p.Status is not null) HostedStatus = p.Status;
        SessionUpdated?.Invoke(this, EventArgs.Empty);
    });

    private sealed class InlineProgress(Action<HostedTurnProgress> report) : IProgress<HostedTurnProgress>
    {
        public void Report(HostedTurnProgress value) => report(value);
    }

    // WP-400: Attach, a project switch and OpenChat resume only a turn this install started. A turn from a package
    // made elsewhere stays put, and the panel offers Run and Discard.
    private async Task ResumeHostedIfNeededAsync()
    {
        if (!_attached || !UsesHosted || !_hosted.HasPendingTurn || !_hosted.PendingTurnStartedHere || IsBusy
            || !await _operation.WaitAsync(0).ConfigureAwait(true)) return;
        using var cts = new CancellationTokenSource();
        _sendCts = cts;
        try
        {
            SessionUpdated?.Invoke(this, EventArgs.Empty);
            await _hosted.ResumeAsync(TryHostedClient()!, Runner(), Progress(), Persist, cts.Token, Gate()).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            HostedStatus = HostedApiErrors.Describe(ex);
            if (ex is HostedApiException { WithdrawnText: { Length: > 0 } withdrawn })
            {
                _withdrawnDraft = withdrawn;
                HostedStatus = "Your earlier message was not sent and is back in the composer. " + HostedStatus;
            }
        }
        finally
        {
            _sendCts = null;
            _operation.Release();
            SessionUpdated?.Invoke(this, EventArgs.Empty);
        }
    }

    private static HostedAgentClient? TryHostedClient()
    {
        var url = HostedEndpoints.ResolveGatewayUrl();
        var owner = HostedRuntime.Session?.UserId;
        return string.IsNullOrWhiteSpace(url) || !HostedRuntime.IsSignedIn
            ? null : new HostedAgentClient(url, () =>
            {
                var source = HostedRuntime.Session;
                if (source is null || source.UserId != owner) return null;
                var token = source.AccessToken;
                return source.UserId == owner ? token : null;
            }, accountId: owner);
    }

    private void OnToolExecuted(string toolName)
    {
        if (IsMutationTool(toolName)) ToolsMutated?.Invoke(this, EventArgs.Empty);
    }

    private void OnSessionChanged(object? sender, EventArgs e) => _ = SwitchProjectAsync();

    private async Task SwitchProjectAsync()
    {
        _switching = true;
        Cancel();
        await _cancelTask.ConfigureAwait(true);
        await _operation.WaitAsync().ConfigureAwait(true);
        try
        {
            Persist();
            _packagePath = AppSession.Current.Session?.PackagePath;
            // WP-251: an unresolved hosted turn opens first, else the newest chat, empty or not.
            var library = ChatSessionLibrary.Open(ChatSessionStore.Load(_packagePath), out var discarded);
            foreach (var empty in discarded) DeleteChatFile(empty.Id);
            lock (_libraryGate)
            {
                _library = library;
                _hosted = new HostedAgentConversation(library.Current);
            }
            HostedStatus = null;
        }
        catch (Exception ex)
        {
            // Never keep the previous project's chat: the next save would write it into this one.
            lock (_libraryGate)
            {
                _library = ChatSessionLibrary.Open([], out _);
                _hosted = new HostedAgentConversation(_library.Current);
            }
            HostedStatus = "Could not load chat. " + ex.Message;
        }
        finally
        {
            _switching = false;
            _operation.Release();
            SessionUpdated?.Invoke(this, EventArgs.Empty);
        }
        await ResumeHostedIfNeededAsync().ConfigureAwait(true);
    }

    private void OnAccountChanged(object? sender, EventArgs e)
    {
        _ = RefreshCatalogAsync();
        SessionUpdated?.Invoke(this, EventArgs.Empty);
    }

    private void Persist() => PersistTo(_packagePath, _hosted.Session);

    private static void PersistTo(string? path, ChatSession session)
    {
        if (string.IsNullOrEmpty(path)) return;
        lock (session.SyncRoot)
        {
            if (session.Messages.Count == 0 && session.Hosted is null) return;
            ChatSessionStore.Save(path, session);
        }
    }
    // WP-400: the open project's tools. Project switching (manage_project and its four aliases) is never offered,
    // and the runner and the gate refuse it even if a model or a saved turn names it.
    internal static IReadOnlyList<AnthropicToolSchema> InAppTools()
    {
        return AnthropicToolSchema.FromCatalog(
            McpToolRegistry.Tools
                .Where(t => ToolExecutor.ImplementedTools.Contains(t.Name) && !AgentToolGate.ProjectTools.Contains(t.Name))
                .Select(t => (t.Name, t.Description, (JsonNode?)t.InputSchema)));
    }

    internal static IReadOnlySet<string> InAppToolNames { get; } =
        InAppTools().Select(t => t.Name).ToHashSet(StringComparer.Ordinal);

    /// <summary>WP-400: the panel that asks the user before an export, an outside file, a download or a paid generation.</summary>
    public IAgentToolConsent? ToolConsent { get; set; }

    private AgentToolGate Gate() => new(InAppToolNames, ToolConsent);

    private McpInProcessToolRunner Runner() => new(OnToolExecuted, InAppToolNames);

    private static bool IsMutationTool(string name) => name is not (
        "get_timeline" or "get_projects" or "get_project" or "list_tools" or
        "inspect_timeline" or "help");
}

internal sealed class McpInProcessToolRunner : IToolRunner
{
    private readonly Action<string>? _onExecuted;
    private readonly IReadOnlySet<string> _allowed;

    /// <param name="allowed">WP-400: the only tools this runner executes. The gate decides first; this is the backstop.</param>
    public McpInProcessToolRunner(Action<string>? onExecuted, IReadOnlySet<string> allowed)
    {
        _onExecuted = onExecuted;
        _allowed = allowed;
    }

    public Task<AgentToolExecutionResult> ExecuteAsync(
        string name,
        string inputJson,
        CancellationToken cancellationToken)
    {
        if (!_allowed.Contains(name) || AgentToolGate.ProjectTools.Contains(name))
            return Task.FromResult(AgentToolExecutionResult.Error($"Not run: {name} is not available in this chat."));
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            JsonObject args;
            try
            {
                args = string.IsNullOrWhiteSpace(inputJson)
                    ? []
                    : JsonNode.Parse(inputJson) as JsonObject ?? [];
            }
            catch (JsonException)
            {
                args = [];
            }

            var result = ToolExecutor.Execute(name, args);
            var blocks = new List<AgentToolResultBlock>();
            foreach (var item in result.Content)
            {
                if (string.Equals(item.Type, "image", StringComparison.OrdinalIgnoreCase)
                    && !string.IsNullOrEmpty(item.Data))
                {
                    blocks.Add(AgentToolResultBlock.Image(item.Data!, item.MimeType ?? "image/png"));
                }
                else
                {
                    blocks.Add(AgentToolResultBlock.FromText(item.Text ?? ""));
                }
            }

            if (blocks.Count == 0)
                blocks.Add(AgentToolResultBlock.FromText(""));

            _onExecuted?.Invoke(name);
            return new AgentToolExecutionResult(blocks, result.IsError);
        }, cancellationToken);
    }
}
