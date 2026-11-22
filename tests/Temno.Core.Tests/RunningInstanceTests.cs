using Temno.Environments;
using Xunit;

namespace Temno.Core.Tests;

/// <summary>WP-402 D-1: a second launch of another build names the open copy instead of switching to it.</summary>
public sealed class RunningInstanceTests
{
    private const string Installed = @"C:\Program Files\WindowsApps\io.temno.windows_0.1.0.4_x64__abc\Temno.App.exe";
    private const string Fork = @"D:\src\temno\src\Temno.App\bin\x64\Debug\net8.0-windows10.0.26100.0\win-x64\Temno.App.exe";
    private const string OtherFork = @"E:\other\Temno.App.exe";

    [Fact]
    public void The_record_round_trips_and_a_bad_one_reads_as_none()
    {
        var record = new RunningInstance(4242, true, Installed, "0.1.0.4");
        Assert.Equal(record, RunningInstance.Parse(record.Serialize()));
        Assert.Contains("\"official\":true", record.Serialize());

        Assert.Null(RunningInstance.Parse(null));
        Assert.Null(RunningInstance.Parse(""));
        Assert.Null(RunningInstance.Parse("{"));
        Assert.Null(RunningInstance.Parse("[]"));
        Assert.Null(RunningInstance.Parse("{\"pid\":0,\"official\":true,\"path\":\"x\"}"));
        Assert.Null(RunningInstance.Parse("{\"pid\":12,\"official\":true}"));
    }

    [Fact]
    public void The_same_build_launched_twice_brings_its_window_forward()
    {
        var open = new RunningInstance(10, false, Fork, "0.1.0.0");
        Assert.Null(RunningInstance.SecondLaunchMessage("Temno", false, Fork, 10, Fork, open));
        Assert.Null(RunningInstance.SecondLaunchMessage("Temno", false, Fork.ToUpperInvariant(), 10, Fork, open));
        Assert.Null(RunningInstance.SecondLaunchMessage("Temno", true, Installed, 10, Installed, null));
    }

    [Fact]
    public void A_custom_build_started_beside_the_official_one_names_it_and_how_to_switch()
    {
        var open = new RunningInstance(10, true, Installed, "0.1.0.4");
        var message = RunningInstance.SecondLaunchMessage("Temno", false, Fork, 10, Installed, open);
        Assert.NotNull(message);
        Assert.StartsWith("Temno is already open. Close it to start the copy you built from source.", message);
        Assert.Contains("one copy of Temno runs at a time", message);
        Assert.Contains(Installed, message);
    }

    [Fact]
    public void The_official_build_started_beside_a_custom_one_names_it()
    {
        var open = new RunningInstance(10, false, Fork, "0.1.0.0");
        var message = RunningInstance.SecondLaunchMessage("Temno", true, Installed, 10, Fork, open);
        Assert.StartsWith("A copy of Temno built from source is already open. Close it to start Temno.", message);
        Assert.Contains(Fork, message);
    }

    [Fact]
    public void Two_different_custom_builds_and_two_official_copies_say_another_copy()
    {
        var custom = new RunningInstance(10, false, Fork, "0.1.0.0");
        Assert.StartsWith(
            "Another copy of Temno-Dev built from source is already open.",
            RunningInstance.SecondLaunchMessage("Temno-Dev", false, OtherFork, 10, Fork, custom));
        var official = new RunningInstance(10, true, Installed, "0.1.0.4");
        Assert.StartsWith(
            "Another copy of Temno is already open.",
            RunningInstance.SecondLaunchMessage("Temno", true, OtherFork, 10, Installed, official));
    }

    [Fact]
    public void A_record_from_an_earlier_run_is_not_believed()
    {
        // The record names another process: the open copy is an older build that writes none.
        var stale = new RunningInstance(99, false, Fork, "0.1.0.0");
        Assert.StartsWith(
            "Another copy of Temno is already open.",
            RunningInstance.SecondLaunchMessage("Temno", false, Fork, 10, Installed, stale));
        // Same process number but another executable: also stale.
        var moved = new RunningInstance(10, false, Fork, "0.1.0.0");
        Assert.StartsWith(
            "Another copy of Temno is already open.",
            RunningInstance.SecondLaunchMessage("Temno", false, Fork, 10, Installed, moved));
    }

    [Fact]
    public void Without_a_path_or_a_record_the_launch_keeps_the_old_hand_off()
        => Assert.Null(RunningInstance.SecondLaunchMessage("Temno", false, Fork, 10, null, null));

    [Fact]
    public void A_record_names_the_open_copy_without_its_path_only_when_its_executable_is_there()
    {
        var folder = Path.Combine(Path.GetTempPath(), "wp402-instance-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var installed = Path.Combine(folder, "Temno.App.exe");
            File.WriteAllText(installed, "");
            var open = new RunningInstance(10, true, installed, "0.1.0.4");
            Assert.StartsWith(
                "Temno is already open. Close it to start the copy you built from source.",
                RunningInstance.SecondLaunchMessage("Temno", false, Fork, 10, null, open));

            // An earlier run's record whose process number now belongs to an unreadable process: its executable is
            // gone, so it is not believed and the old hand-off runs.
            var gone = new RunningInstance(10, true, Path.Combine(folder, "old", "Temno.App.exe"), "0.1.0.3");
            Assert.Null(RunningInstance.SecondLaunchMessage("Temno", false, Fork, 10, null, gone));
            // Nor is one naming another program.
            var other = Path.Combine(folder, "notepad.exe");
            File.WriteAllText(other, "");
            Assert.Null(RunningInstance.SecondLaunchMessage("Temno", false, Fork, 10, null, new RunningInstance(10, true, other, "")));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void A_reused_process_number_that_is_not_temno_keeps_the_old_hand_off()
        => Assert.Null(RunningInstance.SecondLaunchMessage("Temno", false, Fork, 10, @"C:\Windows\System32\notepad.exe", null));
}
