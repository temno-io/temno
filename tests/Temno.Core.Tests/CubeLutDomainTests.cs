using Temno.Core.Editing;
using Xunit;

namespace Temno.Core.Tests;

/// <summary>WP-241: DOMAIN_MIN/DOMAIN_MAX must be three finite components or absent.</summary>
public sealed class CubeLutDomainTests
{
    private const string IdentityBody = """
        0 0 0
        1 0 0
        0 1 0
        1 1 0
        0 0 1
        1 0 1
        0 1 1
        1 1 1
        """;

    private static string Cube(string header) =>
        $"LUT_3D_SIZE 2\n{header}{IdentityBody}";

    private static void AssertNearNeutral(CubeLut lut)
    {
        lut.Sample(0.5, 0.5, 0.5, out var r, out var g, out var b);
        Assert.InRange(r, 0.4, 0.6);
        Assert.InRange(g, 0.4, 0.6);
        Assert.InRange(b, 0.4, 0.6);
    }

    [Fact]
    public void TruncatedDomainMin_AuditRegression_IsRejected()
    {
        // X-2 / release-audit-review harness: DOMAIN_MIN 0 padded G/B to 1 and
        // turned neutral (0.5,0.5,0.5) into (0.5,0,0). Must fail cleanly now.
        var text = Cube("DOMAIN_MIN 0\nDOMAIN_MAX 1 1 1\n");
        var lut = CubeLut.TryParse(text);
        Assert.Null(lut);
    }

    [Theory]
    [InlineData("DOMAIN_MIN 0 0\n")]
    [InlineData("DOMAIN_MAX 1\n")]
    [InlineData("DOMAIN_MIN 0 0 0 0\n")]
    [InlineData("DOMAIN_MIN 0 0 NaN\n")]
    [InlineData("DOMAIN_MAX 1 1 Infinity\n")]
    [InlineData("DOMAIN_MIN 0 0 notanumber\n")]
    public void MalformedDomainDirective_IsRejected(string header)
    {
        Assert.Null(CubeLut.TryParse(Cube(header)));
    }

    [Theory]
    [InlineData("DOMAIN_MIN 0 0 0\nDOMAIN_MAX 0 1 1\n")] // zero R span
    [InlineData("DOMAIN_MIN 1 0 0\nDOMAIN_MAX 0 1 1\n")] // reversed R
    [InlineData("DOMAIN_MIN 0 0.5 0\nDOMAIN_MAX 1 0.5 1\n")] // zero G span
    public void ZeroOrReversedDomainSpan_IsRejected(string header)
    {
        Assert.Null(CubeLut.TryParse(Cube(header)));
    }

    [Fact]
    public void OmittedDomain_DefaultsToUnitCube_IdentitySample()
    {
        var lut = CubeLut.TryParse(Cube(""));
        Assert.NotNull(lut);
        AssertNearNeutral(lut!);
    }

    [Fact]
    public void ExplicitUnitDomain_MatchesOmittedDefault()
    {
        var omitted = CubeLut.TryParse(Cube(""));
        var explicitUnit = CubeLut.TryParse(Cube("DOMAIN_MIN 0 0 0\nDOMAIN_MAX 1 1 1\n"));
        Assert.NotNull(omitted);
        Assert.NotNull(explicitUnit);

        omitted!.Sample(0.5, 0.5, 0.5, out var r0, out var g0, out var b0);
        explicitUnit!.Sample(0.5, 0.5, 0.5, out var r1, out var g1, out var b1);
        Assert.Equal(r0, r1, 6);
        Assert.Equal(g0, g1, 6);
        Assert.Equal(b0, b1, 6);
    }

    [Fact]
    public void NonDefaultDomain_NormalizesTableIntoUnitCube()
    {
        // Table written in [−1,1]; mid lattice point is identity grey after normalize.
        var text = """
            LUT_3D_SIZE 2
            DOMAIN_MIN -1 -1 -1
            DOMAIN_MAX 1 1 1
            -1 -1 -1
            1 -1 -1
            -1 1 -1
            1 1 -1
            -1 -1 1
            1 -1 1
            -1 1 1
            1 1 1
            """;
        var lut = CubeLut.TryParse(text);
        Assert.NotNull(lut);
        AssertNearNeutral(lut!);
    }

    [Fact]
    public void IntensityBlend_RemainsLinearOnIdentity()
    {
        var lut = CubeLut.TryParse(Cube("DOMAIN_MIN 0 0 0\nDOMAIN_MAX 1 1 1\n"));
        Assert.NotNull(lut);
        double r = 0.25, g = 0.5, b = 0.75;
        lut!.Apply(ref r, ref g, ref b, intensity: 0.5);
        // Identity LUT: output equals input, so half intensity is unchanged.
        Assert.Equal(0.25, r, 6);
        Assert.Equal(0.5, g, 6);
        Assert.Equal(0.75, b, 6);
    }

    [Fact]
    public void TryParse_DoesNotThrow_OnMalformedDomain()
    {
        var ex = Record.Exception(() => CubeLut.TryParse(Cube("DOMAIN_MIN 0\n")));
        Assert.Null(ex);
        Assert.Null(CubeLut.TryParse(Cube("DOMAIN_MIN 0\n")));
    }
}
