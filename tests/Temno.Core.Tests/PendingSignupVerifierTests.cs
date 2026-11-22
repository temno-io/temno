using System.Security.Cryptography;
using Temno.Core.Account;
using Xunit;

namespace Temno.Core.Tests;

/// <summary>WP-390: the pending sign-up verifier on disk, and when it is sent, retried and dropped.</summary>
public sealed class PendingSignupVerifierTests : IDisposable
{
    private const string Dev = "https://dev.supabase.co";
    private const string Other = "https://other.supabase.co";
    private const string Verifier = "wp390-verifier-0123456789-abcdefghijklmnopqrstuvwxyz-ABCDEFG";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "temno-wp390-" + Guid.NewGuid().ToString("N"));
    private string? _project = Dev;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private ProjectSignupVerifierStore Store(ISessionProtector? protector = null)
        => new(_root, () => _project, protector ?? new FakeProtector());

    private static PendingSignupVerifier Pending(string origin = Dev, string user = "user-a", DateTimeOffset? at = null)
        => new(user, origin, Verifier, at ?? DateTimeOffset.UtcNow);

    [Fact]
    public void A_saved_verifier_is_read_back_by_the_next_start()
    {
        var saved = Pending();
        Store().Save(saved);

        Assert.Equal(saved, Store().Load());
        Assert.True(File.Exists(ProjectSignupVerifierStore.PathFor(_root, Dev)));
        Assert.Equal(Path.GetDirectoryName(AccountStoreKey.SessionPath(_root, Dev)),
            Path.GetDirectoryName(ProjectSignupVerifierStore.PathFor(_root, Dev)));
    }

    [Fact]
    public void The_file_is_protected_with_its_own_entropy_not_the_sessions()
    {
        var protector = new FakeProtector();
        Store(protector).Save(Pending());
        var bytes = File.ReadAllBytes(ProjectSignupVerifierStore.PathFor(_root, Dev));

        Assert.Throws<CryptographicException>(() => protector.Unprotect(bytes, AccountStoreKey.Entropy(Dev)));
        Assert.NotEmpty(protector.Unprotect(bytes, ProjectSignupVerifierStore.Entropy(Dev)));
    }

    [Fact]
    public void Another_projects_verifier_is_neither_written_nor_read()
    {
        var store = Store();
        store.Save(Pending(origin: Other));
        Assert.Null(store.Load());
        Assert.False(Directory.Exists(_root));

        // A file moved between project folders names its own project and is not returned.
        Store().Save(Pending());
        var devFile = ProjectSignupVerifierStore.PathFor(_root, Dev);
        var otherFile = ProjectSignupVerifierStore.PathFor(_root, Other);
        Directory.CreateDirectory(Path.GetDirectoryName(otherFile)!);
        File.WriteAllBytes(otherFile, new FakeProtector().Protect(
            new FakeProtector().Unprotect(File.ReadAllBytes(devFile), ProjectSignupVerifierStore.Entropy(Dev)),
            ProjectSignupVerifierStore.Entropy(Other)));
        _project = Other;
        Assert.Null(Store().Load());
    }

    [Fact]
    public void Clear_deletes_only_the_current_projects_verifier()
    {
        Store().Save(Pending());
        _project = Other;
        Store().Save(Pending(origin: Other));

        Store().Clear();

        Assert.False(File.Exists(ProjectSignupVerifierStore.PathFor(_root, Other)));
        _project = Dev;
        Assert.NotNull(Store().Load());
    }

    [Fact]
    public void An_unreadable_file_is_deleted_and_reads_as_none()
    {
        var path = ProjectSignupVerifierStore.PathFor(_root, Dev);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, [1, 2, 3]);

        Assert.Null(Store().Load());
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void A_failed_write_keeps_the_verifier_for_this_process_and_does_not_throw()
    {
        var store = Store(new RefusingProtector());
        var pending = Pending();

        store.Save(pending);

        Assert.Equal(pending, store.Load());
        Assert.Null(Store().Load());
    }

    [Fact]
    public void Without_a_project_nothing_is_kept()
    {
        _project = null;
        var store = Store();
        store.Save(Pending());
        store.Clear();

        Assert.Null(store.Load());
        Assert.False(Directory.Exists(_root));
    }

    [Fact]
    public void Retry_gaps_grow_to_a_minute()
    {
        var gaps = Enumerable.Range(0, 7).Select(SignupVerifierRules.RetryGap).Select(gap => gap.TotalSeconds);
        Assert.Equal([5, 10, 20, 40, 60, 60, 60], gaps);
        Assert.Equal(TimeSpan.FromSeconds(30), SignupVerifierRules.BootstrapTimeout);
    }

    [Theory]
    [InlineData(null, true)]   // no response: transport failure or timeout
    [InlineData(401, true)]
    [InlineData(408, true)]
    [InlineData(429, true)]
    [InlineData(500, true)]
    [InlineData(503, true)]
    [InlineData(400, false)]   // invalid_request: a malformed verifier
    [InlineData(403, false)]   // account_suspended, environment_mismatch
    [InlineData(404, false)]
    public void Only_failures_a_later_call_could_change_are_retried(int? status, bool retried)
    {
        Assert.Equal(retried, SignupVerifierRules.MayAnswerLater(new HostedApiException("x", status)));
    }

    [Fact]
    public void Cancellation_keeps_the_verifier_and_other_errors_end_the_attempt()
    {
        Assert.True(SignupVerifierRules.MayAnswerLater(new TaskCanceledException()));
        Assert.False(SignupVerifierRules.MayAnswerLater(new ArgumentException("bad gateway url")));
    }

    [Fact]
    public void A_pending_verifier_belongs_only_to_its_account_and_project_for_a_day()
    {
        var now = DateTimeOffset.UtcNow;
        var pending = Pending(at: now.AddHours(-23));

        Assert.True(SignupVerifierRules.Belongs(pending, "user-a", Dev, now));
        Assert.False(SignupVerifierRules.Belongs(pending, "user-b", Dev, now));
        Assert.False(SignupVerifierRules.Belongs(pending, "user-a", Other, now));
        Assert.False(SignupVerifierRules.Belongs(pending, null, Dev, now));
        Assert.False(SignupVerifierRules.Belongs(Pending(at: now.AddDays(-1).AddSeconds(-1)), "user-a", Dev, now));
        Assert.False(SignupVerifierRules.Belongs(Pending(at: now.AddHours(1)), "user-a", Dev, now));
    }

    [Fact]
    public void Retries_run_for_the_tickets_fifteen_minutes()
    {
        var now = DateTimeOffset.UtcNow;
        Assert.True(SignupVerifierRules.Retrying(Pending(at: now.AddMinutes(-14)), now));
        Assert.True(SignupVerifierRules.Retrying(Pending(at: now.AddMinutes(-15)), now));
        Assert.False(SignupVerifierRules.Retrying(Pending(at: now.AddMinutes(-15).AddSeconds(-1)), now));
    }

    private sealed class FakeProtector : ISessionProtector
    {
        public byte[] Protect(byte[] plain, byte[] entropy) => [.. SHA256.HashData(entropy), .. plain];

        public byte[] Unprotect(byte[] protectedBytes, byte[] entropy)
        {
            var tag = SHA256.HashData(entropy);
            if (protectedBytes.Length < tag.Length || !protectedBytes.AsSpan(0, tag.Length).SequenceEqual(tag))
                throw new CryptographicException("wrong entropy");
            return protectedBytes[tag.Length..];
        }
    }

    private sealed class RefusingProtector : ISessionProtector
    {
        public byte[] Protect(byte[] plain, byte[] entropy) => throw new CryptographicException("refused");
        public byte[] Unprotect(byte[] protectedBytes, byte[] entropy) => throw new CryptographicException("refused");
    }
}
