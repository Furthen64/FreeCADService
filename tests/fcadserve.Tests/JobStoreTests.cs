using FcadServe.Jobs;
using FcadServe.Options;
using Microsoft.Extensions.Logging.Abstractions;

namespace FcadServe.Tests;

public class JobStoreTests
{
    private sealed class Scope(string root) : IDisposable
    {
        public string Root => root;
        public void Dispose() { try { Directory.Delete(root, true); } catch { /* best effort */ } }
    }

    private static async Task<(JobStore Store, JobDatabase Db, Scope Scope)> OpenStoreAsync(int maxRetained = 5)
    {
        var root = Path.Combine(Path.GetTempPath(), "fcadserve-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var options = new ServiceOptions
        {
            StateRoot = root,
            Retention = { MaxRetainedJobs = maxRetained },
        };
        var db = new JobDatabase(options, NullLogger<JobDatabase>.Instance);
        await db.InitializeAsync();
        var store = new JobStore(db, options, NullLogger<JobStore>.Instance);
        return (store, db, new Scope(root));
    }

    [Fact]
    public async Task Create_then_get_is_queued()
    {
        var (store, _, scope) = await OpenStoreAsync();
        using var _ = scope;

        var job = await store.CreateAsync(store.CreateJobId(), "/tmp/a.stl", null, "/tmp", 10, "abc");
        var got = await store.GetAsync(job.Id);
        Assert.NotNull(got);
        Assert.Equal(JobStatus.Queued, got!.Status);
        Assert.Equal(JobPhase.Queued, got.Phase);
        Assert.Null(got.StartedAtUtc);
    }

    [Fact]
    public async Task MarkRunning_sets_status_phase_and_start_time()
    {
        var (store, _, scope) = await OpenStoreAsync();
        using var _ = scope;

        var job = await store.CreateAsync(store.CreateJobId(), "/tmp/a.stl", null, "/tmp", 10, "abc");
        await store.MarkRunningAsync(job);
        var got = await store.GetAsync(job.Id);
        Assert.Equal(JobStatus.Running, got!.Status);
        Assert.Equal(JobPhase.StartingFreeCad, got.Phase);
        Assert.NotNull(got.StartedAtUtc);
    }

    [Fact]
    public async Task MarkTerminal_persists_full_outcome()
    {
        var (store, _, scope) = await OpenStoreAsync();
        using var _ = scope;

        var job = await store.CreateAsync(store.CreateJobId(), "/tmp/a.stl", null, "/tmp", 10, "abc");
        await store.MarkTerminalAsync(job, JobStatus.Skipped, JobPhase.Finished, 0, null, null, "/tmp/a.report.json",
            [new ArtifactFile { Name = "a.stp", Path = "/tmp/a.stp", Kind = "step", SizeBytes = 5 }]);

        var got = await store.GetAsync(job.Id);
        Assert.True(got!.IsTerminal);
        Assert.Equal(JobStatus.Skipped, got.Status);
        Assert.Equal(JobPhase.Finished, got.Phase);
        Assert.Equal("/tmp/a.report.json", got.ReportPath);
        Assert.Single(got.Artifacts());
        Assert.NotNull(got.FinishedAtUtc);
    }

    [Fact]
    public async Task RequestCancel_queued_job_becomes_cancelled_terminal()
    {
        var (store, _, scope) = await OpenStoreAsync();
        using var _ = scope;

        var job = await store.CreateAsync(store.CreateJobId(), "/tmp/a.stl", null, "/tmp", 10, "abc");
        await store.RequestCancelAsync(job.Id);
        var got = await store.GetAsync(job.Id);
        Assert.Equal(JobStatus.Cancelled, got!.Status);
        Assert.True(got.IsTerminal);
        Assert.False(got.CancelRequested);
    }

    [Fact]
    public async Task RequestCancel_running_job_sets_flag_not_terminal()
    {
        var (store, _, scope) = await OpenStoreAsync();
        using var _ = scope;

        var job = await store.CreateAsync(store.CreateJobId(), "/tmp/a.stl", null, "/tmp", 10, "abc");
        await store.MarkRunningAsync(job);
        await store.RequestCancelAsync(job.Id);
        var got = await store.GetAsync(job.Id);
        Assert.Equal(JobStatus.Running, got!.Status);
        Assert.True(got.CancelRequested);
        Assert.False(got.IsTerminal);
    }

    [Fact]
    public async Task RecoverOrphans_requeues_queued_and_running_jobs()
    {
        var (store, _, scope) = await OpenStoreAsync();
        using var _ = scope;

        var queued = await store.CreateAsync(store.CreateJobId(), "/tmp/q.stl", null, "/tmp", 10, "abc");
        var running = await store.CreateAsync(store.CreateJobId(), "/tmp/r.stl", null, "/tmp", 10, "abc");
        await store.MarkRunningAsync(running);
        await store.RequestCancelAsync(running.Id); // running + cancel-requested mid-flight

        var recovered = await store.RecoverOrphansAsync();
        Assert.Equal(2, recovered.Count);

        var gotQ = await store.GetAsync(queued.Id);
        var gotR = await store.GetAsync(running.Id);
        Assert.Equal(JobStatus.Queued, gotQ!.Status);
        Assert.Equal(JobStatus.Queued, gotR!.Status);
        Assert.False(gotR!.CancelRequested);
        Assert.Equal("recovered_after_restart", gotR.ErrorCode);

        var terminal = await store.GetByStatusAsync([JobStatus.Succeeded, JobStatus.Failed, JobStatus.Cancelled]);
        Assert.Empty(terminal);
    }

    [Fact]
    public async Task Prune_removes_terminated_job_work_dirs()
    {
        var (store, _, scope) = await OpenStoreAsync(maxRetained: 0);
        using var _ = scope;

        var job = await store.CreateAsync(store.CreateJobId(), "/tmp/a.stl", null, "/tmp", 10, "abc");
        var workDir = store.JobWorkDir(job.Id);
        Directory.CreateDirectory(workDir);
        await store.MarkTerminalAsync(job, JobStatus.Failed, JobPhase.Finished, 3, "err", "boom", null, []);

        var removed = await store.PruneAsync();
        Assert.Equal(1, removed);
        Assert.False(Directory.Exists(workDir));
        Assert.Null(await store.GetAsync(job.Id));
    }
}

internal static class TestJobRecordExtensions
{
    public static List<ArtifactFile> Artifacts(this JobRecord job) => ArtifactFile.Deserialize(job.ArtifactJson);
}