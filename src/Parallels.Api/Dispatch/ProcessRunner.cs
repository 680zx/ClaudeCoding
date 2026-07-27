using System.Diagnostics;
using System.Text;

namespace Parallels.Api.Dispatch;

/// <summary>Result of running an external process to completion.</summary>
public sealed record ProcessResult(int ExitCode, string Stdout, string Stderr);

/// <summary>
/// Runs an external process, feeding it stdin and capturing both streams.
///
/// Job JSON goes in over stdin rather than as a command-line argument. Both are
/// permitted (spec 3.5), but an argument would have to survive shell quoting on
/// the way into <c>docker exec</c>, and JSON containing quotes is exactly the
/// thing that breaks there. stdin has no such failure mode.
/// </summary>
public static class ProcessRunner
{
    public static async Task<ProcessResult> RunAsync(
        string fileName,
        IEnumerable<string> arguments,
        string? stdin,
        TimeSpan timeout,
        CancellationToken ct = default)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardInput = stdin is not null,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);

        using var process = new Process { StartInfo = startInfo };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();

        process.OutputDataReceived += (_, e) => { if (e.Data is not null) stdout.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderr.AppendLine(e.Data); };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        if (stdin is not null)
        {
            await process.StandardInput.WriteAsync(stdin);
            process.StandardInput.Close();
        }

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutSource.CancelAfter(timeout);

        try
        {
            await process.WaitForExitAsync(timeoutSource.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* already gone */ }
            throw new TimeoutException(
                $"'{fileName}' did not finish within {timeout.TotalMinutes:F0} minutes and was killed.");
        }

        return new ProcessResult(process.ExitCode, stdout.ToString(), stderr.ToString());
    }
}
