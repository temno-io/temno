using Temno.Core.Agent.Skills;

namespace Temno.Core.Agent;

public interface IToolRunner
{
    Task<AgentToolExecutionResult> ExecuteAsync(
        string name,
        string inputJson,
        CancellationToken cancellationToken);
}

public readonly record struct AgentToolExecutionResult(
    IReadOnlyList<AgentToolResultBlock> Content,
    bool IsError)
{
    public static AgentToolExecutionResult Ok(string text) =>
        new([AgentToolResultBlock.FromText(text)], false);

    public static AgentToolExecutionResult Error(string message) =>
        new([AgentToolResultBlock.FromText(message)], true);
}

public sealed class AgentConversation
{
    public const int DefaultMaxIterations = 8;

    public AgentConversation(ChatSession? session = null)
    {
        Session = session ?? new ChatSession();
    }

    public ChatSession Session { get; }

    /// <param name="gate">
    /// WP-400: decides each model tool call. Without one, only the offered tools run and anything that needs the
    /// user's approval (an export, a file outside the project, a download, a paid generation) is refused.
    /// </param>
    public async Task SendUserAsync(
        string text,
        AnthropicClient client,
        IReadOnlyList<AnthropicToolSchema> tools,
        IToolRunner toolRunner,
        string? systemPrompt = null,
        int maxIterations = DefaultMaxIterations,
        CancellationToken cancellationToken = default,
        AgentToolGate? gate = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(tools);
        ArgumentNullException.ThrowIfNull(toolRunner);
        var offered = tools.Select(t => t.Name).ToHashSet(StringComparer.Ordinal);
        gate ??= new AgentToolGate(offered);

        if (!client.HasApiKey)
        {
            throw new AnthropicClientException(
                AnthropicClientErrorKind.MissingApiKey,
                AgentSetup.MissingKeyError);
        }

        var trimmed = text.Trim();
        if (trimmed.Length == 0)
            return;

        Session.Messages.Add(AgentMessage.UserText(trimmed));
        TouchTitle(trimmed);
        TouchUpdated();

        // WP-309: the skill index is read as each message is sent, and only offered with the tool that reads a skill.
        var system = string.IsNullOrWhiteSpace(systemPrompt)
            ? AgentInstructions.WithSkills(AgentInstructions.InApp,
                tools.Any(t => t.Name == SkillIndex.Tool) ? SkillLibrary.IndexSection() : null)
            : systemPrompt;
        var cap = Math.Max(1, maxIterations);

        try
        {
            for (var i = 0; i < cap; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var turn = await client.CompleteAsync(system, tools, Session.Messages, cancellationToken)
                    .ConfigureAwait(false);

                var assistant = AgentMessage.Assistant([.. turn.Blocks]);
                Session.Messages.Add(assistant);
                TouchUpdated();

                if (!string.Equals(turn.StopReason, "tool_use", StringComparison.Ordinal)
                    && !assistant.Blocks.Exists(b => b.Kind == AgentContentBlock.KindToolUse))
                {
                    break;
                }

                var toolUses = assistant.Blocks
                    .Where(b => b.Kind == AgentContentBlock.KindToolUse)
                    .ToList();
                if (toolUses.Count == 0)
                    break;

                var results = new List<AgentContentBlock>();
                try
                {
                    foreach (var use in toolUses)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var name = use.Name ?? "";
                        var input = string.IsNullOrWhiteSpace(use.Input) ? "{}" : use.Input;
                        AgentToolExecutionResult executed;
                        try
                        {
                            // The execution point: an unoffered tool, or a call the user did not allow, never runs.
                            var admission = await gate.AdmitAsync(name, input, offered, cancellationToken)
                                .ConfigureAwait(false);
                            executed = admission.Allowed
                                ? await toolRunner.ExecuteAsync(admission.Name, admission.InputJson, cancellationToken)
                                    .ConfigureAwait(false)
                                : AgentToolExecutionResult.Error(admission.Refusal!);
                        }
                        catch (OperationCanceledException)
                        {
                            throw;
                        }
                        catch (Exception ex)
                        {
                            executed = AgentToolExecutionResult.Error(ex.Message);
                        }

                        results.Add(AgentContentBlock.ToolResult(
                            use.Id ?? "",
                            executed.Content,
                            executed.IsError));
                    }
                }
                catch (OperationCanceledException)
                {
                    if (results.Count > 0)
                    {
                        Session.Messages.Add(new AgentMessage
                        {
                            Role = AgentMessageRole.user,
                            Blocks = results,
                        });
                        TouchUpdated();
                    }

                    throw;
                }

                Session.Messages.Add(new AgentMessage
                {
                    Role = AgentMessageRole.user,
                    Blocks = results,
                });
                TouchUpdated();
            }
        }
        catch (OperationCanceledException)
        {
            ResolveOrphanToolUses("Cancelled");
            throw;
        }
    }

    internal static void ResolveOrphanToolUses(ChatSession session, string reason = "Cancelled")
    {
        ArgumentNullException.ThrowIfNull(session);
        for (var i = 0; i < session.Messages.Count; i++)
        {
            var message = session.Messages[i];
            if (message.Role != AgentMessageRole.assistant)
                continue;

            var toolUseIds = message.Blocks
                .Where(b => b.Kind == AgentContentBlock.KindToolUse)
                .Select(b => b.Id ?? "")
                .Where(id => id.Length > 0)
                .ToList();
            if (toolUseIds.Count == 0)
                continue;

            var next = NextNonSystemIndex(session, i);
            var nextIsToolResult = next < session.Messages.Count
                && session.Messages[next].Role == AgentMessageRole.user
                && session.Messages[next].Blocks.Exists(b => b.Kind == AgentContentBlock.KindToolResult);
            var resolved = nextIsToolResult
                ? session.Messages[next].Blocks
                    .Where(b => b.Kind == AgentContentBlock.KindToolResult)
                    .Select(b => b.ToolUseId ?? "")
                    .ToHashSet(StringComparer.Ordinal)
                : new HashSet<string>(StringComparer.Ordinal);

            var orphans = toolUseIds.Where(id => !resolved.Contains(id)).ToList();
            if (orphans.Count == 0)
                continue;

            var synthetic = orphans
                .Select(id => AgentContentBlock.ToolResult(
                    id,
                    [AgentToolResultBlock.FromText(reason)],
                    isError: true))
                .ToList();
            if (nextIsToolResult)
                session.Messages[next].Blocks.InsertRange(0, synthetic);
            else
                session.Messages.Insert(next, new AgentMessage
                {
                    Role = AgentMessageRole.user,
                    Blocks = synthetic,
                });
        }
    }

    private void ResolveOrphanToolUses(string reason) =>
        ResolveOrphanToolUses(Session, reason);

    private static int NextNonSystemIndex(ChatSession session, int after)
    {
        var i = after + 1;
        while (i < session.Messages.Count && session.Messages[i].Role == AgentMessageRole.system)
            i++;
        return i;
    }

    private void TouchTitle(string userText)
    {
        if (Session.Title == "New chat")
        {
            var line = userText.ReplaceLineEndings(" ").Trim();
            Session.Title = line.Length <= 40 ? line : line[..40];
        }
    }

    private void TouchUpdated() => Session.UpdatedAt = DateTimeOffset.UtcNow;
}
