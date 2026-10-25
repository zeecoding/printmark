using System.Diagnostics;

namespace PrintMark.Core;

/// <summary>Runs an external process with a hard timeout (a hung Ghostscript can no longer stall a job forever).</summary>
public static class ProcessRunner
{
    public static async Task<(int ExitCode, string Output)> RunAsync(
        string exe, string arguments, int timeoutMs, bool highPriority = false)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            Arguments = arguments,
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true
        };

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start {Path.GetFileName(exe)}.");

        if (highPriority)
        {
            try { proc.PriorityClass = ProcessPriorityClass.High; } catch { }
        }

        var errTask = proc.StandardError.ReadToEndAsync();
        var outTask = proc.StandardOutput.ReadToEndAsync();

        using var cts = new CancellationTokenSource(timeoutMs);
        try
        {
            await proc.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            try { proc.Kill(entireProcessTree: true); } catch { }
            throw new TimeoutException($"{Path.GetFileName(exe)} did not finish within {timeoutMs} ms.");
        }

        return (proc.ExitCode, (await errTask) + (await outTask));
    }
}
