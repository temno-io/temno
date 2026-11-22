using System.Text.Json.Nodes;
using Temno.Core.Account;
using Temno.Core.Agent.Skills;

namespace Temno.Core.Agent;

public sealed class HostedAgentConversation
{
    // WP-254: 0 asks the gateway to estimate the hold. An explicit hold (5 or more) is one the user confirmed.
    public const long AutomaticHold = 0;
    // Only for gateways that predate WP-254 and publish no completion policy; newer ones size every call themselves.
    public const int DefaultMaxOutputTokens = 4096;
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private CancellationTokenSource? _active;
    private int _cancelPending;

    public HostedAgentConversation(ChatSession? session = null)
    {
        Session = session ?? new ChatSession();
        Session.Hosted ??= new HostedChatMetadata();
    }

    public ChatSession Session { get; }
    public bool HasPendingTurn
    {
        get
        {
            lock (Session.SyncRoot)
                return Session.Hosted is { Terminal: not true } h
                    && (h.Submission is not null || h.QuoteId is not null || h.TurnId is not null);
        }
    }

    /// <summary>
    /// WP-400: true when the unresolved turn is one this install created and has not seen settle:
    /// <see cref="HostedTurnOrigins"/> holds its retry key for the account the chat names, with the same frozen
    /// submission and no other turn id. Only such a turn resumes, or reconnects on Send, without a click; any other
    /// one waits for <see cref="RunPendingAsync"/> or <see cref="DiscardPendingAsync"/>.
    /// </summary>
    public bool PendingTurnStartedHere
    {
        get
        {
            string? key, account, turn;
            HostedQuoteRequest? submission;
            lock (Session.SyncRoot)
            {
                if (!HasPendingTurn) return false;
                key = Session.Hosted!.IdempotencyKey;
                account = Session.Hosted.AccountId;
                turn = Session.Hosted.TurnId;
                submission = Session.Hosted.Submission;
            }
            return HostedTurnOrigins.Matches(key, account, HostedTurnOrigins.Digest(submission), turn);
        }
    }

    public const string NotStartedHereCode = "pending_turn_not_started_here";

    private static HostedApiException NotStartedHere() => new(
        "This chat holds a hosted turn that was not sent from this PC. Choose Run to send it or Discard to drop it.",
        code: NotStartedHereCode);

    public Task SendUserAsync(string text, HostedAgentClient client, HostedAgentProfile profile,
        IReadOnlyList<string> toolNames, IToolRunner toolRunner, long spendingCapCredits,
        IProgress<HostedTurnProgress>? progress, Action? persist = null,
        CancellationToken cancellationToken = default, AgentToolGate? gate = null) =>
        RunAsync(async ct =>
        {
            ArgumentNullException.ThrowIfNull(profile);
            if (HasPendingTurn)
            {
                // Sending reconnects a turn this install started; any other one needs Run or Discard.
                if (!PendingTurnStartedHere) throw NotStartedHere();
                await RecoverAsync(client, toolRunner, progress, persist, ct, gate).ConfigureAwait(false);
                return;
            }
            var trimmed = text.Trim();
            if (trimmed.Length == 0) return;
            var maxTokens = MaxTokensFor(profile);
            if (spendingCapCredits is < 0 or (> 0 and < 5))
                throw new ArgumentOutOfRangeException(nameof(spendingCapCredits));
            RequireAccount(client);
            var user = AgentMessage.UserText(trimmed);
            HostedChatMetadata next;
            lock (Session.SyncRoot)
            {
                var previous = Session.Hosted!;
                next = NewTurn(previous, profile, client.AccountId!, Guid.NewGuid().ToString("D"), user.Id,
                    previous.Effort ?? HostedModelNames.StartingEffort(profile), previous.ReasoningEnabled ?? true,
                    maxTokens, spendingCapCredits, toolNames, Session.Messages.Append(user));
            }
            // Written before the turn exists, with the submission it freezes: nothing is sent if it cannot be.
            RecordOrigin(client, next);

            Update(persist, () =>
            {
                Session.Messages.Add(user);
                if (Session.Messages.Count == 1)
                    Session.Title = trimmed.Length <= 48 ? trimmed : trimmed[..45] + "…";
                Session.Hosted = next;
            });
            await RecoverAsync(client, toolRunner, progress, persist, ct, gate).ConfigureAwait(false);
        }, cancellationToken, rejectBusy: true);

    /// <summary>
    /// Recovers a turn this install started. Any other pending turn throws <see cref="NotStartedHereCode"/> before
    /// a request is sent: opening a package never runs a turn on its own (WP-400).
    /// </summary>
    public Task ResumeAsync(HostedAgentClient client, IToolRunner toolRunner,
        IProgress<HostedTurnProgress>? progress, Action? persist = null,
        CancellationToken cancellationToken = default, AgentToolGate? gate = null) =>
        RunAsync(ct =>
        {
            if (!HasPendingTurn) return Task.CompletedTask;
            if (!PendingTurnStartedHere) throw NotStartedHere();
            return RecoverAsync(client, toolRunner, progress, persist, ct, gate);
        }, cancellationToken);

    /// <summary>
    /// WP-400: the user's Run for a pending turn this install did not start. A turn the Gateway may already hold
    /// (<see cref="MayBeAdmitted"/>) recovers only for the account the chat names. Anything else is sent as a new
    /// message would be: the visible messages up to the pending one, this chat's tools, <paramref name="profile"/> with
    /// the chat's own effort and reasoning (what its pickers show), a fresh retry key recorded here, and
    /// <paramref name="spendingCapCredits"/> (0 for an automatic hold). Nothing of the file's frozen submission is sent,
    /// and its cap never is: only a hold the user confirms here is explicit.
    /// </summary>
    public Task RunPendingAsync(HostedAgentClient client, HostedAgentProfile profile, IReadOnlyList<string> toolNames,
        IToolRunner toolRunner, long spendingCapCredits, IProgress<HostedTurnProgress>? progress,
        Action? persist = null, CancellationToken cancellationToken = default, AgentToolGate? gate = null) =>
        RunAsync(async ct =>
        {
            ArgumentNullException.ThrowIfNull(client);
            ArgumentNullException.ThrowIfNull(profile);
            if (!HasPendingTurn) return;
            if (PendingTurnStartedHere)
            {
                await RecoverAsync(client, toolRunner, progress, persist, ct, gate).ConfigureAwait(false);
                return;
            }
            if (spendingCapCredits is < 0 or (> 0 and < 5))
                throw new ArgumentOutOfRangeException(nameof(spendingCapCredits));
            var hosted = Session.Hosted!;
            if (MayBeAdmitted(hosted))
            {
                ValidateAccount(client, hosted);
                // The user chose to run this turn as it is, so a restart may resume it as one sent from here.
                if (Guid.TryParse(hosted.IdempotencyKey, out _))
                {
                    RecordOrigin(client, hosted);
                    if (Guid.TryParse(hosted.TurnId, out var admittedTurn))
                        TryRecordTurn(hosted.IdempotencyKey, admittedTurn);
                }
                await RecoverAsync(client, toolRunner, progress, persist, ct, gate).ConfigureAwait(false);
                return;
            }
            AgentMessage? user;
            lock (Session.SyncRoot)
                user = PendingUserMessage();
            if (user is null)
                throw new HostedApiException("This pending turn has no message to send. Discard it to continue.");
            var maxTokens = MaxTokensFor(profile);
            RequireAccount(client);
            var effort = hosted.Effort is { Length: > 0 } e && profile.Efforts.Contains(e, StringComparer.OrdinalIgnoreCase)
                ? e : HostedModelNames.StartingEffort(profile);
            var reasoning = profile.OffEfforts.Length == 0 || (hosted.ReasoningEnabled ?? true);
            HostedChatMetadata next;
            lock (Session.SyncRoot)
            {
                var index = Session.Messages.IndexOf(user);
                next = NewTurn(Session.Hosted!, profile, client.AccountId!, Guid.NewGuid().ToString("D"), user.Id, effort,
                    reasoning, maxTokens, spendingCapCredits, toolNames, Session.Messages.Take(index + 1));
            }
            RecordOrigin(client, next);
            Update(persist, () => Session.Hosted = next);
            await RecoverAsync(client, toolRunner, progress, persist, ct, gate).ConfigureAwait(false);
        }, cancellationToken, rejectBusy: true);

    /// <summary>What <see cref="DiscardPendingAsync"/> did.</summary>
    public enum DiscardOutcome { Nothing, Withdrawn, Cancelled, Forgotten }

    /// <summary>
    /// WP-400: the user's Discard for a pending turn this install did not start. A turn never submitted anywhere this
    /// chat knows of (no turn id, no create attempt) ends here at zero charge, and its unsent message leaves the chat,
    /// with no request. A turn the Gateway may hold is cancelled there when it names the signed-in account;
    /// otherwise, or if that fails, it is dropped from this chat with its charge unknown.
    /// </summary>
    public async Task<DiscardOutcome> DiscardPendingAsync(HostedAgentClient? client, Action? persist = null,
        CancellationToken cancellationToken = default)
    {
        var result = DiscardOutcome.Nothing;
        await RunAsync(async ct =>
        {
            if (!HasPendingTurn) return;
            var hosted = Session.Hosted!;
            var hasTurn = Guid.TryParse(hosted.TurnId, out var turn) && turn != Guid.Empty;
            if (!hasTurn && !hosted.SubmissionAttempted)
            {
                Update(persist, () =>
                {
                    if (PendingUserMessage() is { } unsent) Session.Messages.Remove(unsent);
                    hosted.Terminal = true;
                    hosted.LastChargeCredits = 0;
                    hosted.LastError = "discarded";
                });
                result = DiscardOutcome.Withdrawn;
                return;
            }
            if (hasTurn && client is not null && hosted.AccountId is { Length: > 0 } owner
                && string.Equals(owner, client.AccountId, StringComparison.Ordinal))
            {
                try
                {
                    Update(persist, () => hosted.CancelRequested = true);
                    await CancelKnownTurnAsync(client, turn, persist, ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is HostedApiException or HttpRequestException
                    || (ex is OperationCanceledException && !ct.IsCancellationRequested))
                {
                    // Not cancelled there: it is dropped from this chat below, with its charge unknown.
                }
                if (hosted.Terminal == true)
                {
                    result = DiscardOutcome.Cancelled;
                    return;
                }
            }
            ct.ThrowIfCancellationRequested();
            Update(persist, () =>
            {
                hosted.Terminal = true;
                hosted.LastChargeCredits = null;
                hosted.LastError = "discarded_elsewhere";
            });
            result = DiscardOutcome.Forgotten;
        }, cancellationToken, rejectBusy: true).ConfigureAwait(false);
        return result;
    }

    /// <summary>
    /// WP-400: a pending turn the Gateway may already hold, so Run recovers it instead of sending it anew: one with a
    /// turn id, or one whose create was attempted with a saved quote and key (the quote is the Gateway's, bound to its
    /// owner). An attempt with no quote is not a state a send leaves, so a file claiming it is sent as new.
    /// </summary>
    public static bool MayBeAdmitted(HostedChatMetadata hosted)
    {
        ArgumentNullException.ThrowIfNull(hosted);
        return (Guid.TryParse(hosted.TurnId, out var turn) && turn != Guid.Empty)
            || (hosted.SubmissionAttempted && Guid.TryParse(hosted.QuoteId, out var quote) && quote != Guid.Empty
                && Guid.TryParse(hosted.IdempotencyKey, out var key) && key != Guid.Empty);
    }

    // The message the pending turn sends: its recorded id, else (legacy) the last user text. Only a user's own text
    // message counts: the panel shows those, and never a system message.
    private AgentMessage? PendingUserMessage()
    {
        var hosted = Session.Hosted!;
        static bool Visible(AgentMessage m) => m.Role == AgentMessageRole.user && m.Origin is null
            && m.Blocks.Exists(b => b.Kind == AgentContentBlock.KindText && !string.IsNullOrWhiteSpace(b.Text));
        return (hosted.UserMessageId is { } id ? Session.Messages.FirstOrDefault(m => m.Id == id && Visible(m)) : null)
            ?? Session.Messages.LastOrDefault(Visible);
    }

    private static int MaxTokensFor(HostedAgentProfile profile)
    {
        // WP-254: where the gateway publishes a completion policy, capacity is its decision (0 = use policy).
        // An older gateway advertises only a provider ceiling, so keep the legacy per-call clamp for it.
        var maxTokens = profile.Completion is { Count: > 0 } ? 0
            : profile.MaxOutputTokens == 0 ? DefaultMaxOutputTokens
            : Math.Min(profile.MaxOutputTokens, DefaultMaxOutputTokens);
        if (maxTokens is > 0 and < 128)
            throw new ArgumentOutOfRangeException(nameof(profile), "The hosted output limit must be at least 128 tokens.");
        return maxTokens;
    }

    private static void RequireAccount(HostedAgentClient client)
    {
        if (string.IsNullOrWhiteSpace(client.AccountId))
            throw new HostedApiException("Sign in again before sending: this sign-in has no account id.", code: "account_required");
    }

    // WP-400: the record is written before the turn exists, with its key and frozen submission, so a turn this install
    // sends is always one it can resume and an edited chat is not.
    private static void RecordOrigin(HostedAgentClient client, HostedChatMetadata turn)
    {
        RequireAccount(client);
        try
        {
            HostedTurnOrigins.Record(turn.IdempotencyKey!, client.AccountId!, HostedTurnOrigins.Digest(turn.Submission));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new HostedApiException("Temno could not record this message on this PC, so it was not sent. " + ex.Message,
                code: "origin_unrecorded");
        }
    }

    // Best effort: a turn id the record lacks makes the turn need a click after a restart, nothing worse.
    private static void TryRecordTurn(string? key, Guid turnId)
    {
        try { HostedTurnOrigins.RecordTurn(key, turnId); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static HostedChatMetadata NewTurn(HostedChatMetadata previous, HostedAgentProfile profile, string account,
        string key, Guid userMessageId, string effort, bool reasoning, int maxTokens, long spendingCapCredits,
        IEnumerable<string> toolNames, IEnumerable<AgentMessage> history) => new()
    {
        Model = profile.Id,
        ToolImages = ImagesFor(profile),
        AccountId = account,
        Effort = effort,
        ReasoningEnabled = reasoning,
        SpendingCapCredits = spendingCapCredits == AutomaticHold ? null : spendingCapCredits,
        IdempotencyKey = key,
        UserMessageId = userMessageId,
        Terminal = false,
        Extra = previous.Extra,
        Submission = new HostedQuoteRequest
        {
            Model = profile.Id,
            Effort = effort,
            ReasoningEnabled = reasoning,
            MaxOutputTokens = maxTokens,
            SpendingCapCredits = spendingCapCredits,
            Messages = HostedHistory.ToQuoteMessages(history, ImagesFor(profile)),
            Tools = [.. toolNames],
        },
    };

    // WP-436: images go only to a model the catalog says takes them, under the rules it published.
    private static HostedImagePolicy? ImagesFor(HostedAgentProfile profile) => profile.ImageInput ? profile.ImagePolicy : null;

    private async Task RunAsync(Func<CancellationToken, Task> action, CancellationToken ct, bool rejectBusy = false)
    {
        if (!await _lifecycle.WaitAsync(0, ct).ConfigureAwait(false))
        {
            if (rejectBusy) throw new HostedApiException("The hosted turn is already running. Wait or cancel it.");
            return;
        }
        using var active = CancellationTokenSource.CreateLinkedTokenSource(ct);
        try
        {
            lock (Session.SyncRoot)
            {
                if (_cancelPending > 0) throw new HostedApiException("Cancellation is still pending.");
                _active = active;
            }
            await action(active.Token).ConfigureAwait(false);
        }
        finally
        {
            lock (Session.SyncRoot) _active = null;
            _lifecycle.Release();
        }
    }

    public async Task CancelAsync(HostedAgentClient client, CancellationToken cancellationToken = default,
        Action? persist = null)
    {
        // Signal first so cancel can interrupt a blocked stream, then take the same consumer gate.
        lock (Session.SyncRoot)
        {
            if (!HasPendingTurn) return;
            Session.Hosted!.CancelRequested = true;
            Session.UpdatedAt = DateTimeOffset.UtcNow;
            persist?.Invoke();
            _cancelPending++;
            _active?.Cancel();
        }
        var acquired = false;
        try
        {
            await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
            acquired = true;
            var hosted = Session.Hosted!;
            if (hosted.Terminal == true) return;
            if (Guid.TryParse(hosted.TurnId, out var id) && id != Guid.Empty)
                await CancelKnownTurnAsync(client, id, persist, cancellationToken).ConfigureAwait(false);
            else if (!hosted.SubmissionAttempted && hosted.TurnId is null
                && hosted.Submission is not null && hosted.UserMessageId is not null)
                Update(persist, () => { hosted.Terminal = true; hosted.LastChargeCredits = 0; });
            // A lost create response remains pending: Resume resolves the exact quote/key, then cancels.
        }
        finally
        {
            lock (Session.SyncRoot) _cancelPending--;
            if (acquired) _lifecycle.Release();
        }
    }

    private async Task RecoverAsync(HostedAgentClient client, IToolRunner runner,
        IProgress<HostedTurnProgress>? progress, Action? persist, CancellationToken ct, AgentToolGate? gate)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(runner);
        if (!HasPendingTurn) return;
        var hosted = Session.Hosted!;
        // WP-400: without a gate the turn may run only what it offered, and nothing that needs the user's approval.
        gate ??= new AgentToolGate(hosted.Submission?.Tools ?? []);
        ValidateAccount(client, hosted);
        progress?.Report(new() { Status = "Recovering the hosted turn…", Pending = true });
        if (!Guid.TryParse(hosted.TurnId, out var turnId) || turnId == Guid.Empty)
        {
            if (!Guid.TryParse(hosted.IdempotencyKey, out var key) || key == Guid.Empty)
                throw new HostedApiException("The previous submission has no saved retry key. Its outcome is pending; do not resubmit it.");
            if (!Guid.TryParse(hosted.QuoteId, out var quoteId) || quoteId == Guid.Empty)
            {
                if (hosted.Submission is null)
                    throw new HostedApiException("The previous submission is missing its quote. Its outcome is pending.");
                ct.ThrowIfCancellationRequested();
                HostedQuote quote;
                try { quote = await client.CreateQuoteAsync(HostedHistory.WithInstructions(hosted.Submission), ct).ConfigureAwait(false); }
                catch (HostedApiException ex) when (ex.StatusCode is 400 or 402 or 413
                    && !hosted.SubmissionAttempted && hosted.TurnId is null)
                {
                    // A quote cannot reserve credits or invoke a provider. Definite rejection
                    // lets the retained draft be corrected without replaying an invalid intent.
                    var withdrawn = Withdraw(hosted, ex.Code, persist);
                    // A hold that only needs the user's yes is not a rejection (WP-254 Q12): the text reads right
                    // while the confirm dialog is open and after "Don't send".
                    progress?.Report(new() { Status = ex.Code == "hold_confirmation_required"
                        ? "Not sent yet: this needs a larger hold. No credits charged."
                        : "Quote rejected. No credits charged.", Pending = false });
                    throw new HostedApiException(ex.Message, ex.StatusCode, ex.Code)
                    { SuggestedHoldCredits = ex.SuggestedHoldCredits, WithdrawnText = withdrawn };
                }
                if (quote.Id == Guid.Empty) throw new HostedApiException("The hosted quote id is missing.");
                quoteId = quote.Id;
                Update(persist, () =>
                {
                    hosted.QuoteId = quote.Id.ToString("D");
                    hosted.CallBoundCredits = quote.CallBound;
                    hosted.HoldCredits = quote.Cap;
                });
                progress?.Report(new() { Status = QuoteStatus(quote), Pending = true });
            }
            ct.ThrowIfCancellationRequested();
            // This checkpoint must succeed before a reservation can be sent.
            Update(persist, () => hosted.SubmissionAttempted = true);
            HostedTurn turn;
            try { turn = await client.CreateTurnAsync(quoteId, key, ct).ConfigureAwait(false); }
            catch (HostedApiException ex) when (NoTurnAdmitted(ex)
                && hosted.Submission is not null && hosted.UserMessageId is not null)
            {
                // agent_create looks for this key's turn first, under a lock, and rolls back whole when it refuses.
                // An expired quote or a refused reservation is therefore proof of no turn: withdraw the message.
                var code = ex.StatusCode == 410 ? "quote_expired" : ex.Code;
                var withdrawn = Withdraw(hosted, code, persist);
                progress?.Report(new() { Status = "Not sent. No credits charged.", Pending = false });
                throw new HostedApiException(ex.StatusCode == 410 ? HostedApiErrors.Describe(null, "quote_expired", null) : ex.Message,
                    ex.StatusCode, code) { WithdrawnText = withdrawn };
            }
            if (turn.Id == Guid.Empty) throw new HostedApiException("The hosted turn id is missing.");
            turnId = turn.Id;
            TryRecordTurn(hosted.IdempotencyKey, turn.Id);
            Update(persist, () => hosted.TurnId = turn.Id.ToString("D"));
        }

        // A crash can occur after the side effect but before its result checkpoint.
        foreach (var id in hosted.StartedToolIds.Where(id => !hosted.ToolResults.ContainsKey(id)).ToArray())
            RecordUncertainTool(id, persist);
        if (hosted.UserMessageId is null)
        {
            // Older clients acknowledged tool_request before execution and have no started marker.
            var start = Session.Messages.FindLastIndex(m => m.Role == AgentMessageRole.user
                && m.Blocks.Any(b => b.Kind == AgentContentBlock.KindText));
            var orphanIds = Session.Messages.Skip(Math.Max(0, start)).SelectMany(m => m.Blocks)
                .Where(b => b.Kind == AgentContentBlock.KindToolUse && b.Id is not null
                    && !hosted.ToolResults.ContainsKey(b.Id)).Select(b => b.Id!).ToArray();
            foreach (var id in orphanIds) RecordUncertainTool(id, persist);
        }
        if (hosted.CancelRequested)
        {
            await CancelKnownTurnAsync(client, turnId, persist, ct).ConfigureAwait(false);
            if (hosted.Terminal == true)
            {
                progress?.Report(new() { Status = TerminalStatus("canceled", hosted), Pending = false });
                return;
            }
        }
        try
        {
            while (!await StreamUntilTerminalAsync(client, turnId, runner, gate, progress, persist, ct).ConfigureAwait(false))
                ct.ThrowIfCancellationRequested();
        }
        catch (HostedApiException) when (hosted.RecoveryError is not null && !ct.IsCancellationRequested)
        {
            await CancelKnownTurnAsync(client, turnId, persist, ct).ConfigureAwait(false);
            throw new HostedApiException(hosted.RecoveryError);
        }
    }

    private async Task CancelKnownTurnAsync(HostedAgentClient client, Guid id, Action? persist, CancellationToken ct)
    {
        ValidateAccount(client, Session.Hosted!);
        var turn = await client.CancelTurnAsync(id, ct).ConfigureAwait(false);
        Update(persist, () =>
        {
            if (turn.Status is "succeeded" or "failed" or "canceled" or "cancelled" or "expired")
            {
                Session.Hosted!.Terminal = true;
                Session.Hosted.LastChargeCredits = turn.Charge;
                Session.Hosted.UsageUnknown = turn.UsageUnknown;
                Session.Hosted.LastError = turn.Error;
                Session.Hosted.StopReason ??= turn.StopReason;
                AppendReport(turn.Status);
            }
        });
    }

    private async Task<bool> StreamUntilTerminalAsync(HostedAgentClient client, Guid turnId, IToolRunner runner, AgentToolGate gate,
        IProgress<HostedTurnProgress>? progress, Action? persist, CancellationToken ct)
    {
        var hosted = Session.Hosted!;
        // WP-431: made and saved before the first open, so a reconnect or a relaunch sends the same key and replaces only its
        // own predecessor; a different account, auth session or turn gets a new one.
        var scope = HostedAgentClient.StreamReaderScope(client.AccountId, client.SessionId(), turnId);
        if (!HostedAgentClient.ValidStreamReader(hosted.StreamReader) || hosted.StreamReaderScope != scope)
            Update(persist, () => { hosted.StreamReader = HostedAgentClient.NewStreamReader(); hosted.StreamReaderScope = scope; });
        await foreach (var evt in client.StreamEventsAsync(turnId, hosted.LastEventId, hosted.StreamReader, ct).ConfigureAwait(false))
        {
            ct.ThrowIfCancellationRequested();
            if (evt.Sequence <= hosted.LastEventId) continue;
            var reopenStream = evt.Kind == "tool_request" && !hosted.CancelRequested
                && await HandleToolRequestAsync(evt.Data, client, turnId, runner, gate, persist, ct).ConfigureAwait(false);
            string? visible = null, stage = null;
            Update(persist, () =>
            {
                if (evt.Kind == "report_started")
                {
                    // WP-254: work stopped; the model now writes a closing report with tools off. It is its own message.
                    hosted.ReportStarted = true;
                    hosted.StopReason = evt.Data?["reason"]?.GetValue<string>() ?? hosted.StopReason;
                    hosted.AssistantMessageId = null;
                    var effort = evt.Data?["effort"]?.GetValue<string>();
                    stage = "Stopping early: " + HostedApiErrors.Describe(null, hosted.StopReason, null)
                        + " Writing a short report with tools off" + (effort is { Length: > 0 } ? $" ({effort} effort)." : ".");
                }
                if (evt.Kind == "delta")
                {
                    var part = evt.Data?["text"]?.GetValue<string>();
                    if (!string.IsNullOrEmpty(part))
                    {
                        var assistant = Session.Messages.FirstOrDefault(m => m.Id == hosted.AssistantMessageId);
                        if (assistant is null)
                        {
                            assistant = AgentMessage.Assistant(AgentContentBlock.FromText(""));
                            Session.Messages.Add(assistant);
                            hosted.AssistantMessageId = assistant.Id;
                        }
                        assistant.Blocks[0].Text += part;
                        visible = assistant.Blocks[0].Text;
                    }
                }
                if (evt.Kind == "tool_request") hosted.AssistantMessageId = null;
                if (evt.Kind == "terminal")
                {
                    hosted.Terminal = true;
                    hosted.LastChargeCredits = ReadInt64(evt.Data, "charge");
                    hosted.LastError = evt.Data?["error"]?.GetValue<string>();
                    hosted.UsageUnknown = evt.Data?["usageUnknown"]?.GetValue<bool>() ?? false;
                    hosted.StopReason = evt.Data?["stopReason"]?.GetValue<string>() ?? hosted.StopReason;
                    AppendReport(evt.Data?["status"]?.GetValue<string>() ?? "succeeded");
                }
                // Acknowledge only alongside applied text/results. A failed submit leaves this event replayable.
                hosted.LastEventId = evt.Sequence;
            });
            progress?.Report(new()
            {
                VisibleText = visible,
                Status = evt.Kind == "terminal" ? TerminalStatus(evt.Data?["status"]?.GetValue<string>() ?? "succeeded", hosted) : stage,
                Pending = hosted.Terminal != true,
                Charge = hosted.LastChargeCredits,
            });
            if (hosted.Terminal == true) return true;
            // A recovered POST can leave the original SSE connection stale. Resume at the saved acknowledgement.
            if (reopenStream) return false;
        }
        if (hosted.Terminal == true) return true;
        throw new HostedApiException("Connection ended. This turn is still pending; reconnect to recover it without another reservation.");
    }

    private async Task<bool> HandleToolRequestAsync(JsonNode? data, HostedAgentClient client, Guid turnId,
        IToolRunner runner, AgentToolGate gate, Action? persist, CancellationToken ct)
    {
        var hosted = Session.Hosted!;
        var calls = data as JsonArray ?? data?["tool_calls"] as JsonArray ?? data?["toolCalls"] as JsonArray;
        if (calls is null || calls.Count == 0) throw new HostedApiException("The hosted tool request is invalid.");
        var parsed = calls.Select(node =>
        {
            var id = node?["id"]?.GetValue<string>();
            var name = (node?["function"]?["name"] ?? node?["name"])?.GetValue<string>();
            var args = node?["function"]?["arguments"] ?? node?["arguments"];
            var json = args is JsonValue value && value.TryGetValue<string>(out var str) ? str : args?.ToJsonString() ?? "{}";
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(name))
                throw new HostedApiException("The hosted tool request is invalid.");
            return (Id: id, Name: name, Json: json);
        }).ToArray();
        if (parsed.Select(c => c.Id).Distinct(StringComparer.Ordinal).Count() != parsed.Length)
            throw new HostedApiException("The hosted tool request contains duplicate ids.");
        var results = new List<HostedToolResult>();
        foreach (var call in parsed)
        {
            ct.ThrowIfCancellationRequested();
            if (!hosted.ToolResults.TryGetValue(call.Id, out var content))
            {
                // Legacy sessions can contain a tool-use checkpoint without the new started marker.
                var alreadyStarted = hosted.StartedToolIds.Contains(call.Id)
                    || (hosted.UserMessageId is null && Session.Messages.SelectMany(m => m.Blocks)
                        .Any(b => b.Kind == AgentContentBlock.KindToolUse && b.Id == call.Id));
                if (alreadyStarted)
                {
                    RecordUncertainTool(call.Id, persist);
                    throw new HostedApiException(hosted.RecoveryError!);
                }
                // WP-400: decided before the started marker, so a call that never ran is never "uncertain". A refusal
                // is a saved result like any other: a reconnect resubmits it and asks nothing again.
                var admission = await gate.AdmitAsync(call.Name, call.Json, hosted.Submission?.Tools ?? [], ct).ConfigureAwait(false);
                if (!admission.Allowed)
                {
                    var refusal = admission.Refusal!;
                    Update(persist, () =>
                    {
                        Session.Messages.Add(AgentMessage.Assistant(AgentContentBlock.ToolUse(call.Id, call.Name, call.Json)));
                        hosted.ExecutedToolIds.Add(call.Id);
                        hosted.ToolResults[call.Id] = refusal;
                        Session.Messages.Add(new AgentMessage
                        {
                            Role = AgentMessageRole.user,
                            Blocks = [AgentContentBlock.ToolResult(call.Id, [AgentToolResultBlock.FromText(refusal)], true)],
                        });
                    });
                    results.Add(new() { Id = call.Id, Content = refusal });
                    continue;
                }
                Update(persist, () =>
                {
                    hosted.StartedToolIds.Add(call.Id);
                    Session.Messages.Add(AgentMessage.Assistant(AgentContentBlock.ToolUse(call.Id, call.Name, call.Json)));
                });
                AgentToolExecutionResult executed;
                try { executed = await runner.ExecuteAsync(admission.Name, admission.InputJson, ct).ConfigureAwait(false); }
                catch (Exception)
                {
                    RecordUncertainTool(call.Id, persist);
                    throw new HostedApiException(hosted.RecoveryError!);
                }
                // WP-436: the text blocks; images travel beside it (HostedImages.Compose) and no longer leave empty lines.
                content = string.Join("\n", executed.Content.Where(block => block.Kind != AgentToolResultBlock.KindImage).Select(block => block.Text ?? ""));
                var saved = content;
                Update(persist, () =>
                {
                    hosted.ExecutedToolIds.Add(call.Id);
                    hosted.ToolResults[call.Id] = saved;
                    Session.Messages.Add(new AgentMessage
                    {
                        Role = AgentMessageRole.user,
                        Blocks = [AgentContentBlock.ToolResult(call.Id, executed.Content, executed.IsError)],
                    });
                });
            }
            results.Add(new() { Id = call.Id, Content = content });
        }
        // Retry after a failed disk write must also checkpoint before reusing in-memory results.
        Update(persist, () => { });
        // WP-436: each result's images come from its saved tool-result block, so a resubmission is byte-identical.
        List<HostedToolResult> submitted;
        lock (Session.SyncRoot)
            submitted = HostedImages.Compose(results, id => Session.Messages.SelectMany(m => m.Blocks)
                .LastOrDefault(b => b.Kind == AgentContentBlock.KindToolResult && b.ToolUseId == id)?.Content, hosted.ToolImages);
        return await client.SubmitToolResultsWithRecoveryAsync(turnId, submitted, ct).ConfigureAwait(false);
    }

    private void RecordUncertainTool(string id, Action? persist) => Update(persist, () =>
    {
        var hosted = Session.Hosted!;
        hosted.RecoveryError = "A local tool may have changed the project, but its result could not be confirmed. It will not be repeated. Check the project before continuing; this hosted turn is being cancelled.";
        hosted.CancelRequested = true;
        if (hosted.ToolResults.ContainsKey(id)) return;
        hosted.ToolResults[id] = hosted.RecoveryError;
        Session.Messages.Add(new AgentMessage
        {
            Role = AgentMessageRole.user,
            Blocks = [AgentContentBlock.ToolResult(id, [AgentToolResultBlock.FromText(hosted.RecoveryError)], true)],
        });
    });

    // WP-254: when a turn ends without a finished answer or the model's own report, Temno states what it
    // verifiably did. Called inside Update, once per turn; a clean finish and an untouched cancel add nothing.
    private void AppendReport(string? status)
    {
        var hosted = Session.Hosted!;
        if (hosted.ReportMessageId is not null) return;
        if (status == "succeeded" && hosted.RecoveryError is null && (hosted.StopReason is null || hosted.ReportStarted)) return;
        if (status is "canceled" or "cancelled" && hosted.RecoveryError is null && HostedTurnReport.Steps(Session).Count == 0) return;
        var report = AgentMessage.Assistant(AgentContentBlock.FromText(HostedTurnReport.Build(Session, status)));
        report.Origin = HostedTurnReport.Origin;
        Session.Messages.Add(report);
        hosted.ReportMessageId = report.Id;
        hosted.AssistantMessageId = null;
    }

    private void Update(Action? persist, Action mutation)
    {
        string? settled = null;
        lock (Session.SyncRoot)
        {
            mutation();
            Session.UpdatedAt = DateTimeOffset.UtcNow;
            persist?.Invoke();
            if (Session.Hosted is { Terminal: true, IdempotencyKey: { } key } && key != _forgotten)
                settled = key;
        }
        if (settled is null) return;
        // WP-400: a settled or withdrawn turn vouches for nothing, so a chat edited to look unfinished needs a click.
        _forgotten = settled;
        try { HostedTurnOrigins.Forget(settled); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
    private string? _forgotten;

    // WP-400: a turn that names no account is foreign, never anyone's to recover.
    private static void ValidateAccount(HostedAgentClient client, HostedChatMetadata hosted)
    {
        if (hosted.AccountId is not { Length: > 0 } owner)
            throw new HostedApiException("This pending turn names no account, so Temno will not recover it. Discard it to continue.");
        if (!string.Equals(owner, client.AccountId, StringComparison.Ordinal))
            throw new HostedApiException("This pending turn belongs to another account. Sign in to that account to recover or cancel it.");
    }

    // The agent gateway sends 410 "expired" for an expired quote (TA410); "quote_expired" is the image and media
    // spelling, accepted as well. 402 is a refused credit reservation.
    private static bool NoTurnAdmitted(HostedApiException ex) =>
        ex.StatusCode == 410 && ex.Code is "expired" or "quote_expired" || ex.StatusCode == 402;

    // Ends a submission that never reached the provider: nothing charged, and its text leaves the chat so the
    // caller can put it back in the composer. Returns that text.
    private string? Withdraw(HostedChatMetadata hosted, string? code, Action? persist)
    {
        string? text = null;
        Update(persist, () =>
        {
            text = Session.Messages.FirstOrDefault(m => m.Id == hosted.UserMessageId)?.Blocks
                .FirstOrDefault(b => b.Kind == AgentContentBlock.KindText)?.Text;
            hosted.Terminal = true;
            hosted.LastChargeCredits = 0;
            hosted.LastError = code;
            Session.Messages.RemoveAll(m => m.Id == hosted.UserMessageId);
        });
        return text;
    }

    private static long? ReadInt64(JsonNode? data, string name) =>
        data?[name] is JsonValue value && value.TryGetValue<long>(out var number) ? number : null;

    private static string Returned(long? held, long charge) =>
        held is { } h && h > charge ? $" {HostedApiErrors.Credits(h - charge)} held credits returned." : "";

    // WP-254: the likely charge and what is held. A gateway without estimated holds still gets the step-bound text.
    public static string QuoteStatus(HostedQuote quote)
    {
        if (quote.Estimate is not { } estimate)
            return $"Quoted up to {quote.CallBound} credits this call. Cap {quote.Cap}. Unused credits return at settlement.";
        if (estimate.LikelyLowCredits is not { } low || estimate.LikelyHighCredits is not { } high || estimate.HoldLimitedBy is not { } limitedBy)
            return $"Quoted. First step up to {quote.CallBound} credits, typically about {estimate.TypicalCallCredits}. Cap {quote.Cap}, "
                + $"with {estimate.ReportReserveCredits} kept back so the turn can report if it stops early. Unused credits return at settlement.";
        var held = HostedApiErrors.Credits(quote.Cap);
        return $"Estimated {HostedEstimate.Range(low, high)} credits. " + limitedBy switch
        {
            "balance" => estimate.AvailableCredits is { } available && available > quote.Cap
                ? $"Holding {held} of your {HostedApiErrors.Credits(available)} credits, so {HostedApiErrors.Credits(available - quote.Cap)} stay free for other jobs; it may stop early."
                : $"Holding {held}, your whole available balance; it may stop early. Other hosted jobs need more credits until it finishes.",
            "policy" => $"Up to {held} held, the most Temno holds without asking; it may stop early. Unused credits return when it finishes.",
            _ => $"Up to {held} held while it works; unused credits return when it finishes.",
        };
    }

    /// <summary>Throws before anything is sent when the gateway predates estimated holds (or was rolled back).</summary>
    public static void RequireEstimatedHolds(HostedAgentCatalog? catalog)
    {
        if (catalog?.BillingPolicy?.EstimatedHolds != true)
            throw new HostedApiException(HostedApiErrors.Describe(null, "hold_unsupported", null), null, "hold_unsupported");
    }

    private static string TerminalStatus(string status, HostedChatMetadata hosted)
    {
        var charged = hosted.LastChargeCredits is { } charge
            ? charge > 0 ? $" Final charge {charge} credits." + Returned(hosted.HoldCredits, charge) : " No credits charged."
            : " Final charge is pending.";
        if (hosted.LastError == "worker_lost" && hosted.LastChargeCredits == 0)
            return "The service restarted before this reply finished. No credits charged. You can retry; only the new reply will be charged."
                + (hosted.HoldCredits is { } held ? Returned(held, 0) : "");
        if (hosted.UsageUnknown) charged += " Final usage is still reconciling.";
        if (hosted.RecoveryError is { Length: > 0 } recovery) return recovery + charged;
        var report = hosted.ReportMessageId is not null ? " Temno's report of the verified steps is above." : "";
        if (status == "succeeded" && hosted.StopReason is { Length: > 0 } stopped)
            return "Stopped early: " + HostedApiErrors.Describe(null, stopped, null) + " The model's report is above; tool results are the record." + charged;
        if (!string.IsNullOrWhiteSpace(hosted.LastError) && hosted.LastError != "user_cancel")
            return HostedApiErrors.Describe(null, hosted.LastError, null) + report + charged;
        return (status is "canceled" or "cancelled" ? "Cancelled. Credits already used can still be charged." : "Turn settled.") + charged;
    }
}
public static class HostedHistory
{
    /// <summary>
    /// WP-308: the hosted instructions for a turn offered <paramref name="tools"/>, as the quote's system message.
    /// WP-309: with the skill index as it is now (<see cref="SkillLibrary"/>), so the estimate and the quote count it.
    /// </summary>
    public static JsonObject InstructionsMessage(IEnumerable<string> tools) =>
        InstructionsMessage(tools, SkillLibrary.IndexSection());

    public static JsonObject InstructionsMessage(IEnumerable<string> tools, string? skills) =>
        new() { ["role"] = "system", ["content"] = AgentInstructions.Hosted(tools, skills) };

    /// <summary>
    /// WP-308: the quote as sent, the hosted instructions first and then the frozen history. It is built
    /// at each send and never saved, so an updated prompt reaches old conversations and the chat file
    /// holds no copy. A system message already in the history is dropped, so the quote carries exactly one.
    /// </summary>
    public static HostedQuoteRequest WithInstructions(HostedQuoteRequest frozen) =>
        WithInstructions(frozen, SkillLibrary.IndexSection());

    public static HostedQuoteRequest WithInstructions(HostedQuoteRequest frozen, string? skills)
    {
        ArgumentNullException.ThrowIfNull(frozen);
        var messages = new JsonArray(InstructionsMessage(frozen.Tools, skills));
        foreach (var message in frozen.Messages)
            if (!(message is JsonObject m && m["role"] is JsonValue role && role.TryGetValue<string>(out var name) && name == "system"))
                messages.Add(message?.DeepClone());
        return new HostedQuoteRequest
        {
            Model = frozen.Model,
            Effort = frozen.Effort,
            ReasoningEnabled = frozen.ReasoningEnabled,
            MaxOutputTokens = frozen.MaxOutputTokens,
            SpendingCapCredits = frozen.SpendingCapCredits,
            Messages = messages,
            Tools = frozen.Tools,
        };
    }

    public static JsonArray ToQuoteMessages(IEnumerable<AgentMessage> messages) => ToQuoteMessages(messages, null);

    /// <summary>
    /// WP-436: with a <paramref name="images"/> policy (the turn's model takes images), tool messages carry the newest
    /// images their tools returned (<see cref="HostedImages.AttachToHistory"/>).
    /// </summary>
    public static JsonArray ToQuoteMessages(IEnumerable<AgentMessage> messages, HostedImagePolicy? images)
    {
        var array = new JsonArray();
        var toolBlocks = new Dictionary<JsonObject, IReadOnlyList<AgentToolResultBlock>>(ReferenceEqualityComparer.Instance);
        foreach (var message in messages)
        {
            // Temno-authored text (a turn report) was never said by the model; it is not model history. A system message
            // is never shown, so it is never sent either (WP-400; AnthropicClient.BuildMessages skips it the same way).
            if (message.Origin is not null || message.Role == AgentMessageRole.system)
                continue;
            if (message.Blocks.Exists(b => b.Kind == AgentContentBlock.KindToolUse))
            {
                var calls = new JsonArray();
                var text = new List<string>();
                foreach (var block in message.Blocks)
                {
                    if (block.Kind == AgentContentBlock.KindText && !string.IsNullOrWhiteSpace(block.Text))
                        text.Add(block.Text);
                    if (block.Kind == AgentContentBlock.KindToolUse && !string.IsNullOrWhiteSpace(block.Id))
                    {
                        calls.Add(new JsonObject
                        {
                            ["id"] = block.Id,
                            ["type"] = "function",
                            ["function"] = new JsonObject
                            {
                                ["name"] = block.Name ?? "",
                                ["arguments"] = block.Input ?? "{}",
                            },
                        });
                    }
                }

                var assistant = new JsonObject { ["role"] = "assistant" };
                if (text.Count > 0)
                    assistant["content"] = string.Join("\n", text);
                if (calls.Count > 0)
                    assistant["tool_calls"] = calls;
                array.Add(assistant);
                continue;
            }

            if (message.Blocks.Exists(b => b.Kind == AgentContentBlock.KindToolResult))
            {
                foreach (var block in message.Blocks.Where(b => b.Kind == AgentContentBlock.KindToolResult))
                {
                    var tool = new JsonObject
                    {
                        ["role"] = "tool",
                        ["tool_call_id"] = block.ToolUseId ?? "",
                        ["content"] = string.Join("\n", (block.Content ?? []).Where(c => c.Kind != AgentToolResultBlock.KindImage).Select(c => c.Text ?? "")),
                    };
                    array.Add(tool);
                    if (block.Content is { Count: > 0 } content) toolBlocks[tool] = content;
                }

                continue;
            }

            var body = string.Join("\n", message.Blocks
                .Where(b => b.Kind == AgentContentBlock.KindText)
                .Select(b => b.Text ?? ""));
            if (string.IsNullOrWhiteSpace(body))
                continue;
            array.Add(new JsonObject
            {
                ["role"] = message.Role == AgentMessageRole.assistant ? "assistant" : "user",
                ["content"] = body,
            });
        }

        if (images is not null) HostedImages.AttachToHistory(array, toolBlocks, images);
        return array;
    }
}
