using FcadServe.Jobs;
using FcadServe.Options;
using FcadServe.Security;

namespace FcadServe.Health;

public sealed record CheckResult(bool Ok, string Detail);

/// <summary>
/// Readiness probe: verifies the FreeCAD executable, Xvfb, database, worker
/// script and output policy are usable before reporting "ready".
/// </summary>
public sealed class ReadinessState(ServiceOptions options, JobDatabase db)
{
    public async Task<Dictionary<string, CheckResult>> RunAsync(CancellationToken ct = default)
    {
        var checks = new Dictionary<string, CheckResult>
        {
            ["database"] = await CheckDatabaseAsync(ct),
            ["state_root"] = CheckStateRoot(),
            ["xvfb"] = CheckExecutable(options.Xvfb.Executable, "FCADSERVE_XVFB__EXECUTABLE"),
            ["worker_script"] = CheckWorkerScript(),
            ["freecad"] = CheckFreeCad(),
            ["input_roots"] = CheckInputRoots(),
            ["output_policy"] = CheckOutputPolicy(),
        };
        return checks;
    }

    private async Task<CheckResult> CheckDatabaseAsync(CancellationToken ct)
    {
        try
        {
            _ = await db.CountAsync(ct);
            return new CheckResult(true, $"sqlite ok at {Path.Combine(options.StateRoot, "jobs.db")}");
        }
        catch (Exception ex)
        {
            return new CheckResult(false, $"database error: {ex.Message}");
        }
    }

    private CheckResult CheckStateRoot()
    {
        try
        {
            Directory.CreateDirectory(options.StateRoot);
            var probe = Path.Combine(options.StateRoot, ".probe-" + Guid.NewGuid().ToString("N"));
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
            return new CheckResult(true, options.StateRoot);
        }
        catch (Exception ex)
        {
            return new CheckResult(false, $"state root not writable: {ex.Message}");
        }
    }

    private CheckResult CheckWorkerScript()
        => File.Exists(options.WorkerScript)
            ? new CheckResult(true, options.WorkerScript)
            : new CheckResult(false, $"worker script missing: {options.WorkerScript}");

    private CheckResult CheckFreeCad()
    {
        if (string.Equals(options.FreeCad.Mode, "exec", StringComparison.OrdinalIgnoreCase))
        {
            var cmd = options.FreeCad.Command;
            if (string.IsNullOrEmpty(cmd))
                return new CheckResult(false, "FreeCAD mode 'exec' but FCADSERVE_FREECAD__COMMAND is not set");
            return CheckExecutable(cmd, "FCADSERVE_FREECAD__COMMAND") with
            {
                Detail = $"FreeCAD exec: {cmd}",
            };
        }

        var flatpak = CheckExecutable("flatpak", "PATH");
        if (!flatpak.Ok)
            return flatpak;
        return new CheckResult(true, $"flatpak mode: {options.FreeCad.FlatpakAppId} (verify installed with 'flatpak list' at setup time)");
    }

    private CheckResult CheckExecutable(string name, string source)
    {
        if (Path.IsPathRooted(name))
            return File.Exists(name)
                ? new CheckResult(true, name)
                : new CheckResult(false, $"executable missing: {name}");

        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in path.Split(':', StringSplitOptions.RemoveEmptyEntries))
        {
            if (File.Exists(Path.Combine(dir, name)))
                return new CheckResult(true, Path.Combine(dir, name));
        }
        return new CheckResult(false, $"'{name}' not found on PATH ({source})");
    }

    private CheckResult CheckInputRoots()
    {
        var roots = PathUtils.SplitList(options.AllowedInputRoots);
        if (roots.Length == 0)
            return new CheckResult(false, "no allowed input roots configured (FCADSERVE_ALLOWED_INPUT_ROOTS); all submissions rejected");
        var missing = roots.Where(r => !Directory.Exists(r)).ToList();
        return missing.Count == 0
            ? new CheckResult(true, $"input roots: {string.Join(", ", roots)}")
            : new CheckResult(false, $"input roots missing or inaccessible: {string.Join(", ", missing)}");
    }

    private CheckResult CheckOutputPolicy()
    {
        var roots = PathUtils.SplitList(options.AllowedOutputRoots);
        if (roots.Length == 0)
            return new CheckResult(true, "no output roots configured; writes go only to each input STL's parent directory");
        return new CheckResult(true, $"output roots: {string.Join(", ", roots)}");
    }
}