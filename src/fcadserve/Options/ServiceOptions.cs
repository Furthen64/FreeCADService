namespace FcadServe.Options;

/// <summary>
/// Configuration model. Bound from the configuration root; environment
/// variables use the FCADSERVE_ prefix with "__" for nesting (for example
/// FCADSERVE_JOBS__TIMEOUT_SECONDS).
/// </summary>
public sealed class ServiceOptions
{
    /// <summary>Interface to bind to. Defaults to loopback only.</summary>
    public string BindAddress { get; set; } = "127.0.0.1";

    public int Port { get; set; } = 8688;

    /// <summary>Writable state directory for SQLite, job working directories, and logs.</summary>
    public string StateRoot { get; set; } = "/var/lib/fcadserve";

    /// <summary>Absolute path to the FreeCAD headless pipeline script.</summary>
    public string WorkerScript { get; set; } = "/usr/lib/fcadserve/fcadserve_worker.py";

    /// <summary>Absolute path to the FreeCAD GUI rendering script.</summary>
    public string RenderScript { get; set; } = "/usr/lib/fcadserve/fcadserve_render.py";

    /// <summary>
    /// Allowed roots where input STL files may live. Empty means nothing is
    /// allowed (jobs are rejected). Entries may use ";" or "," as separators.
    /// </summary>
    public string[]? AllowedInputRoots { get; set; }

    /// <summary>
    /// Allowed roots where outputs may be written. Empty means only the input
    /// STL's parent directory is allowed.
    /// </summary>
    public string[]? AllowedOutputRoots { get; set; }

    /// <summary>
    /// When false (default) a job whose output artifact already exists in the
    /// effective output directory is rejected to avoid silent overwrites.
    /// </summary>
    public bool OverwriteArtifacts { get; set; }

    public string LogLevel { get; set; } = "Information";

    public FreeCadOptions FreeCad { get; set; } = new();
    public XvfbOptions Xvfb { get; set; } = new();
    public JobOptions Jobs { get; set; } = new();
    public RetentionOptions Retention { get; set; } = new();
}

public sealed class FreeCadOptions
{
    /// <summary>"flatpak" runs <c>flatpak run &lt;id&gt;</c>; "exec" runs a raw
    /// executable via <see cref="Command"/>.
    ///
    /// NOTE on isolation: in "flatpak" mode FreeCAD runs inside its own sandbox
    /// and controls the XDG user dirs itself, so per-job user-config isolation
    /// is NOT effective there. In "exec" mode <see cref="IsolateUserConfig"/>
    /// works (the env vars reach the process) but the process has NO sandbox —
    /// "exec" implies trusting both the binary and the host.</summary>
    public string Mode { get; set; } = "flatpak";

    /// <summary>Flatpak application ID, e.g. org.freecad.FreeCAD.</summary>
    public string FlatpakAppId { get; set; } = "org.freecad.FreeCAD";

    /// <summary>Raw executable used when <see cref="Mode"/> is "exec".</summary>
    public string? Command { get; set; }

    /// <summary>seconds to allow the FreeCAD process to start before failing.</summary>
    public int StartupTimeoutSec { get; set; } = 120;

    /// <summary>custom FreeCAD user configuration paths are per-job when true.</summary>
    public bool IsolateUserConfig { get; set; } = true;
}

public sealed class XvfbOptions
{
    public string Executable { get; set; } = "Xvfb";

    /// <summary>screen geometry passed to Xvfb, e.g. 1280x1024x24.</summary>
    public string Screen { get; set; } = "1280x1024x24";
}

public sealed class JobOptions
{
    /// <summary>Number of concurrent worker subprocesses.</summary>
    public int WorkerCount { get; set; } = 1;

    /// <summary>Maximum queued-but-not-started jobs accepted by the API.</summary>
    public int QueueCapacity { get; set; } = 100;

    /// <summary>Per-job wall-clock timeout in seconds.</summary>
    public int TimeoutSeconds { get; set; } = 600;

    /// <summary>Maximum accepted input STL size in bytes. Default is small
    /// enough that a single FreeCAD reconstruction of the mesh stays within
    /// reasonable memory; raise it only if you accept the resource cost.</summary>
    public long MaxInputSizeBytes { get; set; } = 50L * 1024 * 1024;

    /// <summary>Allocate a fresh job working directory on every run.</summary>
    public bool RequeueRunningJobsOnStartup { get; set; } = true;
}

public sealed class RetentionOptions
{
    /// <summary>Maximum number of terminal job records kept in the database.</summary>
    public int MaxRetainedJobs { get; set; } = 500;
}