using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Temno.Media;
using Xunit;

namespace Temno.Core.Tests;

/// <summary>
/// WP-401: the FFmpeg advisories in docs/verification/wp-321/native-disposition.md, replayed against
/// the FFmpeg the resolver finds. Each fixture is generated here, every process is bounded, and none
/// opens a network address. The pinned build is win-x64, so other hosts skip.
/// </summary>
public sealed class NativeMediaAdvisoryTests : IDisposable
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(20);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "temno-wp401-" + Guid.NewGuid().ToString("N"));

    public NativeMediaAdvisoryTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private static bool BuildPresent =>
        OperatingSystem.IsWindows()
        && File.Exists(FFmpegResolver.GetFFmpegPath())
        && File.Exists(FFmpegResolver.GetFFprobePath());

    [Fact]
    public void TheResolvedFfmpeg_OpensOnlyLocalFiles_AndLacksTheAdvisoryComponents()
    {
        if (!BuildPresent) return;

        Assert.Contains("ffmpeg version 9.1-temno1", Text("-version"), StringComparison.Ordinal);

        var protocols = Text("-protocols").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var input = protocols.SkipWhile(t => t != "Input:").Skip(1).TakeWhile(t => t != "Output:").ToArray();
        Assert.Equal(["file", "pipe"], input);

        var demuxers = Names("-demuxers");
        foreach (var name in new[] { "dash", "imf", "hls", "rtsp", "sdp", "rtp", "sap", "cdxl", "pcx_pipe", "sunrast_pipe" })
            Assert.DoesNotContain(name, demuxers);
        Assert.Contains("mov,mp4,m4a,3gp,3g2,mj2", demuxers);
        Assert.Contains("matroska,webm", demuxers);
        Assert.Contains("avi", demuxers);

        var decoders = Names("-decoders");
        foreach (var name in new[] { "cdxl", "g2m", "pcx", "sunrast" })
            Assert.DoesNotContain(name, decoders);
        foreach (var name in new[] { "h264", "hevc", "vp9", "libdav1d", "prores", "huffyuv", "png", "mjpeg", "webp", "aac", "mp3", "flac", "opus" })
            Assert.Contains(name, decoders);

        var encoders = Names("-encoders");
        foreach (var name in new[] { "libx264", "libx265", "prores_ks", "aac", "png" })
            Assert.Contains(name, encoders);
    }

    [Fact]
    public async Task Probe_ReadsAPsshFloodInLinearTime()
    {
        // CVE-2026-107678. Every pssh after a track re-parsed the whole list and freed it
        // recursively: 9.0.2 spent 66 s on these 20,000 boxes (641 KB) and 551 s on 45,000.
        if (!BuildPresent) return;
        var basePath = Path.Combine(_root, "base.mp4");
        Make("-f", "lavfi", "-i", "color=c=black:s=16x16:d=0.04:r=25", "-frames:v", "1", "-c:v", "libx264",
            "-pix_fmt", "yuv420p", basePath);
        var data = File.ReadAllBytes(basePath);
        var moov = TopLevelBoxes(data).Single(b => b.Kind == "moov");
        Assert.Equal(data.Length, moov.Offset + moov.Size);
        var moovAt = moov.Offset;

        var pssh = Box("pssh", [0, 0, 0, 0, .. Convert.FromHexString("edef8ba979d64acea3c827dcd51d21ed"), 0, 0, 0, 0]);
        using var flood = new MemoryStream();
        flood.Write(data, 0, moovAt);
        var body = data.AsSpan(moovAt + 8).ToArray();
        WriteBoxHeader(flood, "moov", 8 + body.Length + pssh.Length * 20_000);
        flood.Write(body);
        for (var i = 0; i < 20_000; i++)
            flood.Write(pssh);
        var path = Path.Combine(_root, "pssh-flood.mp4");
        File.WriteAllBytes(path, flood.ToArray());

        using var cts = new CancellationTokenSource(Bound);
        var stopwatch = Stopwatch.StartNew();
        var result = await new FfmpegMediaProbe().ProbeAsync(path, cts.Token);
        Assert.True(result.HasVideo);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(10), $"probe took {stopwatch.Elapsed}");
    }

    [Theory]
    [InlineData("dash.mp4")]   // CVE-2026-107677: an empty SegmentTemplate looped forever in 9.0.2
    [InlineData("hls.m3u8")]   // a playlist read a file outside the project in 9.0.2
    [InlineData("imf.mp4")]    // CVE-2026-120210: ASSETMAP "../" opened an MXF outside the package in 9.0.2
    [InlineData("sdp.mp4")]    // RTSP/RTP family: 9.0.2 already refused rtp here; the demuxer is now gone too
    public async Task Probe_RefusesManifestsThatReachOtherFilesOrTheNetwork(string name)
    {
        if (!BuildPresent) return;
        var outside = Directory.CreateDirectory(Path.Combine(_root, "outside")).FullName;
        var project = Directory.CreateDirectory(Path.Combine(_root, "project")).FullName;
        var path = Path.Combine(project, name);
        switch (name)
        {
            case "dash.mp4":
                File.WriteAllText(path, "<?xml version=\"1.0\"?><MPD xmlns=\"urn:mpeg:dash:schema:mpd:2011\" type=\"static\" "
                    + "mediaPresentationDuration=\"PT10S\" minBufferTime=\"PT1S\" profiles=\"urn:mpeg:dash:profile:isoff-on-demand:2011\">"
                    + "<Period><AdaptationSet mimeType=\"video/mp4\"><SegmentTemplate media=\"\" duration=\"1\" timescale=\"1\" startNumber=\"1\"/>"
                    + "<Representation id=\"v\" bandwidth=\"1000\" width=\"16\" height=\"16\" codecs=\"avc1.42c00a\"/>"
                    + "</AdaptationSet></Period></MPD>\n");
                break;
            case "hls.m3u8":
                var secret = Path.Combine(outside, "secret.mp4");
                Make("-f", "lavfi", "-i", "testsrc2=s=64x64:r=25:d=1", "-c:v", "libx264", "-pix_fmt", "yuv420p", secret);
                File.WriteAllText(path, $"#EXTM3U\n#EXT-X-TARGETDURATION:1\n#EXTINF:1,\n{secret.Replace('\\', '/')}\n#EXT-X-ENDLIST\n");
                break;
            case "imf.mp4":
                Make("-f", "lavfi", "-i", "testsrc2=s=64x64:r=25:d=1", "-c:v", "mpeg2video", "-f", "mxf", Path.Combine(outside, "track.mxf"));
                const string asset = "urn:uuid:11111111-1111-4111-8111-111111111111";
                File.WriteAllText(Path.Combine(project, "ASSETMAP.xml"),
                    "<?xml version=\"1.0\"?><AssetMap xmlns=\"http://www.smpte-ra.org/schemas/429-9/2007/AM\">"
                    + "<Id>urn:uuid:22222222-2222-4222-8222-222222222222</Id><AssetList><Asset>"
                    + $"<Id>{asset}</Id><ChunkList><Chunk><Path>../outside/track.mxf</Path></Chunk></ChunkList>"
                    + "</Asset></AssetList></AssetMap>\n");
                File.WriteAllText(path,
                    "<?xml version=\"1.0\"?><CompositionPlaylist xmlns=\"http://www.smpte-ra.org/schemas/2067-3/2016\" "
                    + "xmlns:cc=\"http://www.smpte-ra.org/schemas/2067-2/2016\"><Id>urn:uuid:33333333-3333-4333-8333-333333333333</Id>"
                    + "<ContentTitle>x</ContentTitle><EditRate>25 1</EditRate><SegmentList><Segment>"
                    + "<Id>urn:uuid:44444444-4444-4444-8444-444444444444</Id><SequenceList><cc:MainImageSequence>"
                    + "<Id>urn:uuid:55555555-5555-4555-8555-555555555555</Id><TrackId>urn:uuid:66666666-6666-4666-8666-666666666666</TrackId>"
                    + "<ResourceList><Resource><Id>urn:uuid:77777777-7777-4777-8777-777777777777</Id><EditRate>25 1</EditRate>"
                    + "<IntrinsicDuration>25</IntrinsicDuration><SourceDuration>25</SourceDuration>"
                    + $"<TrackFileId>{asset}</TrackFileId></Resource></ResourceList></cc:MainImageSequence>"
                    + "</SequenceList></Segment></SegmentList></CompositionPlaylist>\n");
                break;
            case "sdp.mp4":
                File.WriteAllText(path, "v=0\no=- 0 0 IN IP4 127.0.0.1\ns=x\nc=IN IP4 127.0.0.1\nt=0 0\n"
                    + "m=video 5004 RTP/AVP 96\na=rtpmap:96 H264/90000\n");
                break;
        }

        using var cts = new CancellationTokenSource(Bound);
        await Assert.ThrowsAsync<InvalidOperationException>(() => new FfmpegMediaProbe().ProbeAsync(path, cts.Token));
    }

    [Theory]
    [InlineData("pcx", "image.pcx", "rgb24")]       // CVE-2026-120244
    [InlineData("sunrast", "image.ras", "monob")]   // CVE-2026-120245
    public async Task ExcludedImageDecoders_DecodeNoPixels(string encoder, string name, string pixelFormat)
    {
        // A valid image, which 9.0.2 decodes: only a build without the decoder returns no pixels.
        if (!BuildPresent) return;
        var path = Path.Combine(_root, name);
        Make("-f", "lavfi", "-i", "testsrc2=s=64x64", "-frames:v", "1", "-c:v", encoder, "-pix_fmt", pixelFormat, "-update", "1", path);

        using var cts = new CancellationTokenSource(Bound);
        await Assert.ThrowsAnyAsync<Exception>(() => new FfmpegFrameSource().GetFrameAsync(path, 0, cts.Token));
    }

    [Fact]
    public void Avutil_WritesHdr10PlusSaturationBitsWithoutUninitialisedBytes()
    {
        // CVE-2026-107676: with tone_mapping_flag 0, 9.0.2's av_dynamic_hdr_plus_to_t35 skipped the
        // saturation bits and returned its last byte straight from av_malloc (0xAB below).
        if (!BuildPresent) return;
        var directory = Path.GetDirectoryName(FFmpegResolver.GetFFmpegPath())!;
        var avutil = NativeLibrary.Load(Directory.GetFiles(directory, "avutil-*.dll").Single());
        try
        {
            var alloc = Export<AllocDelegate>(avutil, "av_dynamic_hdr_plus_alloc");
            var fromT35 = Export<FromT35Delegate>(avutil, "av_dynamic_hdr_plus_from_t35");
            var toT35 = Export<ToT35Delegate>(avutil, "av_dynamic_hdr_plus_to_t35");
            var malloc = Export<MallocDelegate>(avutil, "av_malloc");
            var free = Export<FreeDelegate>(avutil, "av_free");

            // One window, no peak tables, tone mapping off, saturation mapping on (weight 42): 129 bits.
            var payload = Bits([(8, 1), (2, 1), (27, 4000), (1, 0), (17, 1000), (17, 900), (17, 800), (17, 700),
                (4, 0), (10, 5), (1, 0), (1, 0), (1, 1), (6, 42)]);
            Assert.Equal(17, payload.Length);
            for (var attempt = 0; attempt < 8; attempt++)
            {
                for (var i = 0; i < 64; i++)
                {
                    var block = malloc((nuint)payload.Length);
                    Marshal.Copy(Enumerable.Repeat((byte)0xAB, payload.Length).ToArray(), 0, block, payload.Length);
                    free(block);
                }

                var metadata = alloc(IntPtr.Zero);
                Assert.Equal(0, fromT35(metadata, payload, (nuint)payload.Length));
                var output = IntPtr.Zero;
                nuint size = 0;
                Assert.Equal(0, toT35(metadata, ref output, ref size));
                var written = new byte[(int)size];
                Marshal.Copy(output, written, 0, written.Length);
                free(output);
                free(metadata);
                Assert.Equal(Convert.ToHexString(payload), Convert.ToHexString(written));
            }
        }
        finally
        {
            NativeLibrary.Free(avutil);
        }
    }

    [Fact]
    public void Huffyuv_TruncatedFirstRow_DecodesTheSameWhateverFrameCameBefore()
    {
        // CVE-2026-120247: a packed-BGR row whose bits ran out kept the row buffer's old contents,
        // which were the previous frame's residuals, so frame 2 decoded differently after a
        // different frame 1.
        if (!BuildPresent) return;
        const int width = 256, height = 8;
        (byte[] Strf, List<byte[]> Frames) Encode(string key, string graph)
        {
            var path = Path.Combine(_root, $"hfyu-{key}.avi");
            Make("-f", "lavfi", "-i", graph, "-frames:v", "1", "-c:v", "huffyuv", "-pix_fmt", "rgb24", "-threads", "1", path);
            return AviParts(File.ReadAllBytes(path));
        }

        var x = Encode("x", $"testsrc2=s={width}x{height}:r=25");
        var y = Encode("y", $"mandelbrot=s={width}x{height}:r=25");
        var noise = Encode("noise", $"color=c=gray:s={width}x{height}:r=25,noise=alls=100:allf=u");
        Assert.Equal(x.Strf, y.Strf);
        Assert.Equal(x.Strf, noise.Strf);
        // The decoder drops packets under width*height/8 bytes; a noise frame spends that many inside
        // its first row. Whole 32-bit words keep the byte-swapped packet copy free of a stale tail.
        var truncated = noise.Frames[0][..(width * height / 8)];

        string Frame2(string key, byte[] first)
        {
            var path = Path.Combine(_root, $"hostile-{key}.avi");
            File.WriteAllBytes(path, Avi(x.Strf, [first, truncated], width, height));
            var (exit, stdout) = Run(FFmpegResolver.GetFFmpegPath(),
                ["-hide_banner", "-loglevel", "error", "-threads", "1", "-i", path, "-f", "rawvideo", "-pix_fmt", "rgb24", "-"]);
            Assert.Equal(0, exit);
            Assert.Equal(2 * width * height * 3, stdout.Length);
            return Convert.ToHexString(SHA256.HashData(stdout.AsSpan(width * height * 3)));
        }

        Assert.Equal(Frame2("x", x.Frames[0]), Frame2("y", y.Frames[0]));
    }

    private delegate IntPtr AllocDelegate(IntPtr size);
    private delegate int FromT35Delegate(IntPtr metadata, byte[] data, nuint size);
    // *data must be NULL on entry, or av_dynamic_hdr_plus_to_t35 writes into it as a caller buffer.
    private delegate int ToT35Delegate(IntPtr metadata, ref IntPtr data, ref nuint size);
    private delegate IntPtr MallocDelegate(nuint size);
    private delegate void FreeDelegate(IntPtr pointer);

    private static T Export<T>(IntPtr library, string name) where T : Delegate =>
        Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(library, name));

    private static byte[] Bits((int Width, int Value)[] fields)
    {
        var bits = new StringBuilder();
        foreach (var (w, v) in fields)
            bits.Append(Convert.ToString(v, 2).PadLeft(w, '0'));
        while (bits.Length % 8 != 0)
            bits.Append('0');
        return Enumerable.Range(0, bits.Length / 8).Select(i => Convert.ToByte(bits.ToString(i * 8, 8), 2)).ToArray();
    }

    private static string Text(string flag) =>
        Encoding.UTF8.GetString(Run(FFmpegResolver.GetFFmpegPath(), ["-hide_banner", flag]).Stdout);

    // A list is a legend, a dashed rule, then one "<flags> <name> <description>" line per entry.
    private static HashSet<string> Names(string flag) =>
        Text(flag).Split('\n').SkipWhile(l => !l.Trim().All(c => c == '-') || l.Trim().Length == 0).Skip(1)
            .Select(l => l.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            .Where(t => t.Length >= 2).Select(t => t[1]).ToHashSet(StringComparer.Ordinal);

    private static void Make(params string[] args)
    {
        var (exit, _) = Run(FFmpegResolver.GetFFmpegPath(), ["-hide_banner", "-loglevel", "error", "-y", .. args]);
        Assert.Equal(0, exit);
    }

    private static (int Exit, byte[] Stdout) Run(string exe, string[] args)
    {
        using var process = new Process();
        process.StartInfo.FileName = exe;
        foreach (var arg in args)
            process.StartInfo.ArgumentList.Add(arg);
        process.StartInfo.UseShellExecute = false;
        process.StartInfo.RedirectStandardOutput = true;
        process.StartInfo.RedirectStandardError = true;
        process.StartInfo.CreateNoWindow = true;
        process.Start();
        using var stdout = new MemoryStream();
        var copy = process.StandardOutput.BaseStream.CopyToAsync(stdout);
        var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(Bound))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException($"{Path.GetFileName(exe)} {string.Join(' ', args)} ran past {Bound}.");
        }

        copy.Wait(Bound);
        stderr.Wait(Bound);
        return (process.ExitCode, stdout.ToArray());
    }

    private static IEnumerable<(string Kind, int Offset, int Size)> TopLevelBoxes(byte[] data)
    {
        for (var offset = 0; offset + 8 <= data.Length;)
        {
            var size = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(offset));
            yield return (Encoding.ASCII.GetString(data, offset + 4, 4), offset, size);
            offset += size;
        }
    }

    private static byte[] Box(string kind, byte[] payload)
    {
        using var stream = new MemoryStream();
        WriteBoxHeader(stream, kind, 8 + payload.Length);
        stream.Write(payload);
        return stream.ToArray();
    }

    private static void WriteBoxHeader(Stream stream, string kind, int size)
    {
        Span<byte> header = stackalloc byte[8];
        BinaryPrimitives.WriteInt32BigEndian(header, size);
        Encoding.ASCII.GetBytes(kind, header[4..]);
        stream.Write(header);
    }

    private static IEnumerable<(string FourCc, int Body, int Size)> RiffChunks(byte[] data, int start, int end)
    {
        for (var at = start; at + 8 <= end;)
        {
            var size = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(at + 4));
            yield return (Encoding.ASCII.GetString(data, at, 4), at + 8, size);
            at += 8 + size + (size & 1);
        }
    }

    private static (byte[] Strf, List<byte[]> Frames) AviParts(byte[] data)
    {
        byte[]? strf = null;
        var frames = new List<byte[]>();
        foreach (var (fourCc, body, size) in RiffChunks(data, 12, data.Length))
        {
            if (fourCc != "LIST")
                continue;
            var kind = Encoding.ASCII.GetString(data, body, 4);
            if (kind == "hdrl")
            {
                var strl = RiffChunks(data, body + 4, body + size).First(c => c.FourCc == "LIST" && Encoding.ASCII.GetString(data, c.Body, 4) == "strl");
                var chunk = RiffChunks(data, strl.Body + 4, strl.Body + strl.Size).First(c => c.FourCc == "strf");
                strf = data[chunk.Body..(chunk.Body + chunk.Size)];
            }
            else if (kind == "movi")
            {
                frames.AddRange(RiffChunks(data, body + 4, body + size).Where(c => c.FourCc == "00dc").Select(c => data[c.Body..(c.Body + c.Size)]));
            }
        }

        return (strf ?? throw new InvalidDataException("No strf chunk."), frames);
    }

    private static byte[] Avi(byte[] strf, byte[][] frames, int width, int height)
    {
        static byte[] Chunk(string fourCc, byte[] body)
        {
            var padded = new byte[8 + body.Length + (body.Length & 1)];
            Encoding.ASCII.GetBytes(fourCc, padded);
            BinaryPrimitives.WriteInt32LittleEndian(padded.AsSpan(4), body.Length);
            body.CopyTo(padded, 8);
            return padded;
        }

        static byte[] List(string kind, params byte[][] parts) => Chunk("LIST", [.. Encoding.ASCII.GetBytes(kind), .. parts.SelectMany(p => p)]);
        static byte[] Ints(params int[] values) => values.SelectMany(BitConverter.GetBytes).ToArray();

        var avih = Ints(40000, 0, 0, 0x10, frames.Length, 0, 1, 0, width, height, 0, 0, 0, 0);
        byte[] strh = [.. Encoding.ASCII.GetBytes("vidsHFYU"), .. Ints(0, 0, 0, 1, 25, 0, frames.Length, 0, -1, 0),
            .. BitConverter.GetBytes((short)0), .. BitConverter.GetBytes((short)0),
            .. BitConverter.GetBytes((short)width), .. BitConverter.GetBytes((short)height)];
        var movi = new List<byte>();
        var index = new List<byte>();
        foreach (var frame in frames)
        {
            index.AddRange([.. Encoding.ASCII.GetBytes("00dc"), .. Ints(0x10, 4 + movi.Count, frame.Length)]);
            movi.AddRange(Chunk("00dc", frame));
        }

        var hdrl = List("hdrl", Chunk("avih", avih), List("strl", Chunk("strh", strh), Chunk("strf", strf)));
        return Chunk("RIFF", [.. Encoding.ASCII.GetBytes("AVI "), .. hdrl, .. List("movi", [.. movi]), .. Chunk("idx1", [.. index])]);
    }
}
