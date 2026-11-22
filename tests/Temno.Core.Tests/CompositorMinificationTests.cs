using Temno.Core.Models;
using Temno.Media;
using Temno.Media.Compositing;
using Temno.Media.Hardware;
using Xunit;
using Xunit.Abstractions;

namespace Temno.Core.Tests;

[Collection("CompositorTiming")]
public sealed class CompositorMinificationTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(384, 216)]
    [InlineData(448, 252)]
    [InlineData(192, 108)]
    public void FrequencyTarget_MatchesIntegratedFootprint(int width, int height)
    {
        using var source = Frame(width, height, (x,y) => [(byte)((x & 1) * 255), 0, 0, 255]);
        using var actual = new CpuCompositor().Render([new(source)], 128, 72);
        var expected = Reference(source, new Transform(), 128, 72);
        double squared = 0;
        for (var i = 0; i < actual.Data.Length; i += 4)
            squared += Math.Pow(actual.Data[i] - expected[i], 2);
        var rmse = Math.Sqrt(squared / (128 * 72));
        output.WriteLine($"{width / 128.0}x area-reference RMSE={rmse}");
        Assert.InRange(rmse, 0, 1);
    }

    [Fact]
    public void ThreeByThreeFootprint_ReadsEverySourcePixel()
    {
        for (var impulse = 0; impulse < 9; impulse++)
        {
            using var source = Frame(3, 3, (x,y) => [(byte)(y * 3 + x == impulse ? 255 : 0), 0, 0, 255]);
            using var actual = new CpuCompositor().Render([new(source)], 1, 1);
            Assert.InRange(actual.Data[0], 27, 28);
        }
    }

    [Theory]
    [InlineData(192,108,64,36,false,false,false)]
    [InlineData(224,126,64,36,true,false,false)]
    [InlineData(100,80,64,52,true,true,true)]
    [InlineData(128,96,160,120,false,true,true)]
    // WP-405: a picture keeps its aspect inside its box, so both axes share one ratio (3.5x here).
    [InlineData(224,112,64,32,true,true,false)]
    public void ArbitraryRatios_AgreeWithIndependentIntegral(int sw,int sh,int w,int h,bool flipH,bool flipV,bool fractional)
    {
        using var source = Noise(sw,sh);
        var transform = new Transform { FlipHorizontal=flipH,FlipVertical=flipV,
            CenterX=fractional?0.513:0.5,CenterY=fractional?0.487:0.5,
            Width=fractional?0.77:1,Height=fractional?0.77:1 };
        using var actual = new CpuCompositor().Render([new(source,transform:transform)],w,h);
        var expected = Reference(source,transform,w,h);
        Assert.InRange(Delta(actual.Data,expected),0,2);
        // Coverage is independent of the numerical sample tolerance.
        for(var y=0;y<h;y++)for(var x=0;x<w;x++)
        {
            var nx=(x+0.5-transform.CenterX*w)/(transform.Width*w)+0.5;
            var ny=(y+0.5-transform.CenterY*h)/(transform.Height*h)+0.5;
            if(nx<0||nx>=1||ny<0||ny>=1)Assert.Equal(new byte[]{0,0,0,255},actual.Data.AsSpan((y*w+x)*4,4).ToArray());
            Assert.Equal(255,actual.Data[(y*w+x)*4+3]);
        }
    }

    [Theory]
    [InlineData(64,32)]
    [InlineData(480,270)]
    public void ExactTwoTimes_RetainsTwoStageBoxBytes(int w,int h)
    {
        using var source=Noise(w*2,h*2);
        using var actual=new CpuCompositor().Render([new(source)],w,h);
        for(var y=0;y<h;y++)for(var x=0;x<w;x++)for(var c=0;c<3;c++)
        {
            var i=(2*y*source.Width+2*x)*4+c;
            var top=(source.Data[i]+source.Data[i+4])/2;
            var bottom=(source.Data[i+source.Width*4]+source.Data[i+source.Width*4+4])/2;
            Assert.Equal((byte)((top+bottom)/2),actual.Data[(y*w+x)*4+c]);
        }
    }

    [Fact]
    public void LargeFootprint_RetainsConstantWithoutOverflow()
    {
        using var source=Frame(1024,512,(_,_)=>[255,137,83,255]);
        using var actual=new CpuCompositor().Render([new(source)],1,1);
        Assert.Equal(new byte[]{255,137,83,255},actual.Data);
    }

    [Fact]
    public void SubpixelDestination_ClampsFootprintBeforeIntegerConversion()
    {
        using var source=Frame(4,1,(x,_)=>[(byte)(x==0?10:x==3?210:0),0,0,255]);
        using var actual=new CpuCompositor().Render([new(source,transform:new Transform{Width=1e-12})],1,1);
        Assert.Equal(110,actual.Data[0]);
    }

    [Theory]
    [InlineData(0,255,1,"normal",0)]
    [InlineData(0,137,0.63,"normal",1)]
    [InlineData(0,137,0.8,"screen",1)]
    [InlineData(0,137,0.8,"hue",2)]
    [InlineData(18.25,255,1,"normal",1)]
    [InlineData(37.5,137,0.63,"normal",1)]
    [InlineData(-18.25,137,0.8,"screen",1)]
    [InlineData(37.5,137,0.8,"luminosity",2)]
    public void ActualGpu_MatchesAreaReferenceWithinExistingBounds(double rotation,int alpha,double opacity,string blend,int tolerance)
    {
        var caps=HardwareCapabilities.Probe();
        if(!caps.D3D11Available){output.WriteLine("No D3D11 device; no GPU parity claim.");return;}
        using var gpu=new GpuCompositor(caps:caps);
        Assert.True(gpu.IsGpuReady,gpu.LastGpuError);
        using var source=Noise(224,126,alpha,bgra:true);
        using var background=Frame(64,36,(_,_)=>[31,101,177,255]);
        var transform=new Transform{Rotation=rotation,FlipHorizontal=true,FlipVertical=true,CenterX=0.47,CenterY=0.53,Width=0.83,Height=0.91};
        CompositorLayer[] layers=[new(background),new(source,opacity:opacity,blendMode:blend,transform:transform)];
        using var cpu=new CpuCompositor().Render(layers,64,36);
        using var actual=gpu.Render(layers,64,36);
        Assert.Null(gpu.LastGpuError);
        var delta=Delta(cpu.Data,actual.Data);
        output.WriteLine($"{gpu.AdapterDescription}: rotation={rotation}, mode={blend}, max delta={delta}, bound={tolerance}");
        Assert.InRange(delta,0,tolerance);
        using var replay=gpu.Render(layers,64,36);
        Assert.Equal(actual.Data,replay.Data);
    }

    internal static VideoFrame Frame(int w,int h,Func<int,int,byte[]> pixel)
    {
        var data=new byte[w*h*4];
        for(var y=0;y<h;y++)for(var x=0;x<w;x++)pixel(x,y).CopyTo(data,(y*w+x)*4);
        return new(w,h,data,"rgba");
    }

    [Fact]
    public void LargeRotatedMinification_RetainsOneLsbGpuBound()
    {
        var caps=HardwareCapabilities.Probe();
        if(!caps.D3D11Available)return;
        using var gpu=new GpuCompositor(caps:caps);
        Assert.True(gpu.IsGpuReady,gpu.LastGpuError);
        using var source=Noise(3456,1944);
        foreach(var angle in new[]{18.25,37.5,89.125})
        {
            var transform=new Transform{Rotation=angle,Width=0.83,Height=0.91,CenterX=0.47,CenterY=0.53};
            using var cpu=new CpuCompositor().Render([new(source,transform:transform)],960,540);
            using var actual=gpu.Render([new(source,transform:transform)],960,540);
            Assert.Null(gpu.LastGpuError);
            var delta=Delta(cpu.Data,actual.Data);
            output.WriteLine($"Large rotated {angle}: {delta} LSB");
            Assert.InRange(delta,0,1);
        }
    }

    private static VideoFrame Noise(int w,int h,int alpha=255,bool bgra=false)
    {
        var random=new Random(217);
        var data=new byte[w*h*4];random.NextBytes(data);
        for(var i=3;i<data.Length;i+=4)data[i]=(byte)alpha;
        return new(w,h,data,bgra?"bgra":"rgba");
    }

    internal static int Delta(byte[] a,byte[] b)=>a.Zip(b).Max(p=>Math.Abs(p.First-p.Second));

    internal static byte[] Reference(VideoFrame source,Transform transform,int w,int h)
    {
        var result=new byte[w*h*4];
        for(var y=0;y<h;y++)for(var x=0;x<w;x++)
        {
            var index=(y*w+x)*4;result[index+3]=255;
            var nx=(x+0.5-transform.CenterX*w)/(transform.Width*w)+0.5;
            var ny=(y+0.5-transform.CenterY*h)/(transform.Height*h)+0.5;
            if(nx<0||nx>=1||ny<0||ny>=1)continue;
            if(transform.FlipHorizontal)nx=1-nx;
            if(transform.FlipVertical)ny=1-ny;
            var horizontal=Weights(nx*source.Width,source.Width/(transform.Width*w),source.Width);
            var vertical=Weights(ny*source.Height,source.Height/(transform.Height*h),source.Height);
            var sampled=new int[4];
            for(var c=0;c<4;c++)
            {
                double sum=0;
                foreach(var (sy,wy) in vertical)
                {
                    double row=0;
                    foreach(var (sx,wx) in horizontal)row+=source.Data[(sy*source.Width+sx)*4+c]*wx;
                    sum+=Math.Floor(row+1e-9)*wy;
                }
                sampled[c]=(int)Math.Floor(sum+1e-9);
            }
            for(var c=0;c<3;c++)result[index+c]=(byte)(sampled[c]*sampled[3]/255);
        }
        return result;
    }

    private static List<(int index,double weight)> Weights(double center,double width,int extent)
    {
        var weights=new List<(int,double)>();
        if(width<=1)
        {
            var raw=(int)Math.Floor(center-0.5);var frac=center-0.5-raw;
            var first=Math.Clamp(raw,0,extent-1);
            weights.Add((first,1-frac));weights.Add((Math.Min(first+1,extent-1),frac));return weights;
        }
        var left=center-width/2;var right=center+width/2;
        // Integrate each source pixel's constant value, including clamped edge extension.
        for(var source=0;source<extent;source++)
        {
            var start=source==0?double.NegativeInfinity:source;
            var end=source==extent-1?double.PositiveInfinity:source+1;
            var overlap=Math.Max(0,Math.Min(right,end)-Math.Max(left,start));
            if(overlap>0)weights.Add((source,overlap/width));
        }
        return weights;
    }
}
