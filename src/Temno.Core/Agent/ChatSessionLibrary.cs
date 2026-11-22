using System.Globalization;

namespace Temno.Core.Agent;

/// <summary>One row of the agent panel's Chat history list.</summary>
public sealed record ChatHistoryEntry(Guid Id, string Title, DateTimeOffset UpdatedAt, bool IsCurrent, bool CanDelete);

/// <summary>A move away from the current chat. An empty chat is discarded, never kept in history.</summary>
public readonly record struct ChatSwitch(ChatSession Left, bool DiscardLeft);

/// <summary>
/// A project's chats: the one in the panel and the rest of its history. Mac parity with
/// <c>AgentService.newChat</c>, <c>selectSession</c> and <c>deleteSession</c>. The hosted guard is
/// Windows-only: a chat with an unresolved hosted turn is never left, so the turn is recovered or
/// cancelled where it started instead of being orphaned.
/// </summary>
public sealed class ChatSessionLibrary
{
    public const string NewChatTitle = "New chat";

    private readonly List<ChatSession> _others;

    private ChatSessionLibrary(ChatSession current, List<ChatSession> others)
    {
        Current = current;
        _others = others;
    }

    public ChatSession Current { get; private set; }

    public bool CanLeaveCurrent => !HasUnresolvedTurn(Current);

    /// <summary>An empty chat is already a new chat; starting another would only duplicate it.</summary>
    public bool CanStartNew => CanLeaveCurrent && !IsEmpty(Current);

    /// <summary>
    /// A chat with an unresolved hosted turn opens first, so it is recovered. Otherwise the most
    /// recently updated chat opens, including a message-less one holding hosted choices. Every other
    /// message-less chat is returned in <paramref name="discarded"/>: the Mac never keeps one.
    /// </summary>
    public static ChatSessionLibrary Open(IEnumerable<ChatSession> saved, out IReadOnlyList<ChatSession> discarded)
    {
        ArgumentNullException.ThrowIfNull(saved);
        // Two files can hold one id (Mac and Windows case their file names differently); keep the newer.
        var newestFirst = saved
            .GroupBy(s => s.Id)
            .Select(g => g.OrderByDescending(UpdatedAt).First())
            .OrderByDescending(UpdatedAt)
            .ToList();
        var current = newestFirst.FirstOrDefault(HasUnresolvedTurn)
            ?? newestFirst.FirstOrDefault()
            ?? new ChatSession();
        var drop = newestFirst
            .Where(s => !ReferenceEquals(s, current) && IsEmpty(s) && !HasUnresolvedTurn(s))
            .ToList();
        discarded = drop;
        return new ChatSessionLibrary(
            current,
            newestFirst.Where(s => !ReferenceEquals(s, current) && !drop.Contains(s)).ToList());
    }

    /// <summary>A BYOK send works on a copy and hands the same chat back as a new object.</summary>
    public void ReplaceCurrent(ChatSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (session.Id != Current.Id)
            throw new ArgumentException("Only the current chat can be replaced.", nameof(session));
        Current = session;
    }

    /// <summary>Starts an empty chat that keeps the current hosted choices. Null when refused.</summary>
    public ChatSwitch? StartNew()
    {
        if (!CanStartNew) return null;
        var left = Current;
        _others.Add(left);
        Current = NewFrom(left);
        return new ChatSwitch(left, DiscardLeft: false);
    }

    /// <summary>Opens an earlier chat. Null when refused, unknown, or already open.</summary>
    public ChatSwitch? Select(Guid id)
    {
        if (id == Current.Id || !CanLeaveCurrent) return null;
        var target = _others.FirstOrDefault(s => s.Id == id);
        if (target is null) return null;
        _others.Remove(target);
        var left = Current;
        var discard = IsEmpty(left);
        if (!discard) _others.Add(left);
        Current = target;
        return new ChatSwitch(left, discard);
    }

    public bool CanDelete(Guid id) =>
        id != Current.Id && _others.FirstOrDefault(s => s.Id == id) is { } session && !HasUnresolvedTurn(session);

    /// <summary>Removes a chat from history. The current chat and an unresolved one are refused.</summary>
    public ChatSession? Delete(Guid id)
    {
        if (!CanDelete(id)) return null;
        var target = _others.First(s => s.Id == id);
        _others.Remove(target);
        return target;
    }

    /// <summary>Every chat, newest first, with the current one marked.</summary>
    public IReadOnlyList<ChatHistoryEntry> Entries() =>
        _others.Append(Current)
            .Select(ToEntry)
            .OrderByDescending(e => e.UpdatedAt)
            .ToList();

    public static bool IsEmpty(ChatSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        lock (session.SyncRoot)
            return session.Messages.Count == 0;
    }

    // One definition of an unresolved hosted turn: the conversation's own.
    public static bool HasUnresolvedTurn(ChatSession session) =>
        new HostedAgentConversation(session).HasPendingTurn;

    /// <summary>A new chat keeps the hosted model, effort and reasoning, never turn state or a hold.</summary>
    public static ChatSession NewFrom(ChatSession previous)
    {
        ArgumentNullException.ThrowIfNull(previous);
        lock (previous.SyncRoot)
        {
            var hosted = previous.Hosted;
            return new ChatSession
            {
                Title = NewChatTitle,
                Hosted = new HostedChatMetadata
                {
                    Model = hosted?.Model,
                    Effort = hosted?.Effort,
                    ReasoningEnabled = hosted?.ReasoningEnabled,
                },
            };
        }
    }

    /// <summary>
    /// The Mac's short relative style, in English: "Just now", "5 min ago", "3 hr ago", "2 days ago",
    /// "3 wk ago", then the date in <paramref name="now"/>'s offset.
    /// </summary>
    public static string RelativeTime(DateTimeOffset when, DateTimeOffset now)
    {
        var elapsed = now - when;
        if (elapsed < TimeSpan.FromMinutes(1)) return "Just now";
        if (elapsed < TimeSpan.FromHours(1)) return $"{(int)elapsed.TotalMinutes} min ago";
        if (elapsed < TimeSpan.FromDays(1)) return $"{(int)elapsed.TotalHours} hr ago";
        if (elapsed < TimeSpan.FromDays(7))
        {
            var days = (int)elapsed.TotalDays;
            return days == 1 ? "1 day ago" : $"{days} days ago";
        }
        if (elapsed < TimeSpan.FromDays(28)) return $"{(int)(elapsed.TotalDays / 7)} wk ago";
        var local = when.ToOffset(now.Offset);
        return local.ToString(local.Year == now.Year ? "MMM d" : "MMM d, yyyy", CultureInfo.InvariantCulture);
    }

    private ChatHistoryEntry ToEntry(ChatSession session)
    {
        lock (session.SyncRoot)
        {
            var current = ReferenceEquals(session, Current);
            var title = string.IsNullOrWhiteSpace(session.Title) ? NewChatTitle : session.Title.Trim();
            return new ChatHistoryEntry(session.Id, title, session.UpdatedAt, current,
                CanDelete: !current && !HasUnresolvedTurn(session));
        }
    }

    private static DateTimeOffset UpdatedAt(ChatSession session)
    {
        lock (session.SyncRoot)
            return session.UpdatedAt;
    }
}
