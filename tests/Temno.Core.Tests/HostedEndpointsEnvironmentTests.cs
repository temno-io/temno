using System.Reflection;
using Temno.Core.Account;
using Temno.Environments;
using Xunit;

namespace Temno.Core.Tests;

/// <summary>
/// WP-260 endpoint matrix uses synthetic identities, independent of live deployment values.
/// Nothing here reads the process environment or a real file.
/// </summary>
public sealed class HostedEndpointsEnvironmentTests
{
    private static readonly EnvironmentIdentity Prod = new(
        "prodtestref00000000", "sb_publishable_prodtest", "gateway.prod.test", "accounts.prod.test", "acct_prodtest", "accounts@prod.test");

    private static readonly EnvironmentIdentity Empty = new("", "", "", "", "", "");

    private const string DevFile = """
        {
          "environment": "development",
          "supabaseUrl": "https://devref.supabase.co/",
          "supabaseAnonKey": "sb_publishable_devfile",
          "gatewayUrl": "https://gateway.dev.test",
          "accountsUrl": "https://accounts.dev.test",
          "note": "unknown members are ignored"
        }
        """;

    private static Func<string, string?> Env(params (string Name, string Value)[] values)
        => name => values.FirstOrDefault(v => v.Name == name).Value;

    private static readonly Func<string, string?> NoEnv = _ => null;

    private static Func<string?> FileText(string? json) => () => json;

    private static readonly Func<string?> NoFile = () => null;

    private static HostedBackend Dev(Func<string, string?> env, Func<string?> file)
        => HostedEndpoints.Resolve(TemnoEnvironment.Development, Prod, env, file);

    private static void AssertOff(HostedBackend backend)
    {
        Assert.Equal("", backend.SupabaseUrl);
        Assert.Equal("", backend.AnonKey);
        Assert.Equal("", backend.GatewayUrl);
        Assert.Equal("", backend.AccountsUrl);
        Assert.False(backend.AuthConfigured);
        Assert.False(backend.GatewayConfigured);
        Assert.False(backend.AccountsConfigured);
        Assert.False(string.IsNullOrWhiteSpace(backend.Problem));
    }

    [Fact]
    public void Production_build_uses_production_values_and_never_reads_overrides_or_the_file()
    {
        var envReads = 0;
        var fileReads = 0;
        var backend = HostedEndpoints.Resolve(
            TemnoEnvironment.Production,
            Prod,
            name =>
            {
                envReads++;
                return name == "TEMNO_AUTH_REDIRECT_URI" ? "http://127.0.0.1:1/steal" : "https://override.dev.test";
            },
            () =>
            {
                fileReads++;
                return DevFile;
            });

        Assert.Equal(0, envReads);
        Assert.Equal(0, fileReads);
        Assert.Equal(TemnoEnvironment.Production, backend.Environment);
        Assert.Equal("https://prodtestref00000000.supabase.co", backend.SupabaseUrl);
        Assert.Equal("sb_publishable_prodtest", backend.AnonKey);
        Assert.Equal("https://gateway.prod.test", backend.GatewayUrl);
        Assert.Equal("https://accounts.prod.test", backend.AccountsUrl);
        Assert.Equal(HostedEndpoints.DefaultRedirectUri, backend.RedirectUri);
        Assert.True(backend.AuthConfigured);
        Assert.True(backend.GatewayConfigured);
        Assert.True(backend.AccountsConfigured);
        Assert.Null(backend.Problem);
    }

    [Fact]
    public void Production_build_with_an_empty_identity_is_unconfigured_with_the_production_message()
    {
        var backend = HostedEndpoints.Resolve(
            TemnoEnvironment.Production, Empty, Env(("TEMNO_GATEWAY_URL", "https://gateway.dev.test")), FileText(DevFile));

        AssertOff(backend);
        Assert.Equal(HostedEndpoints.ProductionNotConfiguredMessage, backend.Problem);
        Assert.Equal(
            "This build has no production backend yet. Sign-in and hosted features are off.",
            backend.Problem);
        Assert.Equal(HostedEndpoints.DefaultRedirectUri, backend.RedirectUri);
    }

    [Fact]
    public void Production_build_with_a_partial_identity_turns_on_only_what_it_names()
    {
        var partial = new EnvironmentIdentity("prodtestref00000000", "sb_publishable_prodtest", "", "", "", "");
        var backend = HostedEndpoints.Resolve(TemnoEnvironment.Production, partial, NoEnv, NoFile);

        Assert.True(backend.AuthConfigured);
        Assert.False(backend.GatewayConfigured);
        Assert.False(backend.AccountsConfigured);
        Assert.Contains("gateway host", backend.Problem);
        Assert.Contains("accounts host", backend.Problem);
    }

    [Fact]
    public void Development_build_uses_the_developer_file()
    {
        var backend = Dev(NoEnv, FileText(DevFile));

        Assert.Equal(TemnoEnvironment.Development, backend.Environment);
        Assert.Equal("https://devref.supabase.co", backend.SupabaseUrl);
        Assert.Equal("sb_publishable_devfile", backend.AnonKey);
        Assert.Equal("https://gateway.dev.test", backend.GatewayUrl);
        Assert.Equal("https://accounts.dev.test", backend.AccountsUrl);
        Assert.Equal(HostedEndpoints.DefaultRedirectUri, backend.RedirectUri);
        Assert.Null(backend.Problem);
    }

    [Fact]
    public void Development_variables_beat_the_developer_file_per_field()
    {
        var backend = Dev(
            Env(("TEMNO_GATEWAY_URL", " https://gateway.env.test/ "), ("TEMNO_AUTH_REDIRECT_URI", "http://127.0.0.1:17400/callback")),
            FileText(DevFile));

        Assert.Equal("https://gateway.env.test", backend.GatewayUrl);
        Assert.Equal("https://devref.supabase.co", backend.SupabaseUrl);
        Assert.Equal("sb_publishable_devfile", backend.AnonKey);
        Assert.Equal("https://accounts.dev.test", backend.AccountsUrl);
        Assert.Equal("http://127.0.0.1:17400/callback", backend.RedirectUri);
        Assert.Null(backend.Problem);

        var all = Dev(
            Env(("TEMNO_SUPABASE_URL", "https://envref.supabase.co"),
                ("TEMNO_SUPABASE_ANON_KEY", "sb_publishable_env"),
                ("TEMNO_GATEWAY_URL", "https://gateway.env.test"),
                ("TEMNO_ACCOUNTS_URL", "http://127.0.0.1:5080")),
            NoFile);
        Assert.Equal("https://envref.supabase.co", all.SupabaseUrl);
        Assert.Equal("sb_publishable_env", all.AnonKey);
        Assert.Equal("https://gateway.env.test", all.GatewayUrl);
        Assert.Equal("http://127.0.0.1:5080", all.AccountsUrl);
        Assert.Null(all.Problem);
    }

    [Fact]
    public void Development_build_with_nothing_is_unconfigured_and_never_resolves_production()
    {
        var backend = Dev(NoEnv, NoFile);

        AssertOff(backend);
        Assert.Equal(HostedEndpoints.DevelopmentNotConfiguredMessage, backend.Problem);
        Assert.Equal(
            @"Development backend not configured. Add %LOCALAPPDATA%\Temno-Dev\development-backend.json or set the TEMNO_* variables.",
            backend.Problem);
        Assert.Equal(HostedEndpoints.DefaultRedirectUri, backend.RedirectUri);
        Assert.DoesNotContain(Prod.GatewayHost, backend.GatewayUrl + backend.AccountsUrl + backend.SupabaseUrl);
    }

    [Fact]
    public void Development_build_with_only_some_values_turns_on_those_and_names_the_rest()
    {
        var backend = Dev(Env(("TEMNO_GATEWAY_URL", "https://gateway.env.test")), NoFile);

        Assert.True(backend.GatewayConfigured);
        Assert.False(backend.AuthConfigured);
        Assert.False(backend.AccountsConfigured);
        Assert.Equal(
            @"Development backend is missing supabaseUrl, supabaseAnonKey, accountsUrl. Add it to %LOCALAPPDATA%\Temno-Dev\development-backend.json or set the TEMNO_* variables.",
            backend.Problem);
    }

    [Theory]
    [InlineData("TEMNO_GATEWAY_URL", "https://gateway.prod.test")]
    [InlineData("TEMNO_GATEWAY_URL", "https://GATEWAY.PROD.TEST/v1")]
    [InlineData("TEMNO_GATEWAY_URL", "https://accounts.prod.test")]
    [InlineData("TEMNO_ACCOUNTS_URL", "https://accounts.prod.test")]
    [InlineData("TEMNO_SUPABASE_URL", "https://prodtestref00000000.supabase.co")]
    [InlineData("TEMNO_SUPABASE_URL", "https://gateway.prod.test")]
    public void Development_variable_on_a_production_host_or_project_refuses_the_whole_backend(string variable, string value)
    {
        var backend = Dev(Env((variable, value)), FileText(DevFile));

        AssertOff(backend);
        Assert.Contains(variable, backend.Problem);
        Assert.Contains("production", backend.Problem);
    }

    [Theory]
    [InlineData("gatewayUrl", "https://gateway.prod.test")]
    [InlineData("accountsUrl", "https://accounts.prod.test.")]
    [InlineData("supabaseUrl", "https://PRODTESTREF00000000.supabase.co/")]
    public void Developer_file_value_on_a_production_host_or_project_refuses_the_whole_backend(string member, string value)
    {
        var json = DevFile.Replace(member switch
        {
            "gatewayUrl" => "https://gateway.dev.test",
            "accountsUrl" => "https://accounts.dev.test",
            _ => "https://devref.supabase.co/",
        }, value);
        var backend = Dev(NoEnv, FileText(json));

        AssertOff(backend);
        Assert.Contains($"\"{member}\"", backend.Problem);
        Assert.Contains("production", backend.Problem);
    }

    [Fact]
    public void Development_value_equal_to_production_publishable_key_is_refused()
    {
        var backend = Dev(Env(("TEMNO_SUPABASE_ANON_KEY", "sb_publishable_prodtest")), FileText(DevFile));

        AssertOff(backend);
        Assert.Contains("TEMNO_SUPABASE_ANON_KEY", backend.Problem);
        Assert.Contains("production", backend.Problem);
    }

    [Theory]
    [InlineData("""{"environment":"production","gatewayUrl":"https://gateway.dev.test"}""", "says \"environment\": \"production\"")]
    [InlineData("""{"gatewayUrl":"https://gateway.dev.test"}""", "must say \"environment\": \"development\"")]
    [InlineData("""{"environment":"Development","gatewayUrl":"https://gateway.dev.test"}""", "must say \"environment\": \"development\"")]
    [InlineData("""{"environment":"staging"}""", "must say \"environment\": \"development\"")]
    [InlineData("""{"environment":1}""", "must say \"environment\": \"development\"")]
    [InlineData("""["development"]""", "must be a JSON object")]
    [InlineData("""{"environment":"development","gatewayUrl":42}""", "\"gatewayUrl\"")]
    [InlineData("""{"environment":"development",""", "is not valid JSON")]
    [InlineData("", "is not valid JSON")]
    public void Developer_file_that_is_not_a_development_file_turns_hosted_off_even_with_variables(string json, string expected)
    {
        var backend = Dev(
            Env(("TEMNO_SUPABASE_URL", "https://envref.supabase.co"),
                ("TEMNO_SUPABASE_ANON_KEY", "sb_publishable_env"),
                ("TEMNO_GATEWAY_URL", "https://gateway.env.test"),
                ("TEMNO_ACCOUNTS_URL", "https://accounts.env.test")),
            FileText(json));

        AssertOff(backend);
        Assert.Contains(expected, backend.Problem);
        Assert.Contains(DevelopmentBackendFile.DisplayPath, backend.Problem);
    }

    [Fact]
    public void Developer_file_accepts_comments_and_trailing_commas_and_null_members()
    {
        var backend = Dev(NoEnv, FileText("""
            {
              // hand-edited
              "environment": "development",
              "supabaseUrl": "https://devref.supabase.co",
              "supabaseAnonKey": "sb_publishable_devfile",
              "gatewayUrl": "https://gateway.dev.test",
              "accountsUrl": null,
            }
            """));

        Assert.True(backend.AuthConfigured);
        Assert.True(backend.GatewayConfigured);
        Assert.False(backend.AccountsConfigured);
        Assert.Contains("accountsUrl", backend.Problem);
    }

    [Fact]
    public void Unreadable_developer_file_turns_hosted_off()
    {
        var backend = Dev(NoEnv, () => throw new IOException("locked"));

        AssertOff(backend);
        Assert.Contains("could not be read", backend.Problem);
    }

    [Theory]
    [InlineData("http://gateway.dev.test")]
    [InlineData("https://user:secret@gateway.dev.test")]
    [InlineData("https://gateway.dev.test/?next=1")]
    [InlineData("https://gateway.dev.test/#x")]
    [InlineData("ftp://gateway.dev.test")]
    [InlineData("gateway.dev.test")]
    [InlineData(@"C:\gateway")]
    public void Development_url_must_be_https_or_loopback_http(string url)
    {
        var backend = Dev(Env(("TEMNO_GATEWAY_URL", url)), FileText(DevFile));

        AssertOff(backend);
        Assert.Contains("TEMNO_GATEWAY_URL", backend.Problem);
        Assert.Contains("https", backend.Problem);
    }

    [Theory]
    [InlineData("http://127.0.0.1:8080")]
    [InlineData("http://localhost:5080/")]
    [InlineData("https://gateway.dev.test/prefix")]
    public void Development_url_accepts_https_and_loopback_http(string url)
    {
        var backend = Dev(Env(("TEMNO_GATEWAY_URL", url)), FileText(DevFile));

        Assert.Null(backend.Problem);
        Assert.Equal(url.TrimEnd('/'), backend.GatewayUrl);
    }

    [Fact]
    public void Core_assembly_carries_the_build_environment_and_the_accessor_reads_it()
    {
        var metadata = typeof(HostedEndpoints).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(a => a.Key == HostedEndpoints.BuildEnvironmentMetadataKey);
        Assert.True(TemnoEnvironments.TryParse(metadata.Value, out var compiled));
        Assert.Equal(compiled, HostedEndpoints.BuildEnvironment);
        // The tests always run as a development build: the default here, and -p:TemnoEnvironment=development in the
        // public repository, whose default is production.
        Assert.Equal(TemnoEnvironment.Development, HostedEndpoints.BuildEnvironment);
        // No metadata, or any value but the exact name, is development.
        Assert.Equal(TemnoEnvironment.Development, HostedEndpoints.ReadBuildEnvironment(typeof(Assert).Assembly));
    }

    [Fact]
    public void Stamp_sets_one_header_with_the_build_environment()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://gateway.dev.test/v1/models");
        request.Headers.TryAddWithoutValidation(TemnoEnvironments.HeaderName, "production");
        HostedEndpoints.StampEnvironment(request);
        HostedEndpoints.StampEnvironment(request);

        Assert.Equal(
            new[] { HostedEndpoints.BuildEnvironment.Name() },
            request.Headers.GetValues(TemnoEnvironments.HeaderName));
    }
}
