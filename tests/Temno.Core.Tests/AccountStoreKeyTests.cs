using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Temno.Core.Account;
using Xunit;

namespace Temno.Core.Tests;

/// <summary>WP-260: a stored session belongs to one Supabase project and never crosses to another.</summary>
public sealed class AccountStoreKeyTests : IDisposable
{
    private const string DevOrigin = "https://devref.supabase.co";
    private const string OtherOrigin = "https://otherref.supabase.co";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "temno-account-store-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* ignore */ }
    }

    [Theory]
    [InlineData("https://devref.supabase.co", "https://devref.supabase.co")]
    [InlineData("https://DevRef.Supabase.co/", "https://devref.supabase.co")]
    [InlineData("HTTPS://devref.supabase.co:443/rest/v1?x=1", "https://devref.supabase.co")]
    [InlineData(" https://devref.supabase.co ", "https://devref.supabase.co")]
    [InlineData("http://127.0.0.1:54321", "http://127.0.0.1:54321")]
    [InlineData("https://devref.supabase.co:8443", "https://devref.supabase.co:8443")]
    [InlineData("https://user:pw@devref.supabase.co", "https://devref.supabase.co")]
    [InlineData("https://devref.supabase.co.", "https://devref.supabase.co")]
    [InlineData("https://DEVREF.SUPABASE.CO./auth/v1", "https://devref.supabase.co")]
    [InlineData("https://\uFF24\uFF25\uFF36\uFF32\uFF25\uFF26.supabase.co", "https://devref.supabase.co")]
    [InlineData("https://b\u00FCcher.example", "https://xn--bcher-kva.example")]
    [InlineData("http://[::1]:54321", "http://[::1]:54321")]
    public void Origin_is_scheme_host_and_port_in_lowercase(string url, string expected)
        => Assert.Equal(expected, AccountStoreKey.Origin(url));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("devref.supabase.co")]
    [InlineData("ftp://devref.supabase.co")]
    [InlineData(@"C:\account")]
    [InlineData("https://.")]
    public void Origin_is_null_for_anything_but_an_absolute_http_url(string? url)
        => Assert.Null(AccountStoreKey.Origin(url));

    [Fact]
    public void Key_is_the_first_16_lowercase_hex_of_the_origin_sha256()
    {
        var expected = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(DevOrigin)))[..16].ToLowerInvariant();

        Assert.Equal(expected, AccountStoreKey.Key(DevOrigin));
        Assert.Matches("^[0-9a-f]{16}$", AccountStoreKey.Key(DevOrigin));
        Assert.Equal(AccountStoreKey.Key(DevOrigin), AccountStoreKey.Key(AccountStoreKey.Origin("https://DEVREF.supabase.co/")!));
        Assert.NotEqual(AccountStoreKey.Key(DevOrigin), AccountStoreKey.Key(OtherOrigin));
        Assert.Equal(Path.Combine(_root, expected, "session.dpapi"), AccountStoreKey.SessionPath(_root, DevOrigin));
        Assert.Equal(Path.Combine(_root, "session.dpapi"), AccountStoreKey.LegacySessionPath(_root));
    }

    /// <summary>
    /// Sessions already on disk live under this key; a change to <see cref="AccountStoreKey.Origin"/>
    /// or <see cref="AccountStoreKey.Key"/> that moves it strands them. The pinned value is the first 8
    /// bytes of SHA-256 of the origin, as the code has always computed it, for a placeholder project ref.
    /// </summary>
    [Fact]
    public void Key_of_a_canonical_project_origin_is_pinned()
    {
        const string canonical = "https://abcdefghijklmnopqrst.supabase.co";

        Assert.Equal(canonical, AccountStoreKey.Origin(canonical));
        Assert.Equal("37bc3dc6072f67ce", AccountStoreKey.Key(AccountStoreKey.Origin(canonical)!));
        Assert.Equal("Temno.Account.v2|" + canonical, Encoding.UTF8.GetString(AccountStoreKey.Entropy(AccountStoreKey.Origin(canonical)!)));
    }

    [Theory]
    [InlineData("https://abcdefghijklmnopqrst.supabase.co.")]
    [InlineData("https://ABCDEFGHIJKLMNOPQRST.SUPABASE.CO")]
    [InlineData("HTTPS://Abcdefghijklmnopqrst.Supabase.Co.:443/")]
    [InlineData(" https://abcdefghijklmnopqrst.supabase.co/ ")]
    public void Trailing_dot_and_uppercase_spellings_share_the_canonical_key(string spelling)
    {
        var origin = AccountStoreKey.Origin(spelling)!;

        Assert.Equal("https://abcdefghijklmnopqrst.supabase.co", origin);
        Assert.Equal("37bc3dc6072f67ce", AccountStoreKey.Key(origin));
    }

    [Fact]
    public void Entropy_names_the_version_and_the_origin()
        => Assert.Equal("Temno.Account.v2|https://devref.supabase.co", Encoding.UTF8.GetString(AccountStoreKey.Entropy(DevOrigin)));

    [Fact]
    public void Issuer_matching_is_the_projects_auth_endpoint_only()
    {
        Assert.Equal(DevOrigin + "/auth/v1", AccountStoreKey.Issuer(Jwt(DevOrigin + "/auth/v1")));
        Assert.True(AccountStoreKey.IssuedBy(Jwt(DevOrigin + "/auth/v1"), DevOrigin));
        Assert.True(AccountStoreKey.IssuedBy(Jwt(DevOrigin + "/auth/v1/"), DevOrigin));
        Assert.True(AccountStoreKey.IssuedBy(Jwt("https://DEVREF.supabase.co/auth/v1"), DevOrigin));
        Assert.True(AccountStoreKey.IssuedBy(Jwt("http://127.0.0.1:54321/auth/v1"), "http://127.0.0.1:54321"));
        Assert.True(AccountStoreKey.IssuedBy(Jwt("https://devref.supabase.co./auth/v1"), DevOrigin));

        Assert.False(AccountStoreKey.IssuedBy(Jwt(OtherOrigin + "/auth/v1"), DevOrigin));
        Assert.False(AccountStoreKey.IssuedBy(Jwt(DevOrigin), DevOrigin));
        Assert.False(AccountStoreKey.IssuedBy(Jwt(DevOrigin + "/auth/v2"), DevOrigin));
        Assert.False(AccountStoreKey.IssuedBy(Jwt(DevOrigin + ":8443/auth/v1"), DevOrigin));
        Assert.False(AccountStoreKey.IssuedBy(Jwt("http://devref.supabase.co/auth/v1"), DevOrigin));
        Assert.False(AccountStoreKey.IssuedBy(Jwt(DevOrigin + "/auth/v1?x=1"), DevOrigin));
        Assert.False(AccountStoreKey.IssuedBy(Jwt(null), DevOrigin));
        Assert.False(AccountStoreKey.IssuedBy("not-a-jwt", DevOrigin));
        Assert.False(AccountStoreKey.IssuedBy("a.%%%.c", DevOrigin));
        Assert.False(AccountStoreKey.IssuedBy("a." + B64("[1,2]") + ".c", DevOrigin));
        Assert.False(AccountStoreKey.IssuedBy(null, DevOrigin));
        Assert.False(AccountStoreKey.IssuedBy(Jwt(DevOrigin + "/auth/v1"), ""));
    }

    [Fact]
    public void Store_holds_nothing_while_sign_in_is_not_configured()
    {
        var store = new ProjectSessionStore(_root, () => null, new FakeProtector());

        store.Save(Bundle(DevOrigin, "user-1"));
        Assert.Null(store.Load());
        store.Clear();
        Assert.False(Directory.Exists(_root));
    }

    [Fact]
    public void Store_round_trips_under_the_project_key_and_never_crosses_projects()
    {
        var project = DevOrigin + "/";
        var store = new ProjectSessionStore(_root, () => project, new FakeProtector());
        store.Save(Bundle(DevOrigin, "user-1"));

        Assert.True(File.Exists(AccountStoreKey.SessionPath(_root, DevOrigin)));
        Assert.False(File.Exists(AccountStoreKey.LegacySessionPath(_root)));
        Assert.Equal("user-1", store.Load()?.UserId);

        project = OtherOrigin;
        Assert.Null(store.Load());
        store.Save(Bundle(OtherOrigin, "user-2"));
        Assert.Equal("user-2", store.Load()?.UserId);

        project = DevOrigin;
        Assert.Equal("user-1", store.Load()?.UserId);
        store.Clear();
        Assert.Null(store.Load());
        Assert.False(File.Exists(AccountStoreKey.SessionPath(_root, DevOrigin)));
        Assert.True(File.Exists(AccountStoreKey.SessionPath(_root, OtherOrigin)));
    }

    [Fact]
    public void Keyed_file_is_protected_with_the_project_entropy()
    {
        var protector = new FakeProtector();
        var store = new ProjectSessionStore(_root, () => DevOrigin, protector);
        store.Save(Bundle(DevOrigin, "user-1"));

        var bytes = File.ReadAllBytes(AccountStoreKey.SessionPath(_root, DevOrigin));
        Assert.Throws<CryptographicException>(() => protector.Unprotect(bytes, Encoding.UTF8.GetBytes(AccountStoreKey.LegacyEntropy)));
        Assert.Throws<CryptographicException>(() => protector.Unprotect(bytes, AccountStoreKey.Entropy(OtherOrigin)));
        Assert.NotEmpty(protector.Unprotect(bytes, AccountStoreKey.Entropy(DevOrigin)));
    }

    [Fact]
    public void Legacy_session_issued_by_the_current_project_moves_to_the_keyed_store()
    {
        var protector = new FakeProtector();
        WriteLegacy(protector, Bundle(DevOrigin, "legacy-user"));
        var store = new ProjectSessionStore(_root, () => DevOrigin, protector);

        Assert.Equal("legacy-user", store.Load()?.UserId);
        Assert.False(File.Exists(AccountStoreKey.LegacySessionPath(_root)));
        Assert.True(File.Exists(AccountStoreKey.SessionPath(_root, DevOrigin)));
        Assert.Equal("legacy-user", new ProjectSessionStore(_root, () => DevOrigin, protector).Load()?.UserId);
    }

    [Fact]
    public void Legacy_session_from_another_project_is_left_untouched()
    {
        var protector = new FakeProtector();
        WriteLegacy(protector, Bundle(OtherOrigin, "other-user"));
        var before = File.ReadAllBytes(AccountStoreKey.LegacySessionPath(_root));
        var store = new ProjectSessionStore(_root, () => DevOrigin, protector);

        Assert.Null(store.Load());
        Assert.Equal(before, File.ReadAllBytes(AccountStoreKey.LegacySessionPath(_root)));
        Assert.False(File.Exists(AccountStoreKey.SessionPath(_root, DevOrigin)));

        // The project that issued it still picks it up.
        Assert.Equal("other-user", new ProjectSessionStore(_root, () => OtherOrigin, protector).Load()?.UserId);
    }

    [Fact]
    public void Unreadable_legacy_session_is_left_untouched()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllBytes(AccountStoreKey.LegacySessionPath(_root), [1, 2, 3]);
        var store = new ProjectSessionStore(_root, () => DevOrigin, new FakeProtector());

        Assert.Null(store.Load());
        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(AccountStoreKey.LegacySessionPath(_root)));
    }

    [Fact]
    public void Existing_keyed_session_wins_over_a_legacy_one()
    {
        var protector = new FakeProtector();
        var store = new ProjectSessionStore(_root, () => DevOrigin, protector);
        store.Save(Bundle(DevOrigin, "keyed-user"));
        WriteLegacy(protector, Bundle(DevOrigin, "legacy-user"));

        Assert.Equal("keyed-user", store.Load()?.UserId);
        Assert.True(File.Exists(AccountStoreKey.LegacySessionPath(_root)));
    }

    [Fact]
    public void Corrupt_keyed_session_is_discarded()
    {
        var path = AccountStoreKey.SessionPath(_root, DevOrigin);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, [9, 9, 9]);
        var store = new ProjectSessionStore(_root, () => DevOrigin, new FakeProtector());

        Assert.Null(store.Load());
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void Save_of_a_token_another_project_issued_writes_nothing()
    {
        var store = new ProjectSessionStore(_root, () => DevOrigin, new FakeProtector());

        Assert.False(store.TrySave(Bundle(OtherOrigin, "other-user")));
        store.Save(Bundle(OtherOrigin, "other-user"));
        Assert.False(store.TrySave(new AccountTokenBundle { AccessToken = "not-a-jwt", RefreshToken = "r", UserId = "u" }));
        Assert.False(Directory.Exists(_root));
        Assert.Null(store.Load());

        Assert.True(store.TrySave(Bundle(DevOrigin, "dev-user")));
        var stored = File.ReadAllBytes(AccountStoreKey.SessionPath(_root, DevOrigin));
        store.Save(Bundle(OtherOrigin, "other-user"));
        Assert.Equal(stored, File.ReadAllBytes(AccountStoreKey.SessionPath(_root, DevOrigin)));
        Assert.Equal("dev-user", store.Load()?.UserId);
    }

    [Fact]
    public void Session_taken_before_the_project_changed_is_not_stored_under_the_new_project()
    {
        // The finding: sign-in starts under one project, the developer file switches, and the
        // callback returns a token the old project issued.
        var project = DevOrigin;
        var store = new ProjectSessionStore(_root, () => project, new FakeProtector());
        var taken = Bundle(DevOrigin, "dev-user");
        project = OtherOrigin;

        Assert.False(store.TrySave(taken));
        Assert.False(File.Exists(AccountStoreKey.SessionPath(_root, OtherOrigin)));
        Assert.False(File.Exists(AccountStoreKey.SessionPath(_root, DevOrigin)));
        Assert.Null(store.Load());
    }

    [Fact]
    public void Keyed_file_holding_another_projects_token_loads_as_null_and_is_left_in_place()
    {
        // What the store could hold before this check: another project's token under this key.
        var protector = new FakeProtector();
        var path = AccountStoreKey.SessionPath(_root, DevOrigin);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, protector.Protect(Serialize(Bundle(OtherOrigin, "other-user")), AccountStoreKey.Entropy(DevOrigin)));
        var before = File.ReadAllBytes(path);
        var store = new ProjectSessionStore(_root, () => DevOrigin, protector);

        Assert.Null(store.Load());
        Assert.Equal(before, File.ReadAllBytes(path));

        // Signing in to this project replaces it.
        store.Save(Bundle(DevOrigin, "dev-user"));
        Assert.Equal("dev-user", store.Load()?.UserId);
    }

    [Fact]
    public void Legacy_session_moves_when_the_project_url_is_spelled_with_a_trailing_dot()
    {
        var protector = new FakeProtector();
        WriteLegacy(protector, Bundle(DevOrigin, "legacy-user"));
        var store = new ProjectSessionStore(_root, () => "https://DEVREF.supabase.co./", protector);

        Assert.Equal("legacy-user", store.Load()?.UserId);
        Assert.False(File.Exists(AccountStoreKey.LegacySessionPath(_root)));
        Assert.Equal("legacy-user", new ProjectSessionStore(_root, () => DevOrigin, protector).Load()?.UserId);
    }

    [Fact]
    public void Clear_removes_only_the_current_projects_session()
    {
        var protector = new FakeProtector();
        var project = OtherOrigin;
        var store = new ProjectSessionStore(_root, () => project, protector);
        store.Save(Bundle(OtherOrigin, "other-user"));
        project = DevOrigin;
        store.Save(Bundle(DevOrigin, "dev-user"));
        Assert.Equal("dev-user", store.Load()?.UserId);

        project = OtherOrigin;
        store.Clear();

        Assert.False(File.Exists(AccountStoreKey.SessionPath(_root, OtherOrigin)));
        Assert.True(File.Exists(AccountStoreKey.SessionPath(_root, DevOrigin)));
        project = DevOrigin;
        Assert.Equal("dev-user", store.Load()?.UserId);
    }

    [Theory]
    [InlineData("https://devref.supabase.co", "https://devref.supabase.co", true)]
    [InlineData("https://devref.supabase.co", "https://devref.supabase.co/", true)]
    [InlineData("https://devref.supabase.co", "https://DEVREF.supabase.co.", true)]
    [InlineData("https://devref.supabase.co", " https://devref.supabase.co:443/rest/v1 ", true)]
    [InlineData("http://127.0.0.1:54321", "http://127.0.0.1:54321", true)]
    [InlineData("https://devref.supabase.co", "https://otherref.supabase.co", false)]
    [InlineData("https://devref.supabase.co", "http://devref.supabase.co", false)]
    [InlineData("https://devref.supabase.co", "https://devref.supabase.co:8443", false)]
    [InlineData("http://127.0.0.1:54321", "http://localhost:54321", false)]
    [InlineData("https://devref.supabase.co", null, false)]
    [InlineData("https://devref.supabase.co", "", false)]
    [InlineData(null, "https://devref.supabase.co", false)]
    [InlineData(null, null, false)]
    public void SameProject_holds_only_while_the_configured_project_is_the_sessions(string? bound, string? current, bool expected)
        => Assert.Equal(expected, AccountStoreKey.SameProject(bound, current));

    /// <summary>
    /// The desktop's path through a project switch (AccountSessionService): the session is bound
    /// to the project it was restored under, the switch unbinds it, nothing is written for either
    /// project, and switching back finds the original session where it was.
    /// </summary>
    [Fact]
    public void Project_switch_while_signed_in_drops_the_session_and_keeps_both_stores()
    {
        var protector = new FakeProtector();
        var project = OtherOrigin;
        var store = new ProjectSessionStore(_root, () => project, protector);
        store.Save(Bundle(OtherOrigin, "other-user"));
        project = DevOrigin;
        store.Save(Bundle(DevOrigin, "dev-user"));

        var session = store.Load()!;
        var bound = AccountStoreKey.Origin(project);
        Assert.True(AccountStoreKey.SameProject(bound, project));

        project = OtherOrigin + "/";
        Assert.False(AccountStoreKey.SameProject(bound, project));
        // A refresh result or late save of the dropped session still names the old project.
        Assert.False(store.TrySave(session));
        Assert.Equal("other-user", store.Load()?.UserId);

        project = DevOrigin;
        Assert.Equal("dev-user", store.Load()?.UserId);
    }

    private static byte[] Serialize(AccountTokenBundle bundle)
        => Encoding.UTF8.GetBytes(JsonSerializer.Serialize(bundle, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));

    private void WriteLegacy(FakeProtector protector, AccountTokenBundle bundle)
    {
        Directory.CreateDirectory(_root);
        var json = JsonSerializer.Serialize(bundle, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        File.WriteAllBytes(
            AccountStoreKey.LegacySessionPath(_root),
            protector.Protect(Encoding.UTF8.GetBytes(json), Encoding.UTF8.GetBytes(AccountStoreKey.LegacyEntropy)));
    }

    private static AccountTokenBundle Bundle(string origin, string userId) => new()
    {
        AccessToken = Jwt(origin + "/auth/v1"),
        RefreshToken = "refresh-" + userId,
        UserId = userId,
    };

    private static string Jwt(string? issuer)
        => B64("{\"alg\":\"HS256\"}") + "." + B64(issuer is null ? "{\"sub\":\"u\"}" : JsonSerializer.Serialize(new { iss = issuer, sub = "u" })) + ".sig";

    private static string B64(string json) => Pkce.Base64Url(Encoding.UTF8.GetBytes(json));

    /// <summary>Binds the bytes to the entropy the way DPAPI does, without DPAPI.</summary>
    private sealed class FakeProtector : ISessionProtector
    {
        public byte[] Protect(byte[] plain, byte[] entropy)
            => [.. SHA256.HashData(entropy), .. plain];

        public byte[] Unprotect(byte[] protectedBytes, byte[] entropy)
        {
            var tag = SHA256.HashData(entropy);
            if (protectedBytes.Length < tag.Length || !protectedBytes.AsSpan(0, tag.Length).SequenceEqual(tag))
                throw new CryptographicException("wrong entropy");
            return protectedBytes[tag.Length..];
        }
    }
}
