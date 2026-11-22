using Temno.Core.Agent;
using Temno.Core.Project;
using Xunit;

namespace Temno.Core.Tests;

public sealed class ChatSessionLibraryTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 24, 12, 0, 0, TimeSpan.Zero);

    private static ChatSession Chat(string title, int minutes, params string[] userTexts) => new()
    {
        Title = title,
        UpdatedAt = T0.AddMinutes(minutes),
        Messages = [.. userTexts.Select(AgentMessage.UserText)],
    };

    private static ChatSession Empty(int minutes, string? model = null) => new()
    {
        UpdatedAt = T0.AddMinutes(minutes),
        Hosted = model is null ? null : new HostedChatMetadata { Model = model },
    };

    private static ChatSession Unresolved(string title, int minutes)
    {
        var chat = Chat(title, minutes, "cut the ums");
        chat.Hosted = new HostedChatMetadata { Model = "claude-sonnet-5", QuoteId = "q_1", Terminal = false };
        return chat;
    }

    [Fact]
    public void Open_UnresolvedHostedTurnOpensFirst()
    {
        var newer = Chat("Newer", 30, "hi");
        var pending = Unresolved("Pending", 10);

        var library = ChatSessionLibrary.Open([newer, pending], out var discarded);

        Assert.Same(pending, library.Current);
        Assert.Empty(discarded);
        Assert.False(library.CanLeaveCurrent);
    }

    [Fact]
    public void Open_NewestChatOpens_IncludingAnEmptyOneHoldingHostedChoices()
    {
        // WP-249's finding: choices made on a chat with no messages were saved but never reloaded.
        var older = Chat("Older", 10, "hi");
        var emptyWithChoice = Empty(20, model: "claude-sonnet-5");

        var library = ChatSessionLibrary.Open([older, emptyWithChoice], out var discarded);

        Assert.Same(emptyWithChoice, library.Current);
        Assert.Equal("claude-sonnet-5", library.Current.Hosted!.Model);
        Assert.Empty(discarded);
        Assert.Equal([emptyWithChoice.Id, older.Id], library.Entries().Select(e => e.Id).ToArray());
    }

    [Fact]
    public void Open_DiscardsEveryOtherMessagelessChat()
    {
        var newest = Chat("Newest", 30, "hi");
        var stray1 = Empty(20, model: "claude-sonnet-5");
        var stray2 = Empty(5);

        var library = ChatSessionLibrary.Open([stray2, newest, stray1], out var discarded);

        Assert.Same(newest, library.Current);
        Assert.Equal(new[] { stray1.Id, stray2.Id }.Order(), discarded.Select(s => s.Id).Order());
        Assert.Equal([newest.Id], library.Entries().Select(e => e.Id).ToArray());
    }

    [Fact]
    public void Open_NoSavedChats_StartsAFreshChat()
    {
        var library = ChatSessionLibrary.Open([], out var discarded);

        Assert.Empty(discarded);
        var entry = Assert.Single(library.Entries());
        Assert.True(entry.IsCurrent);
        Assert.Equal(ChatSessionLibrary.NewChatTitle, entry.Title);
        Assert.False(library.CanStartNew);
    }

    [Fact]
    public void Open_TwoFilesWithOneId_KeepsTheNewerAndDiscardsNeither()
    {
        var id = Guid.NewGuid();
        var older = Chat("Old copy", 10, "hi");
        older.Id = id;
        var newer = Chat("New copy", 20, "hi", "again");
        newer.Id = id;

        var library = ChatSessionLibrary.Open([older, newer], out var discarded);

        Assert.Same(newer, library.Current);
        Assert.Empty(discarded);
        Assert.Single(library.Entries());
    }

    [Fact]
    public void StartNew_CarriesHostedChoices_NotTurnState()
    {
        var current = Chat("Tighten the VO", 10, "hi");
        current.Hosted = new HostedChatMetadata
        {
            Model = "claude-sonnet-5",
            Effort = "high",
            ReasoningEnabled = false,
            SpendingCapCredits = 40,
            TurnId = "turn_1",
            IdempotencyKey = "key",
            LastChargeCredits = 7,
            Terminal = true,
        };
        var library = ChatSessionLibrary.Open([current], out _);

        var moved = library.StartNew();

        Assert.NotNull(moved);
        Assert.Same(current, moved.Value.Left);
        Assert.False(moved.Value.DiscardLeft);
        var fresh = library.Current;
        Assert.NotEqual(current.Id, fresh.Id);
        Assert.Empty(fresh.Messages);
        Assert.Equal(ChatSessionLibrary.NewChatTitle, fresh.Title);
        Assert.Equal("claude-sonnet-5", fresh.Hosted!.Model);
        Assert.Equal("high", fresh.Hosted.Effort);
        Assert.False(fresh.Hosted.ReasoningEnabled);
        // WP-254: holds are estimated per request; a cap from an older desktop is not carried into a new chat.
        Assert.Null(fresh.Hosted.SpendingCapCredits);
        Assert.Null(fresh.Hosted.TurnId);
        Assert.Null(fresh.Hosted.IdempotencyKey);
        Assert.Null(fresh.Hosted.LastChargeCredits);
        Assert.Null(fresh.Hosted.Terminal);
        Assert.False(ChatSessionLibrary.HasUnresolvedTurn(fresh));
        Assert.Equal([fresh.Id, current.Id], library.Entries().Select(e => e.Id).ToArray());
    }

    [Fact]
    public void StartNew_OnAnEmptyChat_IsRefused()
    {
        var library = ChatSessionLibrary.Open([Chat("Old", 10, "hi")], out _);
        Assert.NotNull(library.StartNew());
        var fresh = library.Current;

        Assert.False(library.CanStartNew);
        Assert.Null(library.StartNew());
        Assert.Same(fresh, library.Current);
        Assert.Equal(2, library.Entries().Count);
    }

    [Fact]
    public void StartNew_WhileAHostedTurnIsUnresolved_IsRefused()
    {
        var pending = Unresolved("Pending", 10);
        var library = ChatSessionLibrary.Open([pending], out _);

        Assert.False(library.CanStartNew);
        Assert.Null(library.StartNew());
        Assert.Same(pending, library.Current);
    }

    [Fact]
    public void Select_OpensAnEarlierChat_AndKeepsTheOneLeft()
    {
        var newer = Chat("Newer", 30, "hi");
        var older = Chat("Older", 10, "hello");
        var library = ChatSessionLibrary.Open([newer, older], out _);

        var moved = library.Select(older.Id);

        Assert.NotNull(moved);
        Assert.Same(newer, moved.Value.Left);
        Assert.False(moved.Value.DiscardLeft);
        Assert.Same(older, library.Current);
        var entries = library.Entries();
        Assert.Equal([newer.Id, older.Id], entries.Select(e => e.Id).ToArray());
        Assert.Equal([false, true], entries.Select(e => e.IsCurrent).ToArray());
    }

    [Fact]
    public void Select_FromAnEmptyChat_DiscardsIt()
    {
        var older = Chat("Older", 10, "hello");
        var library = ChatSessionLibrary.Open([older], out _);
        library.StartNew();
        var fresh = library.Current;

        var moved = library.Select(older.Id);

        Assert.NotNull(moved);
        Assert.Same(fresh, moved.Value.Left);
        Assert.True(moved.Value.DiscardLeft);
        Assert.Equal([older.Id], library.Entries().Select(e => e.Id).ToArray());
    }

    [Fact]
    public void Select_WhileAHostedTurnIsUnresolved_IsRefused()
    {
        var pending = Unresolved("Pending", 30);
        var other = Chat("Other", 10, "hi");
        var library = ChatSessionLibrary.Open([pending, other], out _);

        Assert.Null(library.Select(other.Id));
        Assert.Same(pending, library.Current);
    }

    [Fact]
    public void Select_CurrentOrUnknownChat_IsANoOp()
    {
        var chat = Chat("Only", 10, "hi");
        var library = ChatSessionLibrary.Open([chat], out _);

        Assert.Null(library.Select(chat.Id));
        Assert.Null(library.Select(Guid.NewGuid()));
        Assert.Same(chat, library.Current);
    }

    [Fact]
    public void Delete_RemovesAnotherChat_ButNeverTheCurrentOrAnUnresolvedOne()
    {
        var current = Unresolved("Pending now", 40);
        var alsoPending = Unresolved("Pending before", 30);
        var plain = Chat("Plain", 10, "hi");
        var library = ChatSessionLibrary.Open([current, alsoPending, plain], out _);
        Assert.Same(current, library.Current);

        Assert.False(library.CanDelete(current.Id));
        Assert.Null(library.Delete(current.Id));
        Assert.False(library.CanDelete(alsoPending.Id));
        Assert.Null(library.Delete(alsoPending.Id));
        Assert.Null(library.Delete(Guid.NewGuid()));

        Assert.True(library.CanDelete(plain.Id));
        Assert.Same(plain, library.Delete(plain.Id));
        Assert.Equal([current.Id, alsoPending.Id], library.Entries().Select(e => e.Id).ToArray());
        Assert.Equal([false, false], library.Entries().Select(e => e.CanDelete).ToArray());
    }

    [Fact]
    public void Entries_NewestFirst_CurrentMarked_TitlesFilled()
    {
        var a = Chat("Alpha", 10, "hi");
        var b = Chat("  ", 20, "hi");
        var c = Chat("Gamma", 30, "hi");
        var library = ChatSessionLibrary.Open([a, b, c], out _);

        var entries = library.Entries();

        Assert.Equal(["Gamma", ChatSessionLibrary.NewChatTitle, "Alpha"], entries.Select(e => e.Title).ToArray());
        Assert.Equal([true, false, false], entries.Select(e => e.IsCurrent).ToArray());
        Assert.Equal([false, true, true], entries.Select(e => e.CanDelete).ToArray());
    }

    [Fact]
    public void ReplaceCurrent_AcceptsOnlyTheSameChat()
    {
        var chat = Chat("Only", 10, "hi");
        var library = ChatSessionLibrary.Open([chat], out _);
        var copy = ChatSessionStore.Snapshot(chat);

        library.ReplaceCurrent(copy);

        Assert.Same(copy, library.Current);
        Assert.Throws<ArgumentException>(() => library.ReplaceCurrent(new ChatSession()));
    }

    [Theory]
    [InlineData(-5, "Just now")]
    [InlineData(0, "Just now")]
    [InlineData(59, "Just now")]
    [InlineData(60, "1 min ago")]
    [InlineData(59 * 60, "59 min ago")]
    [InlineData(60 * 60, "1 hr ago")]
    [InlineData(23 * 3600 + 3599, "23 hr ago")]
    [InlineData(86400, "1 day ago")]
    [InlineData(6 * 86400, "6 days ago")]
    [InlineData(7 * 86400, "1 wk ago")]
    [InlineData(27 * 86400, "3 wk ago")]
    [InlineData(40 * 86400, "Aug 15")]
    [InlineData(400 * 86400, "Aug 20, 2025")]
    public void RelativeTime_MatchesTheMacShortStyle(int secondsAgo, string expected)
    {
        var now = new DateTimeOffset(2026, 9, 24, 12, 0, 0, TimeSpan.Zero);
        Assert.Equal(expected, ChatSessionLibrary.RelativeTime(now.AddSeconds(-secondsAgo), now));
    }

    [Fact]
    public void RelativeTime_DatesUseTheViewersOffset()
    {
        var now = new DateTimeOffset(2026, 9, 24, 8, 0, 0, TimeSpan.FromHours(-7));
        var when = new DateTimeOffset(2026, 8, 1, 3, 0, 0, TimeSpan.Zero); // Jul 31 at 20:00 at -07:00
        Assert.Equal("Jul 31", ChatSessionLibrary.RelativeTime(when, now));
    }

    [Fact]
    public void StoreDelete_RemovesEveryFileHoldingTheChat_AndNothingElse()
    {
        var package = Path.Combine(Path.GetTempPath(), "temno-chat-delete-" + Guid.NewGuid().ToString("N") + ".palmier");
        try
        {
            var doomed = Chat("Doomed", 10, "bye");
            var kept = Chat("Kept", 20, "stay");
            var dir = Path.Combine(package, ProjectConstants.ChatDirectoryName);
            Directory.CreateDirectory(dir);
            // Windows writes a lowercase id, the Mac an uppercase uuidString, a fixture any name.
            File.WriteAllBytes(Path.Combine(dir, doomed.Id.ToString("D") + ".json"), ChatSessionStore.Encode(doomed));
            File.WriteAllBytes(Path.Combine(dir, doomed.Id.ToString("D").ToUpperInvariant() + ".json"), ChatSessionStore.Encode(doomed));
            File.WriteAllBytes(Path.Combine(dir, "session-fixture.json"), ChatSessionStore.Encode(doomed));
            File.WriteAllText(Path.Combine(dir, "unreadable.json"), "{ not json");
            ChatSessionStore.Save(package, kept);

            ChatSessionStore.Delete(package, doomed.Id);

            var left = Directory.EnumerateFiles(dir).Select(f => Path.GetFileName(f)).Order().ToList();
            Assert.Equal(new[] { ChatSessionStore.FileName(kept), "unreadable.json" }.Order(), left);
            Assert.Equal(kept.Id, Assert.Single(ChatSessionStore.Load(package)).Id);
        }
        finally
        {
            if (Directory.Exists(package))
                Directory.Delete(package, recursive: true);
        }
    }

    [Fact]
    public void StoreDelete_WithoutAChatFolder_DoesNothing()
    {
        var package = Path.Combine(Path.GetTempPath(), "temno-chat-none-" + Guid.NewGuid().ToString("N") + ".palmier");
        ChatSessionStore.Delete(package, Guid.NewGuid());
        Assert.False(Directory.Exists(package));
    }

    [Fact]
    public void Reopen_RestoresTheHostedModelPickedOnAnEmptyChat()
    {
        var package = Path.Combine(Path.GetTempPath(), "temno-chat-reopen-" + Guid.NewGuid().ToString("N") + ".palmier");
        try
        {
            var earlier = Chat("Earlier", 10, "hi");
            ChatSessionStore.Save(package, earlier);
            var library = ChatSessionLibrary.Open(ChatSessionStore.Load(package), out _);
            library.StartNew();
            library.Current.Hosted!.Model = "muse-spark-1.3";
            ChatSessionStore.Save(package, library.Current);

            var reopened = ChatSessionLibrary.Open(ChatSessionStore.Load(package), out var discarded);

            Assert.Empty(discarded);
            Assert.Equal(library.Current.Id, reopened.Current.Id);
            Assert.Equal("muse-spark-1.3", reopened.Current.Hosted!.Model);
            Assert.Equal([library.Current.Id, earlier.Id], reopened.Entries().Select(e => e.Id).ToArray());
        }
        finally
        {
            if (Directory.Exists(package))
                Directory.Delete(package, recursive: true);
        }
    }
}
