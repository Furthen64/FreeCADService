using Microsoft.Data.Sqlite;
using FcadServe.Options;

namespace FcadServe.Jobs;

public enum JobStatus
{
    Queued,
    Running,
    Succeeded,
    Skipped,
    Failed,
    Cancelled,
}

public enum JobPhase
{
    Queued,
    StartingFreeCad,
    LoadingStl,
    Reconstructing,
    ExportingStep,
    ValidatingStep,
    Rendering,
    WritingReport,
    Finished,
}

/// <summary>Persistent job record. Stored as one row in the SQLite database.</summary>
public sealed class JobRecord
{
    public required string Id { get; init; }
    public JobStatus Status { get; set; } = JobStatus.Queued;
    public JobPhase Phase { get; set; } = JobPhase.Queued;
    public DateTimeOffset CreatedAtUtc { get; init; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
    public DateTimeOffset? StartedAtUtc { get; set; }
    public DateTimeOffset? FinishedAtUtc { get; set; }
    public required string StlPath { get; init; }
    public string? RequestedOutputDir { get; init; }
    public required string EffectiveOutputDir { get; init; }
    public long? InputSizeBytes { get; set; }
    public string? InputSha256 { get; set; }
    public int? ExitCode { get; set; }
    public string? ErrorCode { get; set; }
    public string? Error { get; set; }
    public string? ReportPath { get; set; }
    public string? ArtifactJson { get; set; }
    public bool CancelRequested { get; set; }
    public bool IsTerminal => Status is JobStatus.Succeeded or JobStatus.Skipped or JobStatus.Failed or JobStatus.Cancelled;
}

/// <summary>
/// SQLite-backed job database. All access is serialized through a semaphore and
/// uses parameterized queries; timestamps are stored as UTC ISO-8601 strings.
/// </summary>
public sealed class JobDatabase(ServiceOptions options, ILogger<JobDatabase> logger)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _connectionString;

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        var stateRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(options.StateRoot));
        Directory.CreateDirectory(stateRoot);
        var dbPath = Path.Combine(stateRoot, "jobs.db");
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = true,
            DefaultTimeout = 30,
        };
        _connectionString = builder.ToString();
        await _gate.WaitAsync(ct);
        try
        {
            await using var conn = CreateConnection();
            await conn.OpenAsync(ct);
            await using (var wal = conn.CreateCommand())
            {
                wal.CommandText = "PRAGMA journal_mode=WAL;";
                await wal.ExecuteNonQueryAsync(ct);
            }
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE IF NOT EXISTS jobs (
                    id TEXT PRIMARY KEY,
                    status TEXT NOT NULL,
                    phase TEXT NOT NULL,
                    created_at TEXT NOT NULL,
                    updated_at TEXT NOT NULL,
                    started_at TEXT,
                    finished_at TEXT,
                    stl_path TEXT NOT NULL,
                    requested_output_dir TEXT,
                    effective_output_dir TEXT NOT NULL,
                    input_size_bytes INTEGER,
                    input_sha256 TEXT,
                    exit_code INTEGER,
                    error_code TEXT,
                    error TEXT,
                    report_path TEXT,
                    artifact_json TEXT,
                    cancel_requested INTEGER NOT NULL DEFAULT 0
                );
                CREATE INDEX IF NOT EXISTS idx_jobs_updated ON jobs(updated_at);
                """;
            await cmd.ExecuteNonQueryAsync(ct);
        }
        finally
        {
            _gate.Release();
        }
        logger.LogInformation("Database initialized at {DbPath}", dbPath);
    }

    private SqliteConnection CreateConnection() =>
        new(_connectionString ?? throw new InvalidOperationException("Database not initialized."));

    public async Task InsertAsync(JobRecord job, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await using var conn = CreateConnection();
            await conn.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO jobs (id, status, phase, created_at, updated_at, started_at, finished_at,
                                  stl_path, requested_output_dir, effective_output_dir, input_size_bytes,
                                  input_sha256, exit_code, error_code, error, report_path, artifact_json,
                                  cancel_requested)
                VALUES ($id, $status, $phase, $created, $updated, $started, $finished,
                        $stl, $outdir, $effout, $size, $sha, $exit, $errcode, $err, $report, $artifacts,
                        $cancel);
                """;
            cmd.Parameters.AddWithValue("$id", job.Id);
            BindJob(cmd, job);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task UpdateAsync(JobRecord job, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await using var conn = CreateConnection();
            await conn.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                UPDATE jobs SET status=$status, phase=$phase, updated_at=$updated, started_at=$started,
                       finished_at=$finished, exit_code=$exit, error_code=$errcode, error=$err,
                       report_path=$report, artifact_json=$artifacts, cancel_requested=$cancel
                WHERE id=$id;
                """;
            cmd.Parameters.AddWithValue("$id", job.Id);
            BindJob(cmd, job);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<JobRecord?> GetAsync(string id, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await using var conn = CreateConnection();
            await conn.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT * FROM jobs WHERE id=$id;";
            cmd.Parameters.AddWithValue("$id", id);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct))
                return null;
            return ReadJob(reader);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task DeleteAsync(string id, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await using var conn = CreateConnection();
            await conn.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM jobs WHERE id=$id;";
            cmd.Parameters.AddWithValue("$id", id);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<List<JobRecord>> GetByStatusAsync(IEnumerable<JobStatus> statuses, CancellationToken ct = default)
    {
        var names = statuses.Select(s => s.ToString()).ToList();
        await _gate.WaitAsync(ct);
        try
        {
            await using var conn = CreateConnection();
            await conn.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $"SELECT * FROM jobs WHERE status IN ({string.Join(",", names.Select((_, i) => $"$s{i}"))}) ORDER BY created_at;";
            for (var i = 0; i < names.Count; i++)
                cmd.Parameters.AddWithValue($"$s{i}", names[i]);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            var jobs = new List<JobRecord>();
            while (await reader.ReadAsync(ct))
                jobs.Add(ReadJob(reader));
            return jobs;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Counts all job rows for retention pruning.</summary>
    public async Task<int> CountAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await using var conn = CreateConnection();
            await conn.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM jobs;";
            var result = await cmd.ExecuteScalarAsync(ct);
            return Convert.ToInt32(result);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Deletes the oldest terminal job rows beyond the given retention limit.</summary>
    public async Task<List<JobRecord>> PruneTerminalAsync(int maxRetained, CancellationToken ct = default)
    {
        var removed = new List<JobRecord>();
        await _gate.WaitAsync(ct);
        try
        {
            await using var conn = CreateConnection();
            await conn.OpenAsync(ct);
            await using var select = conn.CreateCommand();
            select.CommandText = """
                SELECT * FROM jobs WHERE status IN ('Succeeded','Skipped','Failed','Cancelled')
                ORDER BY updated_at DESC LIMIT $keep;
                """;
            select.Parameters.AddWithValue("$keep", long.MaxValue);
            var terminal = new List<JobRecord>();
            await using (var reader = await select.ExecuteReaderAsync(ct))
            {
                while (await reader.ReadAsync(ct))
                    terminal.Add(ReadJob(reader));
            }
            if (terminal.Count <= maxRetained)
                return removed;

            var toDelete = terminal.Skip(maxRetained).Select(j => j.Id).ToList();
            foreach (var id in toDelete)
            {
                var del = conn.CreateCommand();
                del.CommandText = $"DELETE FROM jobs WHERE id=$id;";
                del.Parameters.AddWithValue("$id", id);
                await del.ExecuteNonQueryAsync(ct);
            }
            removed = terminal.Where(j => toDelete.Contains(j.Id)).ToList();
        }
        finally
        {
            _gate.Release();
        }
        return removed;
    }

    private static void BindJob(SqliteCommand cmd, JobRecord job)
    {
        cmd.Parameters.AddWithValue("$status", job.Status.ToString());
        cmd.Parameters.AddWithValue("$phase", job.Phase.ToString());
        cmd.Parameters.AddWithValue("$created", job.CreatedAtUtc.ToString("O"));
        cmd.Parameters.AddWithValue("$updated", job.UpdatedAtUtc.ToString("O"));
        cmd.Parameters.AddWithValue("$started", job.StartedAtUtc?.ToString("O") as object ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$finished", job.FinishedAtUtc?.ToString("O") as object ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$stl", job.StlPath);
        cmd.Parameters.AddWithValue("$outdir", job.RequestedOutputDir as object ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$effout", job.EffectiveOutputDir);
        cmd.Parameters.AddWithValue("$size", job.InputSizeBytes as object ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$sha", job.InputSha256 as object ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$exit", job.ExitCode as object ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$errcode", job.ErrorCode as object ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$err", job.Error as object ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$report", job.ReportPath as object ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$artifacts", job.ArtifactJson as object ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$cancel", job.CancelRequested ? 1 : 0);
    }

    private static JobRecord ReadJob(SqliteDataReader reader)
    {
        static string S(SqliteDataReader r, int i) => r.IsDBNull(i) ? string.Empty : r.GetString(i);
        static string? NS(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);
        static long? NL(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetInt64(i);
        static int? NI(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetInt32(i);

        var id = S(reader, 0);
        return new JobRecord
        {
            Id = id,
            Status = Enum.Parse<JobStatus>(S(reader, 1)),
            Phase = Enum.Parse<JobPhase>(S(reader, 2)),
            CreatedAtUtc = DateTimeOffset.Parse(S(reader, 3)),
            UpdatedAtUtc = DateTimeOffset.Parse(S(reader, 4)),
            StartedAtUtc = NS(reader, 5) is { } st ? DateTimeOffset.Parse(st) : null,
            FinishedAtUtc = NS(reader, 6) is { } fn ? DateTimeOffset.Parse(fn) : null,
            StlPath = S(reader, 7),
            RequestedOutputDir = NS(reader, 8),
            EffectiveOutputDir = S(reader, 9),
            InputSizeBytes = NL(reader, 10),
            InputSha256 = NS(reader, 11),
            ExitCode = NI(reader, 12),
            ErrorCode = NS(reader, 13),
            Error = NS(reader, 14),
            ReportPath = NS(reader, 15),
            ArtifactJson = NS(reader, 16),
            CancelRequested = reader.GetInt32(17) != 0,
        };
    }
}

public sealed class ArtifactFile
{
    public string Name { get; set; } = "";
    public string Kind { get; set; } = "unknown";
    public string Path { get; set; } = "";
    public long SizeBytes { get; set; }

    public static string Serialize(IEnumerable<ArtifactFile> files) =>
        System.Text.Json.JsonSerializer.Serialize(files);

    public static List<ArtifactFile> Deserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return [];
        return System.Text.Json.JsonSerializer.Deserialize<List<ArtifactFile>>(json) ?? [];
    }
}