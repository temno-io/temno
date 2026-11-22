using Temno.Media;
using Xunit;

namespace Temno.Core.Tests;

public class FFmpegResolverTests
{
    [Fact]
    public void Direct_environment_path_wins()
    {
        using var tree = new Tree();
        var pinned = tree.File("pinned", "ffmpeg.exe");
        var bundled = tree.File("app", "ffmpeg", "ffmpeg.exe");

        var resolved = FFmpegResolver.Resolve(
            "ffmpeg",
            tree.Path("app"),
            Env(("TEMNO_FFMPEG_PATH", pinned)),
            windows: true,
            pathEnv: null);

        Assert.Equal(pinned, resolved);
        Assert.NotEqual(bundled, resolved);
    }

    [Fact]
    public void Directory_environment_is_second()
    {
        using var tree = new Tree();
        var dir = tree.Dir("custom");
        var binary = tree.File("custom", "ffprobe.exe");

        var resolved = FFmpegResolver.Resolve(
            "ffprobe",
            tree.Path("app"),
            Env(("TEMNO_FFMPEG_DIR", dir)),
            windows: true,
            pathEnv: null);

        Assert.Equal(binary, resolved);
    }

    [Fact]
    public void Missing_environment_path_falls_through_to_the_bundled_file()
    {
        using var tree = new Tree();
        var bundled = tree.File("app", "ffmpeg", "ffmpeg.exe");

        var resolved = FFmpegResolver.Resolve(
            "ffmpeg",
            tree.Path("app"),
            Env(("TEMNO_FFMPEG_PATH", tree.Path("missing.exe"))),
            windows: true,
            pathEnv: null);

        Assert.Equal(bundled, resolved);
    }

    [Fact]
    public void Windows_does_not_use_PATH()
    {
        using var tree = new Tree();
        var sentinel = tree.File("pathdir", "ffmpeg.exe");
        var start = tree.Path("app");
        Directory.CreateDirectory(start);

        var resolved = FFmpegResolver.Resolve(
            "ffmpeg",
            start,
            _ => null,
            windows: true,
            pathEnv: tree.Path("pathdir"),
            maxParentHops: 2);

        Assert.NotEqual(sentinel, resolved);
        Assert.Equal(Path.Combine(start, "ffmpeg", "ffmpeg.exe"), resolved);
        Assert.False(File.Exists(resolved));
    }

    [Fact]
    public void Development_checkout_is_found_by_walking_up()
    {
        using var tree = new Tree();
        var pinned = tree.File("tools", "ffmpeg", "win-x64", "ffmpeg.exe");
        var start = tree.Dir("src", "Temno.App", "bin");

        var resolved = FFmpegResolver.Resolve(
            "ffmpeg",
            start,
            _ => null,
            windows: true,
            pathEnv: tree.Path("pathdir"),
            maxParentHops: 3);

        Assert.Equal(pinned, resolved);
    }

    [Fact]
    public void Bundled_file_wins_over_the_development_checkout()
    {
        using var tree = new Tree();
        var bundled = tree.File("src", "bin", "ffmpeg", "ffmpeg.exe");
        tree.File("tools", "ffmpeg", "win-x64", "ffmpeg.exe");

        var resolved = FFmpegResolver.Resolve(
            "ffmpeg",
            tree.Path("src", "bin"),
            _ => null,
            windows: true,
            pathEnv: null,
            maxParentHops: 4);

        Assert.Equal(bundled, resolved);
    }

    [Fact]
    public void Non_windows_still_searches_PATH_when_nothing_is_bundled()
    {
        using var tree = new Tree();
        var onPath = tree.File("pathdir", "ffmpeg");
        var start = tree.Dir("app");

        var resolved = FFmpegResolver.Resolve(
            "ffmpeg",
            start,
            _ => null,
            windows: false,
            pathEnv: tree.Path("pathdir"),
            maxParentHops: 0,
            pathSeparator: ';');

        Assert.Equal(onPath, resolved);
    }

    private static Func<string, string?> Env(params (string Key, string Value)[] pairs)
    {
        var map = pairs.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        return key => map.TryGetValue(key, out var value) ? value : null;
    }

    private sealed class Tree : IDisposable
    {
        private readonly string _root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "temno-ffmpeg-" + Guid.NewGuid().ToString("N"));

        public Tree() => Directory.CreateDirectory(_root);

        public string Path(params string[] parts) => Combine(parts);

        public string Dir(params string[] parts)
        {
            var path = Combine(parts);
            Directory.CreateDirectory(path);
            return path;
        }

        public string File(params string[] parts)
        {
            var path = Combine(parts);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            System.IO.File.WriteAllBytes(path, [0x4D]);
            return path;
        }

        public void Dispose()
        {
            try { Directory.Delete(_root, recursive: true); }
            catch (IOException) { }
        }

        private string Combine(string[] parts)
        {
            var path = _root;
            foreach (var part in parts)
                path = System.IO.Path.Combine(path, part);
            return path;
        }
    }
}
