using System;
using System.Threading.Tasks;
using AlienControl.Services;
using Xunit;

namespace AlienControl.Tests;

/// <summary>2.0.2 stability audit: focused regression checks for the ACPI queue hold (4a) and the G-Mode read (3b).</summary>
public class StabilityTests
{
    [Fact]
    public async Task Queue_stays_closed_until_a_held_cli_exits()
    {
        var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        // first operation: a CLI that timed out and is still running hands its exit task to the queue
        var first = await HardwareController.RunAsync(() => { HardwareController.HoldUntil(exited.Task); return Task.FromResult(1); });
        Assert.Equal(1, first);
        Assert.True(HardwareController.Stuck);

        bool secondRan = false;
        var second = HardwareController.RunAsync(() => { secondRan = true; return Task.FromResult(2); });
        await Task.Delay(200);
        Assert.False(secondRan);   // no second ACPI operation while the first process is alive
        Assert.False(second.IsCompleted);

        exited.SetResult();        // the stuck process finally exits
        Assert.Equal(2, await second.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.True(secondRan);
        Assert.False(HardwareController.Stuck);
    }

    [Fact]
    public async Task Hold_that_already_finished_releases_at_once()
    {
        await HardwareController.RunAsync(() => { HardwareController.HoldUntil(Task.CompletedTask); return Task.FromResult(0); });
        Assert.False(HardwareController.Stuck);
        Assert.Equal(3, await HardwareController.RunAsync(() => Task.FromResult(3)).WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Theory]
    [InlineData("", null)]
    [InlineData("alienfan-cli.exe timed out.", null)]
    [InlineData("G-mode is On", true)]
    [InlineData("G-mode is Off", false)]
    public void Failed_gmode_read_is_unknown_not_off(string output, bool? expected) =>
        Assert.Equal(expected, AlienFan.ParseGMode(output));
}
