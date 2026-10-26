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

/// <summary>2.0.2 review: the exit cleanup keeps a test's OcPending marker unless +0 is confirmed.</summary>
public class OcExitPendingTests
{
    [Theory]
    [InlineData(true, true, false, true)]    // +0 written OK
    [InlineData(false, true, false, false)]  // reset skipped (RTX off / switching) or failed: driver unknown, keep it
    [InlineData(false, true, true, false)]
    [InlineData(false, false, false, true)]  // driver known at +0, nothing in flight
    [InlineData(false, false, true, false)]  // +0 only assumed, driver check still owed: keep it
    public void Pending_is_cleared_only_when_zero_is_confirmed(bool resetOk, bool mayBeOn, bool unverified, bool clears) =>
        Assert.Equal(clears, GpuOc.ExitClearsPending(resetOk, mayBeOn, unverified));
}

/// <summary>2.0.2 review: a pending OC test recovery waits for the RTX and only clears on a confirmed +0.</summary>
public class OcStartupRecoveryTests
{
    [Fact]
    public void Rtx_off_at_startup_keeps_the_marker_and_leaves_the_state_unknown()
    {
        Assert.False(GpuOc.StartupRecovered(rtxOff: true, err: null));
        // _ocApplied stays null, so a later sync can't count it as recovered without a write or read
        Assert.False(GpuOc.PendingRecoveryDone(pending: true, testing: false, applied: null));
    }

    [Fact]
    public void Failed_startup_write_keeps_the_marker() =>
        Assert.False(GpuOc.StartupRecovered(rtxOff: false, err: "NVML busy"));

    [Fact]
    public void Successful_startup_write_clears_the_marker() =>
        Assert.True(GpuOc.StartupRecovered(rtxOff: false, err: null));

    [Fact]
    public void Pending_to_recovered_once_the_rtx_is_back()
    {
        (int, int)? applied = null;                                        // startup: RTX off, deferred
        Assert.False(GpuOc.PendingRecoveryDone(true, false, applied));
        applied = null;                                                    // RTX back, +0 write failed
        Assert.False(GpuOc.PendingRecoveryDone(true, false, applied));
        applied = (0, 0);                                                  // +0 written or read back
        Assert.True(GpuOc.PendingRecoveryDone(true, false, applied));
    }

    [Theory]
    [InlineData(false, false)]   // nothing pending
    [InlineData(true, true)]     // a running test owns the marker
    public void Never_clears_outside_a_deferred_recovery(bool pending, bool testing) =>
        Assert.False(GpuOc.PendingRecoveryDone(pending, testing, (0, 0)));

    [Fact]
    public void Non_zero_driver_state_is_not_recovered() =>
        Assert.False(GpuOc.PendingRecoveryDone(true, false, (100, 200)));
}
