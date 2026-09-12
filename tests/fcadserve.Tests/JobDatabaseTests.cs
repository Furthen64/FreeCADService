using FcadServe.Jobs;
using FcadServe.Options;
using Microsoft.Extensions.Logging.Abstractions;

namespace FcadServe.Tests;

public class JobDatabaseTests
{
    private sealed class Scope(string root) : IDisposable
    {
        public string DbPath => Path.Combine(root, "jobs.db");
        public void Dispose() { try { Directory.Delete(root, true); } catch { /* best effort */ } }
    }

    private static async Task<(JobDatabase Db, Scope Scope)> OpenDbAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "fcadserve-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var options = new ServiceOptions { StateRoot = root };
        var db = new JobDatabase(options, NullLogger<JobDatabase>.Instance);
        await db.InitializeAsync();
        return (db, new Scope(root));
    }

    [Fact]
    public async Task Insert_get_update_delete_round_trip()
    {
        var (db, scope) = await OpenDbAsync();
        using var _ = scope;
        var job = NewJob("j_one");
        await db.InsertAsync(job);

        var got = await db.GetAsync("j_one");
        Assert.NotNull(got);
        Assert.Equal(JobStatus.Queued, got!.Status);
        Assert.Equal("j_one", got.Id);

        got.ErrorCode = "boom";
        got.Status = JobStatus.Failed;
        await db.UpdateAsync(got);

        var updated = await db.GetAsync("j_one");
        Assert.Equal(JobStatus.Failed, updated!.Status);
        Assert.Equal("boom", updated.ErrorCode);

        await db.DeleteAsync("j_one");
        Assert.Null(await db.GetAsync("j_one"));
    }

    [Fact]
    public async Task GetByStatus_filters_and_orders()
    {
        var (db, scope) = await OpenDbAsync();
        using var _ = scope;
        await db.InsertAsync(NewJob("j_a", JobStatus.Queued));
        await db.InsertAsync(NewJob("j_b", JobStatus.Running));
        await db.InsertAsync(NewJob("j_c", JobStatus.Failed));

        var running = await db.GetByStatusAsync([JobStatus.Running]);
        Assert.Single(running);
        Assert.Equal("j_b", running[0].Id);

        var nonTerminal = await db.GetByStatusAsync([JobStatus.Queued, JobStatus.Running]);
        Assert.Equal(2, nonTerminal.Count);
    }

    [Fact]
    public async Task Count_reflects_rows()
    {
        var (db, scope) = await OpenDbAsync();
        using var _ = scope;
        await db.InsertAsync(NewJob("j_a"));
        await db.InsertAsync(NewJob("j_b"));
        Assert.Equal(2, await db.CountAsync());
    }

    [Fact]
    public async Task PruneTerminalAsync_keeps_newest_and_removes_oldest_terminal()
    {
        var (db, scope) = await OpenDbAsync();
        using var _ = scope;

        var connString = "Data Source=" + scope.DbPath;
        using (var conn = new Microsoft.Data.Sqlite.SqliteConnection(connString))
        {
            await conn.OpenAsync();
            await using (var init = conn.CreateCommand())
            {
                init.CommandText = "PRAGMA journal_mode=WAL;";
                await init.ExecuteNonQueryAsync();
            }
        }

        await db.InsertAsync(NewJob("s1", JobStatus.Succeeded));
        await db.InsertAsync(NewJob("s2", JobStatus.Succeeded));
        await db.InsertAsync(NewJob("s3", JobStatus.Succeeded));
        await db.InsertAsync(NewJob("r1", JobStatus.Running));

        // Re-stamp ordering so "newest" is deterministic.
        using (var conn = new Microsoft.Data.Sqlite.SqliteConnection(connString))
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE jobs SET updated_at=$u WHERE id=$id;";
            cmd.Parameters.AddWithValue("$id", "s2");
            cmd.Parameters.AddWithValue("$u", DateTimeOffset.UtcNow.AddHours(1).ToString("O"));
            await cmd.ExecuteNonQueryAsync();
        }

        var pruned = await db.PruneTerminalAsync(2);
        Assert.Single(pruned);
        Assert.Contains("s1", pruned.Select(j => j.Id));

        Assert.Null(await db.GetAsync("s1"));      // oldest terminal removed
        Assert.NotNull(await db.GetAsync("s2"));   // refreshed terminal kept
        Assert.NotNull(await db.GetAsync("s3"));   // kept
        Assert.NotNull(await db.GetAsync("r1"));   // non-terminal untouched
    }

    private static JobRecord NewJob(string id, JobStatus status = JobStatus.Queued)
    {
        var now = DateTimeOffset.UtcNow;
        return new JobRecord
        {
            Id = id,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            StlPath = id,
            EffectiveOutputDir = "/tmp/out",
            Status = status,
        };
    }
}