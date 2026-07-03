using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfetch.Plugin.Download.WebMedia;

/// <summary>Captured result of a completed tool run: exit code plus separated streams.</summary>
internal sealed class ProcessResult
{
    public ProcessResult(int exitCode, string stdOut, string stdErr)
    {
        ExitCode = exitCode;
        StdOut = stdOut;
        StdErr = stdErr;
    }

    public int ExitCode { get; }

    public string StdOut { get; }

    public string StdErr { get; }
}

/// <summary>
/// Subprocess orchestration for yt-dlp / svtplay-dl with the hygiene the backend
/// needs: line-by-line async reads (stdout and stderr kept separate — never merged),
/// UTF-8 forced so åäö round-trips, cancellation that kills the whole process tree,
/// and an optional stall timeout (tools go quiet on non-TTY; a dead download must not
/// hang a concurrency slot forever).
/// </summary>
internal sealed class ProcessRunner
{
    /// <summary>Run to completion, buffering both streams. Used for introspection.</summary>
    public async Task<ProcessResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();

        var exit = await StreamAsync(
            fileName,
            arguments,
            line => stdout.AppendLine(line),
            line => stderr.AppendLine(line),
            stallTimeout: null,
            cancellationToken).ConfigureAwait(false);

        return new ProcessResult(exit, stdout.ToString(), stderr.ToString());
    }

    /// <summary>Run a tool, invoking a callback per output line. Returns the exit code.</summary>
    public async Task<int> StreamAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        Action<string> onStdout,
        Action<string> onStderr,
        TimeSpan? stallTimeout,
        CancellationToken cancellationToken)
    {
        using var process = new Process();
        process.StartInfo.FileName = fileName;
        foreach (var arg in arguments)
        {
            process.StartInfo.ArgumentList.Add(arg);
        }

        process.StartInfo.RedirectStandardOutput = true;
        process.StartInfo.RedirectStandardError = true;
        process.StartInfo.UseShellExecute = false;
        process.StartInfo.CreateNoWindow = true;
        process.StartInfo.StandardOutputEncoding = Encoding.UTF8;
        process.StartInfo.StandardErrorEncoding = Encoding.UTF8;

        if (!process.Start())
        {
            throw new InvalidOperationException("Failed to start process: " + fileName);
        }

        var lastActivity = DateTime.UtcNow;
        void Bump() => lastActivity = DateTime.UtcNow;

        var stdoutTask = PumpAsync(process.StandardOutput, l => { Bump(); onStdout(l); }, cancellationToken);
        var stderrTask = PumpAsync(process.StandardError, l => { Bump(); onStderr(l); }, cancellationToken);
        var waitTask = process.WaitForExitAsync(cancellationToken);

        using var stallCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task? stallTask = null;
        if (stallTimeout is TimeSpan window)
        {
            stallTask = Task.Run(
                async () =>
                {
                    while (!stallCts.IsCancellationRequested)
                    {
                        await Task.Delay(TimeSpan.FromSeconds(1), stallCts.Token).ConfigureAwait(false);
                        if (DateTime.UtcNow - lastActivity > window)
                        {
                            throw new TimeoutException(
                                $"No output for {window.TotalSeconds:F0}s; treating the download as stalled.");
                        }
                    }
                },
                stallCts.Token);
        }

        try
        {
            if (stallTask != null)
            {
                var completed = await Task.WhenAny(waitTask, stallTask).ConfigureAwait(false);
                await completed.ConfigureAwait(false); // propagate a stall exception
            }
            else
            {
                await waitTask.ConfigureAwait(false);
            }

            await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
            return process.ExitCode;
        }
        catch (Exception ex) when (ex is OperationCanceledException or TimeoutException)
        {
            KillTree(process);
            throw;
        }
        finally
        {
            stallCts.Cancel();
        }
    }

    private static async Task PumpAsync(StreamReader reader, Action<string> onLine, CancellationToken ct)
    {
        string? line;
        while ((line = await reader.ReadLineAsync(ct).ConfigureAwait(false)) != null)
        {
            onLine(line);
        }
    }

    private static void KillTree(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            // Already exited.
        }
        catch (NotSupportedException)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill();
                }
            }
            catch (InvalidOperationException)
            {
                // Best effort.
            }
        }
    }
}
