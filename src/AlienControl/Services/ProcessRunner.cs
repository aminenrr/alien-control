using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace AlienControl.Services;

/// <summary>
/// 2.0.0-beta1.3 (services review #7): one runner for the Windows tools (pnputil, net, powercfg). Every run has a
/// timeout, and stdout / stderr are read at the same time (reading one to the end before the other can deadlock once
/// a pipe fills). On a timeout the run is reported as timed out. kill = false (pnputil) never kills the process: it
/// is left to finish its device operation, and Pending completes once it has exited (its output goes to the log).
/// </summary>
public static class ProcessRunner
{
    public sealed record Result(bool Started, bool TimedOut, int ExitCode, string Output)
    {
        /// <summary>Completes when the process has really exited (already done unless it timed out without a kill).</summary>
        public Task Pending { get; init; } = Task.CompletedTask;
    }

    private static Process Start(string exe, string args)
    {
        var p = new Process
        {
            StartInfo = new ProcessStartInfo(exe, args)
            { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true }
        };
        p.Start();
        return p;
    }

    public static async Task<Result> RunAsync(string exe, string args, int timeoutMs, bool kill)
    {
        Process p;
        try { p = Start(exe, args); }
        catch (Exception ex) { return new Result(false, false, -1, ex.Message); }
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        using (var cts = new CancellationTokenSource(timeoutMs))
        {
            try { await p.WaitForExitAsync(cts.Token).ConfigureAwait(false); }
            catch (OperationCanceledException)
            {
                string name = System.IO.Path.GetFileName(exe);
                if (kill)
                {
                    try { p.Kill(true); } catch (Exception e) { Log.Warn("ProcessRunner.Kill", e); /* already gone */ }
                    Observe(stdout, stderr);
                    p.Dispose();
                    return new Result(true, true, -1, $"{name} timed out.");
                }
                Log.Info("ProcessRunner", $"{name} {args} still running after {timeoutMs / 1000} s, left to finish");
                var pending = Task.Run(async () =>
                {
                    try
                    {
                        await p.WaitForExitAsync().ConfigureAwait(false);
                        string late = ((await stdout.ConfigureAwait(false)) + (await stderr.ConfigureAwait(false))).Trim();
                        Log.Info("ProcessRunner", $"{name} {args} finished late, exit {p.ExitCode}: {late}");
                    }
                    catch (Exception e) { Log.Warn("ProcessRunner.Late", e); }
                    finally { p.Dispose(); }
                });
                return new Result(true, true, -1, $"{name} is taking too long.") { Pending = pending };
            }
        }
        try
        {
            string o = ((await stdout.ConfigureAwait(false)) + (await stderr.ConfigureAwait(false))).Trim();
            return new Result(true, false, p.ExitCode, o);
        }
        finally { p.Dispose(); }
    }

    /// <summary>The output reads of a killed process may fault once it's disposed: observe them (no "Task" crash.log line).</summary>
    private static void Observe(params Task[] reads)
    {
        foreach (var t in reads) _ = t.ContinueWith(r => _ = r.Exception, TaskContinuationOptions.OnlyOnFaulted);
    }

    /// <summary>Blocking version for the exit paths (crash handler, Windows ending the session). Always kills on timeout:
    /// only used for powercfg.</summary>
    public static Result Run(string exe, string args, int timeoutMs)
    {
        Process p;
        try { p = Start(exe, args); }
        catch (Exception ex) { return new Result(false, false, -1, ex.Message); }
        using (p)
        {
            var stdout = p.StandardOutput.ReadToEndAsync();
            var stderr = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(timeoutMs))
            {
                try { p.Kill(true); } catch (Exception e) { Log.Warn("ProcessRunner.Kill", e); /* already gone */ }
                Observe(stdout, stderr);
                return new Result(true, true, -1, $"{System.IO.Path.GetFileName(exe)} timed out.");
            }
            p.WaitForExit();   // flushes the async output reads
            string o = "";
            try { if (stdout.Wait(1000) && stderr.Wait(1000)) o = (stdout.Result + stderr.Result).Trim(); } catch (Exception e) { Log.Warn("ProcessRunner.Output", e); }
            return new Result(true, false, p.ExitCode, o);
        }
    }
}
