using Temno.Core.Models;
using Xunit;

namespace Temno.Core.Tests;

public class VolumeScaleTests
{
    [Fact]
    public void LinearFromDb_ZeroDb_IsUnity()
    {
        Assert.Equal(1.0, VolumeScale.LinearFromDb(0), 5);
    }

    [Fact]
    public void LinearFromDb_Floor_IsMute()
    {
        Assert.Equal(0, VolumeScale.LinearFromDb(VolumeScale.FloorDb));
        Assert.Equal(0, VolumeScale.LinearFromDb(VolumeScale.FloorDb - 10));
    }

    [Fact]
    public void DbFromLinear_Zero_IsFloor()
    {
        Assert.Equal(VolumeScale.FloorDb, VolumeScale.DbFromLinear(0));
    }

    [Fact]
    public void RoundTrip_NearUnity()
    {
        var linear = VolumeScale.LinearFromDb(-6);
        var back = VolumeScale.DbFromLinear(linear);
        Assert.Equal(-6, back, 3);
    }

    [Fact]
    public void Ceiling_Clamps()
    {
        var linear = VolumeScale.LinearFromDb(VolumeScale.CeilingDb + 20);
        Assert.True(linear <= VolumeScale.LinearFromDb(VolumeScale.CeilingDb) + 1e-9);
        Assert.Equal(VolumeScale.CeilingDb, VolumeScale.DbFromLinear(VolumeScale.LinearFromDb(VolumeScale.CeilingDb)), 5);
    }
}
