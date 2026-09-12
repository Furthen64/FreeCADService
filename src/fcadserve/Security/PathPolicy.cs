namespace FcadServe.Security;

/// <summary>Thrown when job input/output validation fails.</summary>
public sealed class PathValidationException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

public static class PathUtils
{
    /// <summary>Parses ";" and "," separated root lists from configuration.</summary>
    public static string[] SplitList(string[]? values)
    {
        if (values is null or { Length: 0 })
            return [];
        return values
            .SelectMany(v => v.Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    public static bool IsWithin(string path, string root)
    {
        var p = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var r = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        if (string.Equals(p, r, StringComparison.Ordinal))
            return true;
        if (string.Equals(r, Path.GetPathRoot(r), StringComparison.Ordinal))
            return true; // root of the filesystem contains every path
        return p.StartsWith(r + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }

    /// <summary>
    /// Resolves symlinks for every existing path component so allowlists cannot
    /// be bypassed by links that point outside a configured root. The final
    /// component and intermediate directories may not exist yet (output dirs).
    /// </summary>
    public static string ResolvePath(string path)
    {
        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full) ?? "/";
        var rel = full.Length > root.Length ? full[root.Length..] : string.Empty;
        var parts = rel.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
        var cur = root;
        foreach (var part in parts)
        {
            cur = Path.Combine(cur, part);
            if (!File.Exists(cur) && !Directory.Exists(cur))
            {
                // Component does not exist (future output path, or part of one).
                continue;
            }
            FileSystemInfo info = File.Exists(cur) ? new FileInfo(cur) : new DirectoryInfo(cur);
            var target = info.ResolveLinkTarget(returnFinalTarget: true) ?? info;
            if (target.FullName != cur)
                cur = target.FullName;
        }
        return Path.GetFullPath(cur);
    }
}

public sealed record InputValidation(string StlPath, string ResolvedStlPath, string Stem, long SizeBytes, string Sha256);

public sealed record OutputValidation(
    string Directory,
    string StepPath,
    string IsoPngPath,
    string SectionPngPath,
    string ReportPath,
    bool SectionAvailable);

/// <summary>
/// Validates job inputs and outputs against the configured filesystem policy.
/// Path handling is treated as a security boundary (see GENESIS spec).
/// </summary>
public sealed class PathPolicy(Options.ServiceOptions options)
{
    private static readonly string[] AllowedExtensions = [".stl"];

    private readonly string[] _inputRoots = PathUtils.SplitList(options.AllowedInputRoots);
    private readonly string[] _outputRoots = PathUtils.SplitList(options.AllowedOutputRoots);
    private readonly string[] _inputRootsResolved = PathUtils.SplitList(options.AllowedInputRoots).Select(PathUtils.ResolvePath).ToArray();
    private readonly string[] _outputRootsResolved = PathUtils.SplitList(options.AllowedOutputRoots).Select(PathUtils.ResolvePath).ToArray();

    public IReadOnlyList<string> AllowedInputRoots => _inputRoots;
    public IReadOnlyList<string> AllowedOutputRoots => _outputRoots;

    private static bool IsAbsolute(string path) => Path.IsPathFullyQualified(path) && Path.IsPathRooted(path);

    /// <summary>Full validation for job submission: paths, roots, collisions, checksum.</summary>
    public (InputValidation Input, OutputValidation Output) Resolve(string? stlPath, string? outputDir)
    {
        var (input, output) = Plan(stlPath, outputDir);

        // Never overwrite the input STL and avoid clobbering existing artifacts.
        if (!options.OverwriteArtifacts)
        {
            var existing = new[] { output.StepPath, output.IsoPngPath, output.SectionPngPath, output.ReportPath }.Where(File.Exists).ToList();
            if (existing.Count > 0)
                throw new PathValidationException("output_already_exists",
                    $"target artifact already exists in output directory (use overwrite policy or a different output_dir): {string.Join(", ", existing)}");
        }

        var sha256 = ComputeSha256(input.ResolvedStlPath);
        return (input with { Sha256 = sha256 }, output);
    }

    /// <summary>
    /// Validates paths and derives the output layout without side-effect checks
    /// (no collision check or checksum). Used by the worker executor, which must
    /// not fail just because a destination now exists.
    /// </summary>
    public (InputValidation Input, OutputValidation Output) Plan(string? stlPath, string? outputDir)
    {
        if (string.IsNullOrWhiteSpace(stlPath))
            throw new PathValidationException("missing_stl_path", "stl_path is required.");
        if (!IsAbsolute(stlPath))
            throw new PathValidationException("relative_path", "stl_path must be an absolute path.");

        var ext = Path.GetExtension(stlPath);
        if (!AllowedExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase))
            throw new PathValidationException("unsupported_extension", $"unsupported extension '{ext}' (allowed: .stl).");

        var resolvedInput = PathUtils.ResolvePath(stlPath);
        if (!File.Exists(resolvedInput))
            throw new PathValidationException("missing_file", $"input file does not exist: {resolvedInput}");
        if (Directory.Exists(resolvedInput))
            throw new PathValidationException("input_is_directory", "stl_path must point to a file.");

        if (_inputRootsResolved.Length > 0 &&
            !_inputRootsResolved.Any(root => PathUtils.IsWithin(resolvedInput, root)))
            throw new PathValidationException("input_outside_allowed_root", $"input file is outside the configured allowed input roots.");

        var fi = new FileInfo(resolvedInput);
        var size = fi.Length;
        if (size > options.Jobs.MaxInputSizeBytes)
            throw new PathValidationException("input_too_large",
                $"input file is {size} bytes, exceeding the maximum of {options.Jobs.MaxInputSizeBytes}.");

        // Effective output directory.
        string outDir;
        if (string.IsNullOrWhiteSpace(outputDir))
        {
            outDir = Path.GetDirectoryName(resolvedInput)
                     ?? throw new PathValidationException("invalid_output_dir", "cannot derive output directory from input path.");
        }
        else
        {
            if (!IsAbsolute(outputDir))
                throw new PathValidationException("relative_path", "output_dir must be an absolute path or empty.");
            outDir = outputDir;
        }

        var resolvedOut = PathUtils.ResolvePath(outDir);
        if (File.Exists(resolvedOut))
            throw new PathValidationException("output_is_file", "output_dir must be a directory.");

        // Output location policy: must be inside a configured output root, or,
        // when none are configured, the input's parent directory.
        var inputParent = Path.GetDirectoryName(resolvedInput)!;
        var allowedOutputs = _outputRootsResolved.Length > 0
            ? _outputRootsResolved
            : [PathUtils.ResolvePath(inputParent)];

        if (!allowedOutputs.Any(root => PathUtils.IsWithin(resolvedOut, root)))
            throw new PathValidationException("output_outside_allowed_root", "output_dir is outside the configured allowed output roots.");

        // Deterministic stem-based artifact names + unique job manifest.
        var stem = Path.GetFileNameWithoutExtension(stlPath);
        var step = Path.Combine(resolvedOut, $"{stem}.stp");
        var iso = Path.Combine(resolvedOut, $"{stem}_iso.png");
        var section = Path.Combine(resolvedOut, $"{stem}_section.png");
        var report = Path.Combine(resolvedOut, $"{stem}.report.json");

        // Never overwrite the input STL (name-space collision is inherent to the layout).
        if (string.Equals(Path.GetFullPath(step), resolvedInput, StringComparison.Ordinal)
            || string.Equals(Path.GetFullPath(iso), resolvedInput, StringComparison.Ordinal)
            || string.Equals(Path.GetFullPath(section), resolvedInput, StringComparison.Ordinal)
            || string.Equals(Path.GetFullPath(report), resolvedInput, StringComparison.Ordinal))
            throw new PathValidationException("artifact_collides_with_input", "derived artifact name collides with the input file.");

        return (
            new InputValidation(stlPath, resolvedInput, stem, size, ""),
            new OutputValidation(resolvedOut, step, iso, section, report, SectionAvailable: true));
    }

    private static string ComputeSha256(string path)
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        using var fs = File.OpenRead(path);
        var hash = sha.ComputeHash(fs);
        return Convert.ToHexStringLower(hash);
    }
}