using Temno.Core.Account;

namespace Temno.Core.Agent;

/// <summary>
/// WP-254. What a hosted turn verifiably did, built only from Temno's own records: the tool calls it
/// started, the results it saved, and the settlement the gateway returned. Model text is never a source,
/// so the report stays true when the model ran out of output, returned nothing, or was cancelled.
/// </summary>
public static class HostedTurnReport
{
    public const string Origin = "temno";

    public enum StepState { Done, Failed, Unconfirmed }

    public enum Outcome { Completed, None, Partial, Uncertain }

    public sealed record Step(string Id, string Name, string Input, bool ReadOnly, StepState State);

    // Names that only read the project. Anything else is treated as an edit, so a change is never under-reported.
    public static bool IsReadOnly(string name) =>
        name.StartsWith("get_", StringComparison.Ordinal) || name.StartsWith("list_", StringComparison.Ordinal)
        || name.StartsWith("inspect_", StringComparison.Ordinal)
        || name is "search_media" or "validate_timeline" or "read_skill";

    public static IReadOnlyList<Step> Steps(ChatSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        var hosted = session.Hosted ?? new HostedChatMetadata();
        var messages = session.Messages;
        var start = hosted.UserMessageId is { } user ? messages.FindIndex(m => m.Id == user) : -1;
        if (start < 0)
            start = messages.FindLastIndex(m => m.Role == AgentMessageRole.user && m.Blocks.Any(b => b.Kind == AgentContentBlock.KindText));
        var errors = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var block in messages.Skip(start + 1).SelectMany(m => m.Blocks))
            if (block.Kind == AgentContentBlock.KindToolResult && block.ToolUseId is { } id)
                errors[id] = block.IsError == true;
        var steps = new List<Step>();
        foreach (var block in messages.Skip(start + 1).Where(m => m.Origin is null).SelectMany(m => m.Blocks))
        {
            if (block.Kind != AgentContentBlock.KindToolUse || block.Id is not { } id || steps.Exists(s => s.Id == id))
                continue;
            var name = block.Name ?? "tool";
            // Confirmed only when the result was saved after the tool returned; a started call without one is unresolved.
            var confirmed = hosted.ExecutedToolIds.Contains(id) && errors.ContainsKey(id);
            var state = !confirmed ? StepState.Unconfirmed : errors[id] ? StepState.Failed : StepState.Done;
            steps.Add(new Step(id, name, block.Input ?? "{}", IsReadOnly(name), state));
        }
        return steps;
    }

    public static Outcome Classify(IReadOnlyList<Step> steps, bool finished)
    {
        var edits = steps.Where(s => !s.ReadOnly).ToArray();
        if (edits.Any(s => s.State == StepState.Unconfirmed)) return Outcome.Uncertain;
        if (edits.Any(s => s.State == StepState.Done)) return finished ? Outcome.Completed : Outcome.Partial;
        return Outcome.None;
    }

    public static string Build(ChatSession session, string? status)
    {
        var hosted = session.Hosted ?? new HostedChatMetadata();
        var steps = Steps(session);
        var finished = status == "succeeded" && hosted.StopReason is null && hosted.RecoveryError is null;
        var lines = new List<string>
        {
            status switch
            {
                "canceled" or "cancelled" => "Temno report: this turn was cancelled.",
                "expired" => "Temno report: this turn ran out of time.",
                _ when finished => "Temno report: this turn finished.",
                _ => "Temno report: this turn stopped before the task was finished.",
            },
        };
        var reason = hosted.RecoveryError is not null ? null : hosted.StopReason ?? hosted.LastError;
        if (hosted.RecoveryError is { Length: > 0 } recovery) lines.Add("Why: " + recovery);
        else if (reason is { Length: > 0 } && reason != "user_cancel") lines.Add("Why: " + HostedApiErrors.Describe(null, reason, null));

        var edits = steps.Where(s => !s.ReadOnly).ToArray();
        int done = edits.Count(s => s.State == StepState.Done), failed = edits.Count(s => s.State == StepState.Failed),
            unconfirmed = edits.Count(s => s.State == StepState.Unconfirmed);
        lines.Add(Classify(steps, finished) switch
        {
            Outcome.Uncertain => $"Outcome uncertain: {Count(unconfirmed, "editing step")} may have changed the project but could not be confirmed. Check the project before continuing."
                                 + (done > 0 ? $" {Count(done, "other editing step")} completed." : ""),
            Outcome.Partial => $"Partly done: {Count(done, "editing step")} completed before the turn stopped.",
            Outcome.Completed => $"Done: {Count(done, "editing step")} completed.",
            _ when failed > 0 => $"No confirmed changes: {Count(failed, "editing step")} reported an error.",
            _ => "No project changes: no editing tool ran.",
        });
        AddSteps(lines, "Completed", edits.Where(s => s.State == StepState.Done));
        AddSteps(lines, "Reported an error", edits.Where(s => s.State == StepState.Failed));
        AddSteps(lines, "Not confirmed, check the project", edits.Where(s => s.State == StepState.Unconfirmed));
        var reads = steps.Where(s => s.ReadOnly).GroupBy(s => s.Name).Select(g => g.Count() > 1 ? $"{g.Key} ×{g.Count()}" : g.Key).ToArray();
        if (reads.Length > 0) lines.Add("Checked: " + string.Join(", ", reads) + ".");
        if (!finished)
            lines.Add("Not done: the rest of the request. Your message and any completed steps are kept; send a follow-up to continue.");
        lines.Add(Charges(hosted));
        return string.Join("\n", lines);
    }

    public static string Charges(HostedChatMetadata hosted)
    {
        var cap = (hosted.HoldCredits ?? hosted.SpendingCapCredits) is { } c ? $" (held {c})" : "";
        var text = hosted.LastChargeCredits switch
        {
            null => "Charges: the final charge is still pending.",
            > 0 and var charged => $"Charges: {charged} credits{cap}.",
            _ => "Charges: no credits charged.",
        };
        if (hosted.UsageUnknown) text += " Some usage could not be confirmed; a charge never exceeds the credits held.";
        return text;
    }

    private static void AddSteps(List<string> lines, string label, IEnumerable<Step> steps)
    {
        var items = steps.Select(s => s.Name + " " + Shorten(s.Input)).ToArray();
        if (items.Length > 0) lines.Add(label + ": " + string.Join("; ", items) + ".");
    }

    private static string Shorten(string input)
    {
        var flat = input.ReplaceLineEndings(" ").Trim();
        return flat.Length <= 80 ? flat : flat[..77] + "…";
    }

    private static string Count(int n, string noun) => n == 1 ? "1 " + noun : $"{n} {noun}s";
}
