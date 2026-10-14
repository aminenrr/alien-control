using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace AlienControl.Services;

/// <summary>Runs one of T-Troll's CLI tools from the tools/ folder and returns its stdout.</summary>
public sealed class CliRunner
{
    private readonly string _exePath;
    // alienfan-cli probes ACPI on every start; never run two at once.
    private readonly SemaphoreSlim _gate = new(1, 1);

    public CliRunner(string exeName)
    {
        _exePath = Path.Combine(AppContext.BaseDirectory, "tools", exeName);
        if (!File.Exists(_exePath))
            _exePath = Path.Combine(AppContext.BaseDirectory, exeName);
    }

    public bool IsAvailable => File.Exists(_exePath);
    public string ExePath => _exePath;

    public async Task<CliResult> RunAsync(string arguments, int timeoutMs = 8000)
    {
        if (!IsAvailable)
            return new CliResult(false, "", $"{Path.GetFileName(_exePath)} not found next to AlienControl.exe (tools/).");

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var psi = new ProcessStartInfo(_exePath, arguments)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                WorkingDirectory = Path.GetDirectoryName(_exePath)!
            };

            using var p = new Process { StartInfo = psi };
            p.Start();
            var stdout = p.StandardOutput.ReadToEndAsync();
            var stderr = p.StandardError.ReadToEndAsync();

            using var cts = new CancellationTokenSource(timeoutMs);
            try
            {
                await p.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try { p.Kill(true); } catch { /* already gone */ }
                return new CliResult(false, "", $"{Path.GetFileName(_exePath)} {arguments} timed out.");
            }

            return new CliResult(p.ExitCode == 0, await stdout.ConfigureAwait(false), await stderr.ConfigureAwait(false));
        }
        catch (Exception ex)
        {
            return new CliResult(false, "", ex.Message);
        }
        finally
        {
            _gate.Release();
        }
    }
}

public sealed record CliResult(bool Ok, string Output, string Error);
