using System.Text.Json;
using FcadServe.FreeCad;
using FcadServe.Options;
using FcadServe.Security;

namespace FcadServe.Jobs;

public sealed class JobExecutor(
    JobStore store,
    JobDatabase db,
    IFreeCadRunner runner,
    ActiveProcessRegistry registry,
    PathPolicy policy,
    ServiceOptions serviceOptions,
    ILogger<JobExecutor> logger)
{
    private sealed record WorkerParams(
        string JobId,
        string JobDir,
        string ArtifactsDir,
        string StlPath,
        string Stem,
        string InputSha256,
        string CreatedAtUtc,
        TargetNames TargetNames,
        RenderOptions Render,
        string ProgressPath,
        string StatusPath);

    private sealed record TargetNames(
        string Step,
        string IsoPng,
        string SectionPng,
        string LeftPng,
        string TopPng,
        string RightPng,
        string BottomPng,
        string Report);

    private sealed record RenderOptions(int Width, int Height);

    private sealed record WorkerResult(
        string Status,
        string? Reason,
        string? ErrorCode,
        string? Error,
        string? ReportPath);

    /// <summary>Runs one job to a terminal state (or leaves it Running on a
    /// graceful service stop so it can be recovered on restart).</summary>
    public async Task ExecuteAsync(JobRecord job, CancellationToken shutdownCt)
    {
        var id = job.Id;
        var jobDir = store.JobWorkDir(id);
        var overallTimeout = TimeSpan.FromSeconds(Math.Max(1, serviceOptions.Jobs.TimeoutSeconds));

        try
        {
            // A queued job may have been cancelled while waiting in the channel.
            var current = await store.GetAsync(id, shutdownCt);
            if (current is null || current.IsTerminal)
            {
                logger.LogInformation("Job {JobId}: skipped, already {Status}", id, current?.Status.ToString() ?? "removed");
                return;
            }
            job = current;

            await PrepareWorkDirAsync(jobDir, shutdownCt);
            await WriteJobMetadataAsync(jobDir, job, shutdownCt);

            var paramsFile = WriteParams(job, jobDir, policy);
            await store.MarkRunningAsync(job, shutdownCt);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(shutdownCt);
            var poll = PollCancellationAsync(id, cts);

            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                // ---- Phase 1: headless pipeline (FreeCADCmd) --------------------
                var pipeTimeout = TimeSpan.FromSeconds(Math.Max(1, serviceOptions.Jobs.TimeoutSeconds * 2 / 3));
                var pipeRun = await runner.RunAsync(job, jobDir, paramsFile,
                    FreeCadRunKind.Pipeline, pipeTimeout, cts.Token,
                    pid => registry.Register(id, pid));
                registry.Unregister(id);

                if (pipeRun.Kind == RunOutcomeKind.CancelledOrStopped && shutdownCt.IsCancellationRequested)
                {
                    logger.LogInformation("Job {JobId}: left running for recovery during shutdown", id);
                    return;
                }

                if (pipeRun.Kind is RunOutcomeKind.TimedOut or RunOutcomeKind.FailedToStart or RunOutcomeKind.CancelledOrStopped)
                {
                    await FinishAsync(job, BuildOutcomeFromRun(pipeRun, jobDir), jobDir, shutdownCt);
                    return;
                }

                var fresh = await store.GetAsync(id, shutdownCt) ?? job;
                if (fresh.CancelRequested)
                {
                    await FinishAsync(job, new JobOutcome(JobStatus.Cancelled, pipeRun.ExitCode, "cancelled",
                        "job was cancelled.", null, CollectWorkArtifacts(jobDir)), jobDir, shutdownCt);
                    return;
                }

                var worker = ReadWorkerResult(jobDir);
                if (worker is null)
                {
                    await FinishAsync(job, new JobOutcome(JobStatus.Failed, pipeRun.ExitCode, "missing_status",
                        "FreeCAD worker finished without a readable status.json.", null, CollectWorkArtifacts(jobDir)), jobDir, shutdownCt);
                    return;
                }
                if (worker.Status == "failed")
                {
                    await FinishAsync(job, new JobOutcome(JobStatus.Failed, pipeRun.ExitCode,
                        worker.ErrorCode ?? "worker_failed", worker.Error ?? worker.Reason,
                        worker.ReportPath, CollectWorkArtifacts(jobDir)), jobDir, shutdownCt);
                    return;
                }

                // ---- Phase 2: GUI render (Xvfb) --------------------------------
                var renderBudget = overallTimeout - sw.Elapsed;
                if (renderBudget <= TimeSpan.FromSeconds(5))
                {
                    await FinishAsync(job, new JobOutcome(JobStatus.Failed, null, "render_timeout",
                        "insufficient time budget for render phase.", worker.ReportPath, CollectWorkArtifacts(jobDir)), jobDir, shutdownCt);
                    return;
                }

                var renderRun = await runner.RunAsync(job, jobDir, paramsFile,
                    FreeCadRunKind.Render, renderBudget, cts.Token,
                    pid => registry.Register(id, pid));
                registry.Unregister(id);

                if (renderRun.Kind == RunOutcomeKind.CancelledOrStopped && shutdownCt.IsCancellationRequested)
                {
                    logger.LogInformation("Job {JobId}: left running for recovery during render shutdown", id);
                    return;
                }

                fresh = await store.GetAsync(id, shutdownCt) ?? job;
                if (fresh.CancelRequested)
                {
                    await FinishAsync(job, new JobOutcome(JobStatus.Cancelled, renderRun.ExitCode, "cancelled",
                        "job was cancelled during render.", worker.ReportPath, CollectWorkArtifacts(jobDir)), jobDir, shutdownCt);
                    return;
                }

                if (renderRun.Kind is RunOutcomeKind.TimedOut or RunOutcomeKind.FailedToStart or RunOutcomeKind.CancelledOrStopped)
                {
                    await FinishAsync(job, BuildOutcomeFromRun(renderRun, jobDir), jobDir, shutdownCt);
                    return;
                }

                var renderResult = ReadRenderResult(jobDir);
                if (renderResult is null)
                {
                    await FinishAsync(job, new JobOutcome(JobStatus.Failed, renderRun.ExitCode, "missing_render_result",
                        "render phase produced no render.result.json.", worker.ReportPath, CollectWorkArtifacts(jobDir)), jobDir, shutdownCt);
                    return;
                }
                if (!renderResult.Ok)
                {
                    await FinishAsync(job, new JobOutcome(JobStatus.Failed, renderRun.ExitCode, "render_failed",
                        renderResult.Error ?? "render phase failed.", worker.ReportPath, CollectWorkArtifacts(jobDir)), jobDir, shutdownCt);
                    return;
                }

                // ---- Both phases succeeded — publish final artifacts ------------
                var outcome = await PublishArtifactsAsync(job, jobDir, worker, renderRun.ExitCode, shutdownCt);
                await FinishAsync(job, outcome, jobDir, shutdownCt);
            }
            catch (OperationCanceledException) when (shutdownCt.IsCancellationRequested)
            {
                logger.LogInformation("Job {JobId}: aborted during shutdown", id);
            }
            finally
            {
                registry.Unregister(id);
                cts.Cancel();
                try { await poll; } catch { /* poll cancelled */ }
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Job {JobId}: unhandled executor error; marking failed", id);
            await FinishAsync(job, new JobOutcome(JobStatus.Failed, null, "internal_error", ex.Message, null, []), jobDir, shutdownCt);
        }
    }

    private static JobOutcome BuildOutcomeFromRun(JobRunResult run, string jobDir)
    {
        return run.Kind switch
        {
            RunOutcomeKind.TimedOut => new JobOutcome(JobStatus.Failed, run.ExitCode, "job_timeout", run.Error, null, CollectWorkArtifacts(jobDir)),
            RunOutcomeKind.FailedToStart => new JobOutcome(JobStatus.Failed, run.ExitCode, run.ErrorCode, run.Error, null, CollectWorkArtifacts(jobDir)),
            RunOutcomeKind.CancelledOrStopped => new JobOutcome(JobStatus.Cancelled, run.ExitCode, "cancelled", run.Error, null, CollectWorkArtifacts(jobDir)),
            _ => new JobOutcome(JobStatus.Failed, run.ExitCode, "internal_error", "unexpected run outcome.", null, CollectWorkArtifacts(jobDir)),
        };
    }

    private async Task PrepareWorkDirAsync(string jobDir, CancellationToken ct)
    {
        if (Directory.Exists(jobDir))
            Directory.Delete(jobDir, recursive: true);
        Directory.CreateDirectory(jobDir);
        Directory.CreateDirectory(Path.Combine(jobDir, "artifacts"));
        await Task.CompletedTask;
    }

    private string WriteParams(JobRecord job, string jobDir, PathPolicy policy)
    {
        var (_, output) = policy.Plan(job.StlPath, job.RequestedOutputDir ?? "");
        var artifactsDir = Path.Combine(jobDir, "artifacts");
        var stem = output.StepPath[..^Path.GetExtension(output.StepPath).Length];

        var finalNames = new TargetNames(
            Step: Path.GetFileName(output.StepPath),
            IsoPng: Path.GetFileName(output.IsoPngPath),
            SectionPng: Path.GetFileName(output.SectionPngPath),
            LeftPng: Path.GetFileName(output.LeftPngPath),
            TopPng: Path.GetFileName(output.TopPngPath),
            RightPng: Path.GetFileName(output.RightPngPath),
            BottomPng: Path.GetFileName(output.BottomPngPath),
            Report: Path.GetFileName(output.ReportPath));

        var p = new WorkerParams(
            JobId: job.Id,
            JobDir: jobDir,
            ArtifactsDir: artifactsDir,
            StlPath: job.StlPath,
            Stem: Path.GetFileNameWithoutExtension(job.StlPath),
            InputSha256: job.InputSha256 ?? "",
            CreatedAtUtc: job.CreatedAtUtc.ToString("O"),
            TargetNames: finalNames,
            Render: new RenderOptions(1280, 960),
            ProgressPath: Path.Combine(jobDir, "progress.json"),
            StatusPath: Path.Combine(jobDir, "status.json"));

        var path = Path.Combine(jobDir, "params.json");
        File.WriteAllText(path, JsonSerializer.Serialize(p, new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        }));
        return path;
    }

    private async Task WriteJobMetadataAsync(string jobDir, JobRecord job, CancellationToken ct)
    {
        var path = Path.Combine(jobDir, "job.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new
        {
            id = job.Id,
            stl_path = job.StlPath,
            requested_output_dir = job.RequestedOutputDir,
            effective_output_dir = job.EffectiveOutputDir,
            created_at_utc = job.CreatedAtUtc,
            input_size_bytes = job.InputSizeBytes,
            input_sha256 = job.InputSha256,
            service_version = VersionInfo.Version,
        }, JsonHelper.Default), ct);
    }

    private async Task PollCancellationAsync(string jobId, CancellationTokenSource cts)
    {
        try
        {
            while (!cts.IsCancellationRequested)
            {
                await Task.Delay(250, cts.Token);
                var current = await db.GetAsync(jobId, CancellationToken.None);
                if (current?.CancelRequested == true)
                {
                    cts.Cancel();
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // token fired
        }
    }

    private sealed record JobOutcome(
        JobStatus Status,
        int? ExitCode,
        string? ErrorCode,
        string? Error,
        string? ReportPath,
        List<ArtifactFile> Artifacts);

    /// <summary>Publishes worker artifacts into the final output directory and
    /// validates that required artifacts are present before reporting success.</summary>
    private async Task<JobOutcome> PublishArtifactsAsync(
        JobRecord job, string jobDir, WorkerResult worker, int? exitCode, CancellationToken ct)
    {
        var (_, output) = policy.Plan(job.StlPath, job.RequestedOutputDir ?? "");
        var artifactsDir = Path.Combine(jobDir, "artifacts");
        Directory.CreateDirectory(output.Directory);

        var step = Path.Combine(artifactsDir, Path.GetFileName(output.StepPath));
        var report = Path.Combine(artifactsDir, Path.GetFileName(output.ReportPath));

        var failures = new List<string>();
        var published = new List<ArtifactFile>();

        var requiredRenderPaths = new[]
        {
            output.IsoPngPath,
            output.LeftPngPath,
            output.TopPngPath,
            output.RightPngPath,
            output.BottomPngPath,
        };
        var requiredArtifacts = new[]
        {
            (step, output.StepPath),
            (report, output.ReportPath),
        }.Concat(requiredRenderPaths.Select(path =>
            (Path.Combine(artifactsDir, Path.GetFileName(path)), path)));
        var optionalSection = (Path.Combine(artifactsDir, Path.GetFileName(output.SectionPngPath)), output.SectionPngPath);

        foreach (var (source, destination) in requiredArtifacts)
        {
            Publish(source, destination, out var artifact, failures);
            if (artifact is not null)
                published.Add(artifact);
        }
        if (File.Exists(optionalSection.Item1))
        {
            Publish(optionalSection.Item1, optionalSection.Item2, out var artifact, failures);
            if (artifact is not null)
                published.Add(artifact);
        }

        if (failures.Count > 0)
        {
            logger.LogError("Job {JobId}: required artifacts missing or failed to publish: {Failures}", job.Id, string.Join(", ", failures));
            return new JobOutcome(JobStatus.Failed, exitCode, "missing_artifacts",
                $"required artifacts were not produced: {string.Join(", ", failures)}", worker.ReportPath, CollectWorkArtifacts(jobDir));
        }

        var artifacts = new List<ArtifactFile>(published);
        artifacts.AddRange(CollectWorkArtifacts(jobDir));

        var status = worker.Status == "skipped" ? JobStatus.Skipped : JobStatus.Succeeded;
        if (status == JobStatus.Succeeded)
            logger.LogInformation("Job {JobId}: success, artifacts published to {Dir}", job.Id, output.Directory);
        else
            logger.LogInformation("Job {JobId}: reconstruction intentionally skipped ({Reason}); fallback artifacts published", job.Id, worker.Reason);

        // Normalize log/status entry names in the work dir to stable names.
        foreach (var a in artifacts)
            a.Kind = NormalizeKind(a);

        return new JobOutcome(status, exitCode, null, null, worker.ReportPath, artifacts);
    }

    private static string NormalizeKind(ArtifactFile a)
    {
        var name = a.Name;
        if (name.EndsWith(".stp", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".step", StringComparison.OrdinalIgnoreCase)) return "step";
        if (name.EndsWith("_iso.png", StringComparison.OrdinalIgnoreCase)) return "iso_png";
        if (name.EndsWith("_section.png", StringComparison.OrdinalIgnoreCase)) return "section_png";
        if (name.EndsWith("_left.png", StringComparison.OrdinalIgnoreCase)) return "left_png";
        if (name.EndsWith("_top.png", StringComparison.OrdinalIgnoreCase)) return "top_png";
        if (name.EndsWith("_right.png", StringComparison.OrdinalIgnoreCase)) return "right_png";
        if (name.EndsWith("_bottom.png", StringComparison.OrdinalIgnoreCase)) return "bottom_png";
        if (name.EndsWith(".report.json", StringComparison.OrdinalIgnoreCase)) return "report";
        if (name == "status.json") return "status";
        if (name == "freecad_console.log") return "console_log";
        if (name == "job.json") return "job_metadata";
        if (name == "progress.json") return "service_metadata";
        return "artifact";
    }

    private static void Publish(string src, string dest, out ArtifactFile? artifact, List<string>? failures)
    {
        artifact = null;
        if (!File.Exists(src))
        {
            failures?.Add(dest);
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        var temp = dest + $".publishing-{Guid.NewGuid():N}.tmp";
        try
        {
            File.Copy(src, temp, overwrite: true);
            File.Move(temp, dest, overwrite: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            try { File.Delete(temp); } catch { /* best effort */ }
            failures?.Add(dest);
            return;
        }

        artifact = new ArtifactFile
        {
            Name = Path.GetFileName(dest),
            Path = Path.GetFullPath(dest),
            SizeBytes = new FileInfo(dest).Length,
        };
    }

    /// <summary>Work-dir sidecar files (logs, status, metadata) listed in the manifest.</summary>
    private static List<ArtifactFile> CollectWorkArtifacts(string jobDir)
    {
        var files = new List<ArtifactFile>();
        foreach (var name in new[] { "job.json", "status.json", "freecad_console.log" })
        {
            var path = Path.Combine(jobDir, name);
            if (File.Exists(path))
            {
                files.Add(new ArtifactFile
                {
                    Name = name,
                    Kind = NormalizeKind(new ArtifactFile { Name = name, Path = path }),
                    Path = path,
                    SizeBytes = new FileInfo(path).Length,
                });
            }
        }
        return files;
    }

    private static WorkerResult? ReadWorkerResult(string jobDir)
    {
        var path = Path.Combine(jobDir, "status.json");
        if (!File.Exists(path))
            return null;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            string? Get(string key) =>
                root.TryGetProperty(key, out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null;
            return new WorkerResult(
                Get("status") ?? "failed",
                Get("reason"),
                Get("error_code"),
                Get("error"),
                Get("report_path"));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private sealed record RenderResult(bool Ok, string? Error);

    private static RenderResult? ReadRenderResult(string jobDir)
    {
        var path = Path.Combine(jobDir, "render.result.json");
        if (!File.Exists(path))
            return null;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            var ok = root.TryGetProperty("ok", out var okProp) && okProp.ValueKind == JsonValueKind.True;
            string? error = root.TryGetProperty("error", out var errProp) && errProp.ValueKind == JsonValueKind.String
                ? errProp.GetString() : null;
            return new RenderResult(ok, error);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private async Task FinishAsync(JobRecord job, JobOutcome outcome, string jobDir, CancellationToken ct)
    {
        await WriteJobMetadataAsync(jobDir, MergeFinalState(job, outcome), ct);
        await store.MarkTerminalAsync(job, outcome.Status, JobPhase.Finished, outcome.ExitCode,
            outcome.ErrorCode, outcome.Error, outcome.ReportPath, outcome.Artifacts, ct);
        logger.LogInformation("Job {JobId}: final status {Status} (exit={ExitCode}, error={ErrorCode})",
            job.Id, outcome.Status, outcome.ExitCode, outcome.ErrorCode);
    }

    private JobRecord MergeFinalState(JobRecord job, JobOutcome outcome)
    {
        job.Status = outcome.Status;
        job.Phase = JobPhase.Finished;
        job.ExitCode = outcome.ExitCode;
        job.ErrorCode = outcome.ErrorCode;
        job.Error = outcome.Error;
        job.ReportPath = outcome.ReportPath;
        job.ArtifactJson = ArtifactFile.Serialize(outcome.Artifacts);
        job.FinishedAtUtc = DateTimeOffset.UtcNow;
        return job;
    }
}

public static class VersionInfo
{
    public static readonly string Version =
        typeof(VersionInfo).Assembly.GetName().Version?.ToString(3) ?? "dev";
}
