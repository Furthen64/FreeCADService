using System.Text.Json;
using FcadServe.Options;

namespace FcadServe.Jobs;

/// <summary>
/// Higher-level job operations on top of <see cref="JobDatabase"/>, including
/// live phase merging from the worker's progress file. Terminal-state
/// transitions are centralized here so the API and worker agree on semantics.
/// </summary>
public sealed class JobStore(JobDatabase db, ServiceOptions options, ILogger<JobStore> logger)
{
    public string CreateJobId() => "j_" + Convert.ToHexStringLower(Guid.NewGuid().ToByteArray());

    public string JobWorkDir(string id) => Path.Combine(options.StateRoot, "jobs", id);

    public async Task<JobRecord> CreateAsync(
        string id,
        string stlPath,
        string? requestedOutputDir,
        string effectiveOutputDir,
        long inputSize,
        string sha256,
        CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        var job = new JobRecord
        {
            Id = id,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            StlPath = stlPath,
            RequestedOutputDir = requestedOutputDir,
            EffectiveOutputDir = effectiveOutputDir,
            InputSizeBytes = inputSize,
            InputSha256 = sha256,
        };
        await db.InsertAsync(job, ct);
        return job;
    }

    public async Task<JobRecord?> GetAsync(string id, CancellationToken ct = default)
    {
        var job = await db.GetAsync(id, ct);
        if (job is null)
            return null;
        MergeLivePhase(job);
        return job;
    }

    public async Task<List<JobRecord>> GetByStatusAsync(IEnumerable<JobStatus> statuses, CancellationToken ct = default)
    {
        var jobs = await db.GetByStatusAsync(statuses, ct);
        foreach (var job in jobs)
            MergeLivePhase(job);
        return jobs;
    }

    /// <summary>Updates a job row, bumping the modification timestamp.</summary>
    public async Task SaveAsync(JobRecord job, CancellationToken ct = default)
    {
        job.UpdatedAtUtc = DateTimeOffset.UtcNow;
        await db.UpdateAsync(job, ct);
    }

    public async Task MarkRunningAsync(JobRecord job, CancellationToken ct = default)
    {
        job.Status = JobStatus.Running;
        job.Phase = JobPhase.StartingFreeCad;
        job.StartedAtUtc = DateTimeOffset.UtcNow;
        await SaveAsync(job, ct);
        logger.LogInformation("Job {JobId} started", job.Id);
    }

    public async Task MarkTerminalAsync(
        JobRecord job,
        JobStatus status,
        JobPhase phase,
        int? exitCode,
        string? errorCode,
        string? error,
        string? reportPath,
        List<ArtifactFile> artifacts,
        CancellationToken ct = default)
    {
        job.Status = status;
        job.Phase = phase;
        job.ExitCode = exitCode;
        job.ErrorCode = errorCode;
        job.Error = error;
        job.ReportPath = reportPath;
        job.ArtifactJson = ArtifactFile.Serialize(artifacts);
        job.FinishedAtUtc = DateTimeOffset.UtcNow;
        await SaveAsync(job, ct);
    }

    public async Task RequestCancelAsync(string id, CancellationToken ct = default)
    {
        var job = await db.GetAsync(id, ct);
        if (job is null)
            return;
        if (job.IsTerminal)
            return;
        if (job.Status == JobStatus.Queued)
        {
            job.Status = JobStatus.Cancelled;
            job.Phase = JobPhase.Finished;
            job.FinishedAtUtc = DateTimeOffset.UtcNow;
            await SaveAsync(job, ct);
            logger.LogInformation("Queued job {JobId} cancelled before starting", job.Id);
        }
        else
        {
            job.CancelRequested = true;
            await SaveAsync(job, ct);
        }
    }

    /// <summary>
    /// Recovery for process restarts: queued jobs become runnable again; jobs
    /// that were running are returned to the queue (their working directory is
    /// regenerated) unless disabled.
    /// </summary>
    public async Task<List<JobRecord>> RecoverOrphansAsync(CancellationToken ct = default)
    {
        var stale = await db.GetByStatusAsync([JobStatus.Queued, JobStatus.Running], ct);
        foreach (var job in stale)
        {
            job.Status = JobStatus.Queued;
            job.Phase = JobPhase.Queued;
            job.CancelRequested = false;
            job.ErrorCode = "recovered_after_restart";
            job.Error = "job was interrupted by a service restart and has been re-queued.";
            await SaveAsync(job, ct);
            logger.LogWarning("Re-queued job {JobId} ({PreviousStatus}) after restart", job.Id, job.Status);
        }
        return stale;
    }

    public async Task<int> PruneAsync(CancellationToken ct = default)
    {
        var removed = await db.PruneTerminalAsync(options.Retention.MaxRetainedJobs, ct);
        foreach (var job in removed)
        {
            var dir = JobWorkDir(job.Id);
            if (Directory.Exists(dir))
            {
                try
                {
                    Directory.Delete(dir, recursive: true);
                }
                catch (IOException ex)
                {
                    logger.LogWarning(ex, "Unable to remove job directory {Dir}", dir);
                }
            }
        }
        if (removed.Count > 0)
            logger.LogInformation("Pruned {Count} terminated jobs beyond retention limit {Max}",
                removed.Count, options.Retention.MaxRetainedJobs);
        return removed.Count;
    }

    /// <summary>
    /// While a job runs it may not be possible to flush status to SQLite at
    /// exactly the worker's phase; the worker also writes a small progress file
    /// that is merged into reads for accurate phase reporting.
    /// </summary>
    private void MergeLivePhase(JobRecord job)
    {
        if (job.Status != JobStatus.Running)
            return;
        var progress = Path.Combine(JobWorkDir(job.Id), "progress.json");
        if (!File.Exists(progress))
            return;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(progress));
            var root = doc.RootElement;
            if (root.TryGetProperty("phase", out var phaseEl)
                && phaseEl.ValueKind == JsonValueKind.String
                && Enum.TryParse<JobPhase>(phaseEl.GetString(), ignoreCase: true, out var phase))
            {
                job.Phase = phase;
            }
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Failed to read progress file {Path}", progress);
        }
    }
}