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

    public CliRunner(string exeName)
    {
        _exePath = Path.Combine(AppContext.BaseDirectory, "tools", exeName);
        if (!File.Exists(_exePath))
            _exePath = Path.Combine(AppContext.BaseDirectory, exeName);
    }

    public bool IsAvailable => File.Exists(_exePath);

    // 1.3.0-beta.1 (review #6): only T-Troll's published alienfx-tools 9.6.x CLIs run (SHA-256 of the exes inside
    // each release's alienfx-tools.zip). The app is elevated, so a swapped exe in a user-writable folder would
    // otherwise run as admin. The file is held open (no writes / replace) from the hash check until the CLI exits.
    private static readonly System.Collections.Generic.HashSet<string> Trusted = new(StringComparer.OrdinalIgnoreCase)
    {
        // alienfan-cli.exe
        "10fded5ac72621edd16a751b0c9e471ee0d4bcee31deff321811947c725c285d", // 9.6.2.1
        "101723bb978ae0dc626ba3fba7436bf6186264b89efd2240ee487582f82f479c", // 9.6.2
        "6eee88320415339d16d093c0e81a7fd555cffd0f2e1a741053cd36170ccbd19c", // 9.6.1.1 / 9.6.1.2
        "6f0891249ef9670640b659d828a1bc1bb75ffde9ab847be720ad3d517b0cf2b4", // 9.6.1
        // alienfx-cli.exe
        "c01acecadca1996935268a047f04dd4a0794aff6a1d8e1a026354d022af4745e", // 9.6.2 / 9.6.2.1
        "9fb332a480163159f45301946d43bd4685551ecd1365cfd382890035486481d1", // 9.6.1.2
        "9a29cd6c98997bd88b9f8fac820ba31d80ce09244d50fa0ce17cb33900f7152f", // 9.6.1.1
        "fe456aceff8893f20bfd05d1430819649a185c64e63d51f101b4a260aa8b8c3c", // 9.6.1
    };

    public static string Sha256(Stream s)
    {
        s.Position = 0;
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(s));
    }

    /// <summary>Opens the exe (read, deny write) and checks its hash. Null + error when it isn't a trusted build.</summary>
    private FileStream? OpenTrusted(out string? error)
    {
        error = null;
        try
        {
            var fs = new FileStream(_exePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            var h = Sha256(fs);
            if (Trusted.Contains(h)) return fs;
            fs.Dispose();
            Log.Info("CliRunner", $"refused {_exePath}: SHA-256 {h} is not a known alienfx-tools 9.6 build");
            error = $"{Path.GetFileName(_exePath)} isn't a known alienfx-tools 9.6 build, so it wasn't run (hash in crash.log).";
        }
        catch (Exception ex) { Log.Warn("CliRunner.OpenTrusted", ex); error = $"{Path.GetFileName(_exePath)} couldn't be checked."; }
        return null;
    }
    public string ExePath => _exePath;

    /// <summary>alienfan-cli probes ACPI on every start: runs through the shared HardwareController queue.</summary>
    public Task<CliResult> RunAsync(string arguments, int timeoutMs = 8000) =>
        IsAvailable ? HardwareController.RunAsync(() => RunCoreAsync(arguments, timeoutMs))
                    : Task.FromResult(new CliResult(false, "", $"{Path.GetFileName(_exePath)} not found next to AlienControl.exe (tools/)."));

    private async Task<CliResult> RunCoreAsync(string arguments, int timeoutMs)
    {
        if (!IsAvailable)
            return new CliResult(false, "", $"{Path.GetFileName(_exePath)} not found next to AlienControl.exe (tools/).");

        using var pin = OpenTrusted(out var trustError);
        if (pin is null) return new CliResult(false, "", trustError ?? "untrusted tool");
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
                try { p.Kill(true); } catch (System.Exception __e) { AlienControl.Services.Log.Warn("CliRunner.cs:101", __e); /* already gone */ }
                return new CliResult(false, "", $"{Path.GetFileName(_exePath)} {arguments} timed out.");
            }

            return new CliResult(p.ExitCode == 0, await stdout.ConfigureAwait(false), await stderr.ConfigureAwait(false));
        }
        catch (Exception ex)
        {
            return new CliResult(false, "", ex.Message);
        }
    }
}

public sealed record CliResult(bool Ok, string Output, string Error);
