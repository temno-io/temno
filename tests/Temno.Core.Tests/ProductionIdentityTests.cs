using Temno.Core.Account;
using Temno.Environments;
using Xunit;

namespace Temno.Core.Tests;

public sealed class ProductionIdentityTests
{
    [Fact]
    public void Production_resolves_public_endpoints_without_reading_development_settings()
    {
        var backend = HostedEndpoints.Resolve(TemnoEnvironment.Production, EnvironmentIdentity.Production,
            _ => throw new InvalidOperationException("Must not read environment"),
            () => throw new InvalidOperationException("Must not read development file"));
        Assert.True(backend.AuthConfigured);
        Assert.True(backend.GatewayConfigured);
        Assert.True(backend.AccountsConfigured);
        Assert.Null(backend.Problem);
        Assert.Equal("https://fshqvdygdtbkrrbtgpda.supabase.co", backend.SupabaseUrl);
        Assert.Equal("https://gateway.temno.io", backend.GatewayUrl);
        Assert.Equal("https://accounts.temno.io", backend.AccountsUrl);
        Assert.Equal("https://gateway.temno.io", backend.EventsUrl);
        Assert.StartsWith("sb_publishable_", backend.AnonKey);
        Assert.Equal("acct_1UEHttRzwSqmSvUB", EnvironmentIdentity.Production.StripeAccountId);
    }

    [Theory]
    [InlineData("https://auth.temno.io")]
    [InlineData("https://AUTH.TEMNO.IO.:443/")]
    [InlineData("https://fshqvdygdtbkrrbtgpda.supabase.co")]
    public void Development_refuses_both_origins_of_the_production_project(string origin)
    {
        var backend = HostedEndpoints.Resolve(TemnoEnvironment.Development, EnvironmentIdentity.Production,
            name => name == "TEMNO_SUPABASE_URL" ? origin : null, () => null);
        Assert.False(backend.AuthConfigured);
        Assert.Contains("production", backend.Problem);
    }

    [Theory]
    [InlineData("https://auth.temno.io.attacker.test")]
    [InlineData("https://not-auth.temno.io")]
    [InlineData("https://auth.temno.io@devref.supabase.co")]
    [InlineData("https://devref.supabase.co")]
    public void Custom_alias_matching_is_exact(string origin)
        => Assert.False(EnvironmentIdentity.Production.NamesSupabaseProject(origin));
}
