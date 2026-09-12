using System.Text.Json;
using System.Text.Json.Nodes;
using FcadServe.FreeCad;
using FcadServe.Jobs;
using FcadServe.Options;
using FcadServe.Security;
using Microsoft.Extensions.Logging.Abstractions;

namespace FcadServe.Tests;

public class JobExecutorTests
{
    private sealed class Scope(string root) : IDisposable
    {
        public string Root => root;
        public void Dispose() { try { Directory.Delete(root, true); } catch { /* best effort */ } }
    }

    private sealed class FakeRunner : IFreeCadRunner
    {
        public Func<JsonObject, string, JobRunResult>? Pipeline { get; set; }
        public Func<JsonObject, string, JobRunResult>? Render { get; set; }

        public Task<JobRunResult> RunAsync(JobRecord job, string jobDir, string paramsFile, FreeCadRunKind kind,
            TimeSpan timeout, CancellationToken cancelToken, Action<int>? onProcessStarted = null)
        {
            var p = JsonNode.Parse(File.ReadAllText(paramsFile))!.AsObject();
            var handler = kind == FreeCadRunKind.Pipeline ? Pipeline : Render;
            return Task.FromResult(handler!(p, jobDir));
        }
    }

    private (JobExecutor Executor, FakeRunner Runner, JobStore Store, Scope Scope, string Stl, string OutDir) Harness()
    {
        var root = Path.Combine(Path.GetTempPath(), "fcadserve-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var inputDir = Path.Combine(root, "in");
        var outDir = Path.Combine(root, "out");
        Directory.CreateDirectory(inputDir);
        Directory.CreateDirectory(outDir);
        var stl = Path.Combine(inputDir, "part.stl");
        File.WriteAllBytes(stl, new byte[256]);

        var options = new ServiceOptions
        {
            StateRoot = Path.Combine(root, "state"),
            AllowedInputRoots = [inputDir],
            AllowedOutputRoots = [outDir],
            FreeCad = { Mode = "exec", Command = "/bin/true" },
            Jobs = { TimeoutSeconds = 60, MaxInputSizeBytes = 1_000_000 },
        };
        var db = new JobDatabase(options, NullLogger<JobDatabase>.Instance);
        db.InitializeAsync().GetAwaiter().GetResult();
        var store = new JobStore(db, options, NullLogger<JobStore>.Instance);
        var runner = new FakeRunner();
        var executor = new JobExecutor(store, db, runner, new ActiveProcessRegistry(), new PathPolicy(options),
            options, NullLogger<JobExecutor>.Instance);
        return (executor, runner, store, new Scope(root), stl, outDir);
    }

    private static async Task<JobRecord> SubmitAsync(JobStore store, string stl, string outDir)
        => await store.CreateAsync(store.CreateJobId(), stl, outDir, outDir, new FileInfo(stl).Length, "sha");

    private static void WritePipelineArtifacts(JsonObject p, bool includeSection = true, string status = "skipped", string? reason = "fallback")
    {
        var artifactsDir = (string)p["artifacts_dir"]!;
        var targets = p["target_names"]!.AsObject();
        Directory.CreateDirectory(artifactsDir);

        byte[] content = [1, 2, 3, 4];
        var step = Path.Combine(artifactsDir, (string)targets["step"]!);
        File.WriteAllBytes(step, content);
        var iso = Path.Combine(artifactsDir, (string)targets["iso_png"]!);
        File.WriteAllBytes(iso, content);
        if (includeSection)
            File.WriteAllBytes(Path.Combine(artifactsDir, (string)targets["section_png"]!), content);
        var report = Path.Combine(artifactsDir, (string)targets["report"]!);
        File.WriteAllText(report, "{\"ok\":true}");

        var statusObj = new JsonObject
        {
            ["status"] = status,
            ["reason"] = reason,
            ["report_path"] = report,
        };
        File.WriteAllText((string)p["status_path"]!, statusObj.ToJsonString());
    }

    private static void WriteRenderResult(JsonObject p, bool ok = true, string? error = null)
    {
        var result = new JsonObject { ["ok"] = ok, ["error"] = error };
        File.WriteAllText(Path.Combine((string)p["job_dir"]!, "render.result.json"), result.ToJsonString());
    }

    [Fact]
    public async Task Successful_skipped_reconstruction_publishes_artifacts_without_section_failure()
    {
        var (executor, runner, store, scope, stl, outDir) = Harness();
        using var _ = scope;

        runner.Pipeline = (p, _) => { WritePipelineArtifacts(p); return NewResult(RunOutcomeKind.Completed, 0); };
        runner.Render = (p, _) => { WriteRenderResult(p); return NewResult(RunOutcomeKind.Completed, 0); };

        var job = await SubmitAsync(store, stl, outDir);
        await executor.ExecuteAsync(job, CancellationToken.None);

        var final = (await store.GetAsync(job.Id))!;
        Assert.Equal(JobStatus.Skipped, final.Status);
        Assert.Equal(JobPhase.Finished, final.Phase);
        Assert.Null(final.Error);

        var artifacts = final.Artifacts();
        Assert.Contains(artifacts, a => a.Kind == "step" && a.Name == "part.stp");
        Assert.Contains(artifacts, a => a.Kind == "iso_png" && a.Name == "part_iso.png");
        Assert.Contains(artifacts, a => a.Kind == "section_png" && a.Name == "part_section.png");
        Assert.Contains(artifacts, a => a.Kind == "report" && a.Name == "part.report.json");

        Assert.True(File.Exists(Path.Combine(outDir, "part.stp")));
        Assert.True(File.Exists(Path.Combine(outDir, "part_iso.png")));
        Assert.True(File.Exists(Path.Combine(outDir, "part.report.json")));
    }

    [Fact]
    public async Task Successful_reconstruction_marks_job_succeeded()
    {
        var (executor, runner, store, scope, stl, outDir) = Harness();
        using var _ = scope;

        runner.Pipeline = (p, _) => { WritePipelineArtifacts(p, status: "reconstructed"); return NewResult(RunOutcomeKind.Completed, 0); };
        runner.Render = (p, _) => { WriteRenderResult(p); return NewResult(RunOutcomeKind.Completed, 0); };

        var job = await SubmitAsync(store, stl, outDir);
        await executor.ExecuteAsync(job, CancellationToken.None);
        Assert.Equal(JobStatus.Succeeded, (await store.GetAsync(job.Id))!.Status);
    }

    [Fact]
    public async Task Missing_section_image_with_valid_iso_still_succeeds()
    {
        var (executor, runner, store, scope, stl, outDir) = Harness();
        using var _ = scope;

        runner.Pipeline = (p, _) =>
        {
            WritePipelineArtifacts(p, includeSection: false);
            return NewResult(RunOutcomeKind.Completed, 0);
        };
        runner.Render = (p, _) => { WriteRenderResult(p); return NewResult(RunOutcomeKind.Completed, 0); };

        var job = await SubmitAsync(store, stl, outDir);
        await executor.ExecuteAsync(job, CancellationToken.None);

        var final = (await store.GetAsync(job.Id))!;
        Assert.Equal(JobStatus.Skipped, final.Status);
        Assert.DoesNotContain(final.Artifacts(), a => a.Kind == "section_png");
        Assert.Contains(final.Artifacts(), a => a.Kind == "iso_png");
    }

    [Fact]
    public async Task Worker_without_status_file_is_missing_status_failure()
    {
        var (executor, runner, store, scope, stl, outDir) = Harness();
        using var _ = scope;

        runner.Pipeline = (p, _) => NewResult(RunOutcomeKind.Completed, 0);

        var job = await SubmitAsync(store, stl, outDir);
        await executor.ExecuteAsync(job, CancellationToken.None);

        var final = (await store.GetAsync(job.Id))!;
        Assert.Equal(JobStatus.Failed, final.Status);
        Assert.Equal("missing_status", final.ErrorCode);
    }

    [Fact]
    public async Task Worker_reporting_failed_status_fails_with_its_error_code()
    {
        var (executor, runner, store, scope, stl, outDir) = Harness();
        using var _ = scope;

        runner.Pipeline = (p, _) =>
        {
            File.WriteAllText(
                (string)p["status_path"]!,
                "{\"status\":\"failed\",\"error_code\":\"reconstruct_error\",\"error\":\"kaboom\"}");
            return NewResult(RunOutcomeKind.Completed, 1);
        };

        var job = await SubmitAsync(store, stl, outDir);
        await executor.ExecuteAsync(job, CancellationToken.None);

        var final = (await store.GetAsync(job.Id))!;
        Assert.Equal(JobStatus.Failed, final.Status);
        Assert.Equal("reconstruct_error", final.ErrorCode);
        Assert.Equal(1, final.ExitCode);
    }

    [Fact]
    public async Task Render_without_result_file_is_missing_render_result_failure()
    {
        var (executor, runner, store, scope, stl, outDir) = Harness();
        using var _ = scope;

        runner.Pipeline = (p, _) => { WritePipelineArtifacts(p); return NewResult(RunOutcomeKind.Completed, 0); };
        runner.Render = (p, _) => NewResult(RunOutcomeKind.Completed, 0);

        var job = await SubmitAsync(store, stl, outDir);
        await executor.ExecuteAsync(job, CancellationToken.None);

        var final = (await store.GetAsync(job.Id))!;
        Assert.Equal(JobStatus.Failed, final.Status);
        Assert.Equal("missing_render_result", final.ErrorCode);
    }

    [Fact]
    public async Task Failed_render_result_fails_the_job()
    {
        var (executor, runner, store, scope, stl, outDir) = Harness();
        using var _ = scope;

        runner.Pipeline = (p, _) => { WritePipelineArtifacts(p); return NewResult(RunOutcomeKind.Completed, 0); };
        runner.Render = (p, _) => { WriteRenderResult(p, ok: false, error: "iso failed"); return NewResult(RunOutcomeKind.Completed, 1); };

        var job = await SubmitAsync(store, stl, outDir);
        await executor.ExecuteAsync(job, CancellationToken.None);

        var final = (await store.GetAsync(job.Id))!;
        Assert.Equal(JobStatus.Failed, final.Status);
        Assert.Equal("render_failed", final.ErrorCode);
    }

    [Fact]
    public async Task Pipeline_timeout_fails_with_job_timeout_and_terminates()
    {
        var (executor, runner, store, scope, stl, outDir) = Harness();
        using var _ = scope;

        runner.Pipeline = (p, _) => NewResult(RunOutcomeKind.TimedOut, null, "job_timeout", "exceeded timeout");

        var job = await SubmitAsync(store, stl, outDir);
        await executor.ExecuteAsync(job, CancellationToken.None);

        var final = (await store.GetAsync(job.Id))!;
        Assert.True(final.IsTerminal);
        Assert.Equal(JobStatus.Failed, final.Status);
        Assert.Equal("job_timeout", final.ErrorCode);
    }

    [Fact]
    public async Task Cancelled_run_outcome_is_terminated_as_cancelled()
    {
        var (executor, runner, store, scope, stl, outDir) = Harness();
        using var _ = scope;

        runner.Pipeline = (p, _) =>
        {
            WritePipelineArtifacts(p);
            return NewResult(RunOutcomeKind.CancelledOrStopped, null, "cancelled", "job was cancelled.");
        };

        var job = await SubmitAsync(store, stl, outDir);
        await executor.ExecuteAsync(job, CancellationToken.None);

        var final = (await store.GetAsync(job.Id))!;
        Assert.Equal(JobStatus.Cancelled, final.Status);
        Assert.Equal("cancelled", final.ErrorCode);
    }

    [Fact]
    public async Task FreeCad_unable_to_start_fails_with_start_error()
    {
        var (executor, runner, store, scope, stl, outDir) = Harness();
        using var _ = scope;

        runner.Pipeline = (p, _) => NewResult(RunOutcomeKind.FailedToStart, null, "freecad_start_failed", "FreeCAD process could not be started.");

        var job = await SubmitAsync(store, stl, outDir);
        await executor.ExecuteAsync(job, CancellationToken.None);

        var final = (await store.GetAsync(job.Id))!;
        Assert.Equal(JobStatus.Failed, final.Status);
        Assert.Equal("freecad_start_failed", final.ErrorCode);
    }

    [Fact]
    public async Task Render_phase_timeout_fails_with_job_timeout()
    {
        var (executor, runner, store, scope, stl, outDir) = Harness();
        using var _ = scope;

        runner.Pipeline = (p, _) => { WritePipelineArtifacts(p); return NewResult(RunOutcomeKind.Completed, 0); };
        runner.Render = (p, _) => NewResult(RunOutcomeKind.TimedOut, null, "job_timeout", "job exceeded the timeout and was terminated.");

        var job = await SubmitAsync(store, stl, outDir);
        await executor.ExecuteAsync(job, CancellationToken.None);

        var final = (await store.GetAsync(job.Id))!;
        Assert.Equal(JobStatus.Failed, final.Status);
        Assert.Equal("job_timeout", final.ErrorCode);
        Assert.Contains(final.Artifacts(), a => a.Kind == "status");
    }

    private static JobRunResult NewResult(RunOutcomeKind kind, int? exitCode, string? errorCode = null, string? error = null)
        => new(kind, exitCode, errorCode, error);
}