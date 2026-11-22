using System.Reflection;
using Temno.Core.IO;
using Xunit;

namespace Temno.Core.Tests;

public class ReleaseSourcePathTests
{
    [Fact]
    public void Release_stack_trace_has_no_developer_path()
    {
        var configuration = typeof(EnvCompat).Assembly
            .GetCustomAttribute<AssemblyConfigurationAttribute>()
            ?.Configuration;
        if (!string.Equals(configuration, "Release", StringComparison.OrdinalIgnoreCase))
            return;

        string text;
        try
        {
            EnvCompat.Get(null!);
            text = "";
        }
        catch (Exception ex)
        {
            text = ex.ToString();
        }

        var dest = Environment.GetEnvironmentVariable("TEMNO_WP264_STACK_OUT");
        if (!string.IsNullOrWhiteSpace(dest))
        {
            var record = string.Join(Environment.NewLine, new[]
            {
                "=====================================================",
                $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz}  ReleaseSourcePath",
                $"thread {Environment.CurrentManagedThreadId}",
                text,
                "",
            });
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.WriteAllText(dest, record);
        }

        Assert.Contains("/_/", text, StringComparison.Ordinal);
        Assert.DoesNotContain("palmier-pro-windows", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(@"C:\Users\", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(@"D:\", text, StringComparison.OrdinalIgnoreCase);
    }
}
