using System.Threading.Channels;
using FcadServe.FreeCad;
using FcadServe.Options;
using FcadServe.Security;

namespace FcadServe.Jobs;

public sealed class JobQueueFullException : Exception
{
    public JobQueueFullException() : base("job queue is full.") { }
}

/// <summary>
/// Owns the job queue and handles submission, cancellation, restart recovery
/// and retention pruning. Never executes FreeCAD geometry itself.
/// </summary>
public sealed class JobDirector(
    Channel<JobRecord> queue,
    JobStore store,
    JobDatabase db,
    PathPolicy policy,
    ActiveProcessRegistry registry,
    ILogger<JobDirector> logger) : BackgroundService
{
    private readonly PeriodicTimer _pruneTimer = new(TimeSpan.FromMinutes(5));

    /// <summary>Validates and queues a new job. Returns the created record.</summary>
    public async Task<JobRecord> SubmitAsync(string? stlPath, string? outputDir, CancellationToken ct = default)
    {
        var (input, output) = policy.Resolve(stlPath, outputDir);

        var id = store.CreateJobId();
        var requested = string.IsNullOrWhiteSpace(outputDir) ? null : outputDir;
        var job = await store.CreateAsync(id, input.StlPath, requested, output.Directory, input.SizeBytes, input.Sha256, ct);

        if (!queue.Writer.TryWrite(job))
        {
            await db.DeleteAsync(id, ct);
            throw new JobQueueFullException();
        }
        return job;
    }

    /// <summary>Cancels a queued or running job. Returns false if the job id is unknown.</summary>
    public async Task<bool> CancelAsync(string id, CancellationToken ct = default)
    {
        var job = await store.GetAsync(id, ct);
        if (job is null)
            return false;
        if (job.IsTerminal)
            return true;

        // Signal first so the worker sees the flag even if the group kill races.
        await store.RequestCancelAsync(id, ct);
        if (registry.TryGetPid(id, out var pid))
        {
            await ProcessGroupKiller.KillGroupAsync(pid, ct);
            logger.LogInformation("Job {JobId}: killed worker process group {Pid}", id, pid);
        }
        return true;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (await _pruneTimer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await store.PruneAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Prune pass failed");
            }
        }
    }

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        // Database init + restart recovery must finish before queue workers
        // drain the channel (hosted services start in registration order, and
        // the worker is registered after this service).
        await db.InitializeAsync(cancellationToken);
        await base.StartAsync(cancellationToken);

        var recovered = await store.RecoverOrphansAsync(cancellationToken);
        foreach (var job in recovered)
        {
            if (!queue.Writer.TryWrite(job))
            {
                logger.LogWarning("Job {JobId}: recovered job could not be re-queued (queue full)", job.Id);
            }
        }
        await store.PruneAsync(cancellationToken);
    }
}

/// <summary>Worker pool: drains the job queue with up to WorkerCount concurrent executors.</summary>
public sealed class JobQueueWorker(
    Channel<JobRecord> queue,
    JobExecutor executor,
    ServiceOptions options,
    ILogger<JobQueueWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var count = Math.Max(1, options.Jobs.WorkerCount);
        var tasks = new Task[count];
        for (var i = 0; i < count; i++)
        {
            var worker = i;
            tasks[i] = Task.Run(() => WorkerLoopAsync(worker, stoppingToken), CancellationToken.None);
        }
        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    private async Task WorkerLoopAsync(int worker, CancellationToken ct)
    {
        await foreach (var job in queue.Reader.ReadAllAsync(ct).ConfigureAwait(false))
        {
            logger.LogInformation("Worker {Worker} picked up job {JobId}", worker, job.Id);
            try
            {
                await executor.ExecuteAsync(job, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Worker {Worker} failed while executing job {JobId}", worker, job.Id);
            }
        }
    }
}