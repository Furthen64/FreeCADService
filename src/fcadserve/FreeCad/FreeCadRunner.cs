using System.Diagnostics;
using FcadServe.Jobs;
using FcadServe.Options;

namespace FcadServe.FreeCad;

public enum RunOutcomeKind
{
    Completed,
    TimedOut,
    CancelledOrStopped,
    FailedToStart,
}

public sealed record JobRunResult(RunOutcomeKind Kind, int? ExitCode, string? ErrorCode, string? Error);

/// <summary>Which FreeCAD binary a run stage uses.</summary>
public enum FreeCadRunKind
{
    /// <summary>Headless pipeline (FreeCADCmd): load, reconstruct, export, validate, report.</summary>
    Pipeline,

    /// <summary>GUI renderer under Xvfb: six standard view PNGs and report patch.</summary>
    Render,
}

/// <summary>
/// Runs one FreeCAD job stage in an isolated subprocess with, for the GUI
/// render stage, a dedicated virtual display. The process is spawned with
/// argument arrays, placed in its own process group (via setsid), and
/// hard-killed by group so nothing leaks even if FreeCAD or the flatpak sandbox
/// misbehaves. Xvfb is started per render job and always torn down.
/// </summary>
public interface IFreeCadRunner
{
    Task<JobRunResult> RunAsync(
        JobRecord job,
        string jobDir,
        string paramsFile,
        FreeCadRunKind kind,
        TimeSpan timeout,
        CancellationToken cancelToken,
        Action<int>? onProcessStarted = null);
}

public sealed class FreeCadRunner(
    ServiceOptions options,
    ILogger<FreeCadRunner> logger) : IFreeCadRunner
{
    private static readonly TimeSpan XvfbStartupTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan KillGrace = TimeSpan.FromMilliseconds(750);

    public async Task<JobRunResult> RunAsync(
        JobRecord job,
        string jobDir,
        string paramsFile,
        FreeCadRunKind kind,
        TimeSpan timeout,
        CancellationToken cancelToken,
        Action<int>? onProcessStarted = null)
    {
        var consolePath = Path.Combine(jobDir, "freecad_console.log");
        var disposables = new List<IDisposable>();

        var needsDisplay = kind == FreeCadRunKind.Render;
        var script = kind == FreeCadRunKind.Pipeline
            ? options.WorkerScript
            : options.RenderScript;

        string? xvfbDisplay = null;
        int xvfbPid = 0;
        if (needsDisplay)
        {
            var xvfb = await StartXvfbAsync(consolePath, cancelToken);
            if (xvfb is null)
            {
                logger.LogError("Job {JobId}: failed to start Xvfb", job.Id);
                return new JobRunResult(RunOutcomeKind.FailedToStart, null, "xvfb_failed", "Xvfb could not be started.");
            }
            (xvfbDisplay, xvfbPid) = xvfb.Value;
        }

        Process process;
        try
        {
            var psi = FreeCadCommandBuilder.Build(options, script, paramsFile, xvfbDisplay, headless: !needsDisplay);
            FreeCadCommandBuilder.ApplyUserConfigIsolation(psi, options, jobDir);
            foreach (var dir in new[]
                     {
                         psi.Environment.TryGetValue("XDG_CONFIG_HOME", out var c) ? c : null,
                         psi.Environment.TryGetValue("XDG_CACHE_HOME", out var ca) ? ca : null,
                         psi.Environment.TryGetValue("XDG_DATA_HOME", out var d) ? d : null,
                     })
            {
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir!);
            }

            process = new Process { StartInfo = psi };
            var console = new LineFileLogger(consolePath);
            disposables.Add(console);
            process.OutputDataReceived += (_, e) => console.Write(e.Data);
            process.ErrorDataReceived += (_, e) => console.Write(e.Data);

            if (!process.Start())
            {
                if (needsDisplay) await KillProcessGroupAsync(xvfbPid, "xvfb");
                return new JobRunResult(RunOutcomeKind.FailedToStart, null, "freecad_start_failed", "FreeCAD process could not be started.");
            }
        }
        catch (Exception ex)
        {
            if (needsDisplay) await KillProcessGroupAsync(xvfbPid, "xvfb");
            logger.LogError(ex, "Job {JobId}: failed to launch FreeCAD", job.Id);
            return new JobRunResult(RunOutcomeKind.FailedToStart, null, "freecad_start_failed", ex.Message);
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        // process.Id is the setsid pid, which is also the FreeCAD job's
        // process-group id.
        onProcessStarted?.Invoke(process.Id);
        logger.LogInformation("Job {JobId}: FreeCAD {Kind} started pid={Pid} display={Display}", job.Id, kind, process.Id, xvfbDisplay);

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancelToken);
        linked.CancelAfter(timeout);

        RunOutcomeKind outcome;
        int? exitCode = null;
        try
        {
            await process.WaitForExitAsync(linked.Token);
            exitCode = process.ExitCode;
            outcome = cancelToken.IsCancellationRequested ? RunOutcomeKind.CancelledOrStopped : RunOutcomeKind.Completed;
        }
        catch (OperationCanceledException)
        {
            outcome = cancelToken.IsCancellationRequested
                ? RunOutcomeKind.CancelledOrStopped
                : RunOutcomeKind.TimedOut;
        }

        await KillProcessGroupAsync(process.Id, job.Id);
        if (needsDisplay) await KillProcessGroupAsync(xvfbPid, "xvfb");

        foreach (var d in disposables)
            d.Dispose();

        if (outcome == RunOutcomeKind.TimedOut)
        {
            logger.LogError("Job {JobId}: timed out after {Seconds}s and was killed", job.Id, timeout.TotalSeconds);
            return new JobRunResult(outcome, exitCode, "job_timeout", $"job exceeded the {timeout.TotalSeconds}s timeout and was terminated.");
        }
        if (outcome == RunOutcomeKind.CancelledOrStopped)
        {
            logger.LogInformation("Job {JobId}: cancelled or service stopping; worker terminated", job.Id);
            return new JobRunResult(outcome, exitCode, "cancelled", "job was cancelled.");
        }

        logger.LogInformation("Job {JobId}: FreeCAD {Kind} exited with code {ExitCode}", job.Id, kind, exitCode);
        return new JobRunResult(outcome, exitCode, null, null);
    }

    /// <summary>Starts a dedicated Xvfb and returns its display and pid.</summary>
    private async Task<(string Display, int Pid)?> StartXvfbAsync(string logPath, CancellationToken cancelToken)
    {
        Process? xvfb = null;
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = options.Xvfb.Executable,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            psi.ArgumentList.Add("-screen");
            psi.ArgumentList.Add("0");
            psi.ArgumentList.Add(options.Xvfb.Screen);
            psi.ArgumentList.Add("-nolisten");
            psi.ArgumentList.Add("tcp");
            psi.ArgumentList.Add("-displayfd");
            psi.ArgumentList.Add("1");

            xvfb = new Process { StartInfo = psi };
            var errLogger = new LineFileLogger(logPath);
            xvfb.ErrorDataReceived += (_, e) => errLogger.Write(e.Data);
            if (!xvfb.Start())
                return null;
            xvfb.BeginErrorReadLine();

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancelToken);
            timeout.CancelAfter(XvfbStartupTimeout);
            var display = await xvfb.StandardOutput.ReadLineAsync(timeout.Token);
            if (string.IsNullOrWhiteSpace(display))
            {
                await KillProcessGroupAsync(xvfb.Id, "xvfb");
                return null;
            }

            // Xvfb's -displayfd prints the bare display number (e.g. "2");
            // the forward-colon form is required for DISPLAY.
            _ = xvfb.StandardOutput.BaseStream.CopyToAsync(Stream.Null);
            errLogger.Dispose();
            return (":" + display.Trim(), xvfb.Id);
        }
        catch
        {
            if (xvfb is not null)
                await KillProcessGroupAsync(xvfb.Id, "xvfb");
            return null;
        }
    }

    internal static async Task KillProcessGroupAsync(int pid, string label)
    {
        if (pid <= 0)
            return;
        await SignalAsync("-TERM", pid);
        await Task.Delay(KillGrace);
        await SignalAsync("-KILL", pid);
        try
        {
            var alive = Process.GetProcessById(pid);
            alive.Kill(entireProcessTree: true);
        }
        catch
        {
            // process already gone
        }
    }

    private static async Task SignalAsync(string signal, int pid)
    {
        try
        {
            using var kill = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "kill",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    ArgumentList = { signal, $"-{pid}" },
                },
            };
            kill.Start();
            await kill.WaitForExitAsync();
        }
        catch
        {
            // best effort
        }
    }
}

/// <summary>Thread-safe append-only line logger used for FreeCAD console output.</summary>
internal sealed class LineFileLogger(string path) : IDisposable
{
    private readonly object _lock = new();

    public void Write(string? line)
    {
        if (line is null)
            return;
        lock (_lock)
        {
            try
            {
                File.AppendAllText(path, line + Environment.NewLine);
            }
            catch (IOException)
            {
                // ignore transient IO issues while logging
            }
        }
    }

    public void Dispose()
    {
        // nothing to close; File.AppendAllText is self-contained
    }
}

/// <summary>Registry of job-id → worker process group pid for cancellation.</summary>
public sealed class ActiveProcessRegistry
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> _byJob = new();

    public void Register(string jobId, int pid) => _byJob[jobId] = pid;
    public void Unregister(string jobId) => _byJob.TryRemove(jobId, out _);
    public bool TryGetPid(string jobId, out int pid) => _byJob.TryGetValue(jobId, out pid);
}

public static class ProcessGroupKiller
{
    public static async Task KillGroupAsync(int pid, CancellationToken ct = default)
    {
        if (pid <= 0)
            return;
        try
        {
            using var kill = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "kill",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    ArgumentList = { "-KILL", $"-{pid}" },
                },
            };
            kill.Start();
            await kill.WaitForExitAsync(ct);
        }
        catch
        {
            // best effort
        }
    }
}
