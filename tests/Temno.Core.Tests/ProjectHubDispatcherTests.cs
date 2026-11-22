using System.IO;
using System.Text.Json.Nodes;
using Temno.Core.Session;
using Temno.Mcp;
using Xunit;

namespace Temno.Core.Tests;

[Collection("EnvRegistry")]
public sealed class ProjectHubDispatcherTests
{
    [Fact]
    public async Task RunSerialized_UsesConfiguredDispatcherForBackgroundCaller()
    {
        var dispatcher = new RecordingDispatcher();
        ProjectHub.ConfigureDispatcher(dispatcher);
        try
        {
            var callerThread = Environment.CurrentManagedThreadId;
            var executionThread = await Task.Run(() =>
                ProjectHub.RunSerialized(() => Environment.CurrentManagedThreadId));

            Assert.Equal(1, dispatcher.InvocationCount);
            Assert.NotEqual(callerThread, executionThread);
        }
        finally
        {
            ProjectHub.ConfigureDispatcher(null);
        }
    }

    [Fact]
    public async Task RunSerialized_PreventsConcurrentFallbackExecution()
    {
        ProjectHub.ConfigureDispatcher(null);
        var active = 0;
        var maximum = 0;
        var tasks = Enumerable.Range(0, 12).Select(_ => Task.Run(() =>
            ProjectHub.RunSerialized(() =>
            {
                var current = Interlocked.Increment(ref active);
                maximum = Math.Max(maximum, current);
                Thread.Sleep(10);
                Interlocked.Decrement(ref active);
            })));

        await Task.WhenAll(tasks);

        Assert.Equal(1, maximum);
    }

    [Fact]
    public async Task ImportMediaFile_CopyAndProbeHappenOutsideDispatcher()
    {
        var dispatcher = new RecordingDispatcher();
        ProjectHub.ConfigureDispatcher(dispatcher);
        var package = Path.Combine(Path.GetTempPath(), "temno-disp-" + Guid.NewGuid().ToString("N") + ".palmier");
        var source = Path.Combine(Path.GetTempPath(), "temno-disp-src-" + Guid.NewGuid().ToString("N") + ".png");
        try
        {
            File.WriteAllBytes(source, Convert.FromBase64String(
                "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII="));
            var session = ProjectSession.CreateNew();
            session.Save(package);

            var result = await Task.Run(() => ToolExecutor.ImportMediaFile(session, source));

            Assert.False(result.IsError);
            Assert.Single(session.Manifest.Entries);
            Assert.Equal(2, dispatcher.InvocationCount);
        }
        finally
        {
            ProjectHub.ConfigureDispatcher(null);
            if (Directory.Exists(package)) Directory.Delete(package, recursive: true);
            if (File.Exists(source)) File.Delete(source);
        }
    }

    [Fact]
    public async Task SearchMedia_IndexesOutsideDispatcher()
    {
        var dispatcher = new RecordingDispatcher();
        ProjectHub.ConfigureDispatcher(dispatcher);
        var package = Path.Combine(Path.GetTempPath(), "temno-search-" + Guid.NewGuid().ToString("N") + ".palmier");
        var previous = ProjectHub.ActiveSession;
        try
        {
            var session = ProjectSession.CreateNew();
            session.Save(package);
            ProjectHub.ActiveSession = session;

            var result = await Task.Run(() => ToolExecutor.Execute(
                "search_media",
                new JsonObject
                {
                    ["query"] = "blue",
                    ["scope"] = "visual",
                    ["limit"] = 5,
                }));

            Assert.False(result.IsError);
            Assert.Equal(1, dispatcher.InvocationCount);
        }
        finally
        {
            ProjectHub.ActiveSession = previous;
            ProjectHub.ConfigureDispatcher(null);
            if (Directory.Exists(package)) Directory.Delete(package, recursive: true);
        }
    }

    private sealed class RecordingDispatcher : IProjectSessionDispatcher
    {
        public bool HasThreadAccess => false;
        public int InvocationCount { get; private set; }

        public T Invoke<T>(Func<T> operation)
        {
            InvocationCount++;
            T? result = default;
            Exception? error = null;
            var thread = new Thread(() =>
            {
                try { result = operation(); }
                catch (Exception ex) { error = ex; }
            });
            thread.Start();
            thread.Join();
            if (error is not null) throw error;
            return result!;
        }
    }
}
