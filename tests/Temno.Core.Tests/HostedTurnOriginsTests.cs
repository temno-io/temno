using System.Text.Json.Nodes;
using Temno.Core.Agent;
using Temno.Core.IO;
using Xunit;

namespace Temno.Core.Tests;

/// <summary>WP-400: the per-user record of hosted turns this install created.</summary>
public sealed class HostedTurnOriginsTests : IDisposable
{
    private const string D = "digest-a";
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "wp400-origins-" + Guid.NewGuid().ToString("N"));
    private string File => Path.Combine(_folder, HostedTurnOrigins.FileName);

    public HostedTurnOriginsTests() => HostedTurnOrigins.SetPathForTests(File);

    public void Dispose()
    {
        HostedTurnOrigins.SetPathForTests(null);
        if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true);
    }

    private static string Key() => Guid.NewGuid().ToString("D");

    [Fact]
    public void ARecordedTurn_MatchesOnlyItsKeyAccountAndSubmission()
    {
        var key = Key();
        HostedTurnOrigins.Record(key, "account-a", D);
        Assert.True(HostedTurnOrigins.Matches(key, "account-a", D, null));
        Assert.True(HostedTurnOrigins.Matches(key.ToUpperInvariant(), "account-a", D, null));
        Assert.False(HostedTurnOrigins.Matches(key, "account-b", D, null));
        Assert.False(HostedTurnOrigins.Matches(key, "ACCOUNT-A", D, null));
        Assert.False(HostedTurnOrigins.Matches(key, "account-a", "digest-b", null));
        Assert.False(HostedTurnOrigins.Matches(Key(), "account-a", D, null));
    }

    // Before its turn id is recorded the record vouches for no turn id; after, for that one, or a chat still lagging it.
    [Fact]
    public void ARecordedTurnId_IsTheOnlyTurnIdThatMatches()
    {
        var key = Key();
        var turn = Guid.NewGuid();
        HostedTurnOrigins.Record(key, "a", D);
        Assert.False(HostedTurnOrigins.Matches(key, "a", D, turn.ToString("D")));
        HostedTurnOrigins.RecordTurn(key, turn);
        Assert.True(HostedTurnOrigins.Matches(key, "a", D, turn.ToString("D")));
        Assert.True(HostedTurnOrigins.Matches(key, "a", D, null));
        Assert.False(HostedTurnOrigins.Matches(key, "a", D, Guid.NewGuid().ToString("D")));
        Assert.False(HostedTurnOrigins.Matches(key, "a", D, "not-a-guid"));
    }

    [Fact]
    public void AForgottenTurn_MatchesNothing()
    {
        var key = Key();
        HostedTurnOrigins.Record(key, "a", D);
        HostedTurnOrigins.Forget(key);
        Assert.False(HostedTurnOrigins.Matches(key, "a", D, null));
        HostedTurnOrigins.Forget(key);
    }

    // The digest is taken after a JSON round trip, so a chat saved to the package and read back digests the same,
    // and any edit to the frozen submission does not.
    [Fact]
    public void TheSubmissionDigest_SurvivesASaveAndRead_AndChangesWithAnyEdit()
    {
        var session = new ChatSession
        {
            Hosted = new HostedChatMetadata
            {
                Submission = new HostedQuoteRequest
                {
                    Model = "claude-haiku-5.5", Effort = "medium", SpendingCapCredits = 0, Tools = ["get_timeline"],
                    Messages = HostedHistory.ToQuoteMessages([AgentMessage.UserText("Trim the “intro”…\n\tthen <export> & \\ done 🎬")]),
                },
            },
        };
        var digest = HostedTurnOrigins.Digest(session.Hosted.Submission);
        var reread = ChatSessionStore.Decode(ChatSessionStore.Encode(session));
        Assert.Equal(digest, HostedTurnOrigins.Digest(reread.Hosted!.Submission));
        Assert.Equal(digest, HostedTurnOrigins.Digest(ChatSessionStore.Decode(ChatSessionStore.Encode(reread)).Hosted!.Submission));
        reread.Hosted.Submission!.SpendingCapCredits = 500;
        Assert.NotEqual(digest, HostedTurnOrigins.Digest(reread.Hosted.Submission));
        Assert.Equal("none", HostedTurnOrigins.Digest(null));
    }

    [Theory]
    [InlineData(null, "account")]
    [InlineData("", "account")]
    [InlineData("not-a-guid", "account")]
    [InlineData("00000000-0000-0000-0000-000000000000", "account")]
    [InlineData("8d1c1a40-1f5e-4f37-a0d7-6a8d7c111111", null)]
    [InlineData("8d1c1a40-1f5e-4f37-a0d7-6a8d7c111111", "")]
    public void AMissingKeyOrAccount_NeverMatches(string? key, string? account) =>
        Assert.False(HostedTurnOrigins.Matches(key, account, D, null));

    [Fact]
    public void Record_RefusesAMissingKeyAccountOrDigest_AndWritesNothing()
    {
        Assert.Throws<ArgumentException>(() => HostedTurnOrigins.Record("", "a", D));
        Assert.ThrowsAny<ArgumentException>(() => HostedTurnOrigins.Record(Key(), " ", D));
        Assert.ThrowsAny<ArgumentException>(() => HostedTurnOrigins.Record(Key(), "a", ""));
        Assert.False(System.IO.File.Exists(File));
    }

    [Fact]
    public void ADamagedRecord_MatchesNothing_AndTheNextRecordKeepsItAside()
    {
        Directory.CreateDirectory(_folder);
        System.IO.File.WriteAllText(File, "{ not json");
        Assert.False(HostedTurnOrigins.Matches(Key(), "a", D, null));
        var key = Key();
        HostedTurnOrigins.Record(key, "a", D);
        Assert.True(HostedTurnOrigins.Matches(key, "a", D, null));
        Assert.Single(Directory.GetFiles(_folder, HostedTurnOrigins.FileName + ".damaged-*"));
    }

    [Fact]
    public void ARecordFromANewerBuild_OrWithoutADigest_IsNotTrusted()
    {
        Directory.CreateDirectory(_folder);
        var key = Key();
        System.IO.File.WriteAllText(File, new JsonObject
        {
            ["version"] = 99,
            ["turns"] = new JsonArray(new JsonObject { ["key"] = key, ["account"] = "a", ["submission"] = D, ["recordedAt"] = "2026-11-06T00:00:00Z" }),
        }.ToJsonString());
        Assert.False(HostedTurnOrigins.Matches(key, "a", D, null));
        System.IO.File.WriteAllText(File, new JsonObject
        {
            ["version"] = 1,
            ["turns"] = new JsonArray(new JsonObject { ["key"] = key, ["account"] = "a", ["recordedAt"] = "2026-11-06T00:00:00Z" }),
        }.ToJsonString());
        Assert.False(HostedTurnOrigins.Matches(key, "a", "", null));
    }

    [Fact]
    public void OldRecords_ArePruned_AndTheRecordStaysBounded()
    {
        var now = DateTimeOffset.Parse("2026-11-06T12:00:00Z");
        var old = Key();
        HostedTurnOrigins.Record(old, "a", D, now - HostedTurnOrigins.Retention - TimeSpan.FromMinutes(1));
        var keys = Enumerable.Range(0, HostedTurnOrigins.MaxRecords + 5).Select(_ => Key()).ToList();
        for (var i = 0; i < keys.Count; i++)
            HostedTurnOrigins.Record(keys[i], "a", D, now.AddSeconds(i));
        Assert.False(HostedTurnOrigins.Matches(old, "a", D, null));
        Assert.False(HostedTurnOrigins.Matches(keys[0], "a", D, null));
        Assert.True(HostedTurnOrigins.Matches(keys[^1], "a", D, null));
        Assert.Equal(HostedTurnOrigins.MaxRecords, JsonNode.Parse(System.IO.File.ReadAllText(File))!["turns"]!.AsArray().Count);
    }

    [Fact]
    public void AnEditOutsideTemno_IsReadAgain()
    {
        var key = Key();
        HostedTurnOrigins.Record(key, "a", D);
        Assert.True(HostedTurnOrigins.Matches(key, "a", D, null));
        System.IO.File.WriteAllText(File, "{\"version\":1,\"turns\":[]}");
        System.IO.File.SetLastWriteTimeUtc(File, DateTime.UtcNow.AddMinutes(1));
        Assert.False(HostedTurnOrigins.Matches(key, "a", D, null));
    }

    // Temno and Temno-Dev keep apart: the record sits in the environment's own data root, never in a package.
    [Fact]
    public void TheRecordLivesInThisEnvironmentsDataRoot()
    {
        HostedTurnOrigins.SetPathForTests(null);
        HostedTurnOrigins.SetDefaultPathForTests(null);
        try
        {
            Assert.Equal(Path.Combine(UserDataRoots.LocalAppDataRoot, HostedTurnOrigins.FileName), HostedTurnOrigins.FilePath);
        }
        finally
        {
            HostedTurnOriginsProcessDefault.Install();
            HostedTurnOrigins.SetPathForTests(File);
        }
    }
}
