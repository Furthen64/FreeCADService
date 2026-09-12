using FcadServe.Options;
using FcadServe.Security;

namespace FcadServe.Tests;

public class PathPolicyTests
{
    private static string MakeTempDir()
        => Path.Combine(Path.GetTempPath(), "fcadserve-" + Guid.NewGuid().ToString("N"));

    private static (string Root, string InputDir, string OutputDir, string Stl) MakeSandbox(string stlName = "part.stl")
    {
        var root = MakeTempDir();
        var input = Path.Combine(root, "in");
        var output = Path.Combine(root, "out");
        Directory.CreateDirectory(input);
        Directory.CreateDirectory(output);
        var stl = Path.Combine(input, stlName);
        File.WriteAllBytes(stl, new byte[128]);
        return (root, input, output, stl);
    }

    private static ServiceOptions OptionsFor((string Root, string InputDir, string OutputDir, string Stl) box, long maxBytes = 1_000_000)
        => new()
        {
            AllowedInputRoots = [box.InputDir],
            AllowedOutputRoots = [box.OutputDir],
            Jobs = { MaxInputSizeBytes = maxBytes },
        };

    [Fact]
    public void Plan_missing_or_relative_stl_is_rejected()
    {
        var box = MakeSandbox();
        var policy = new PathPolicy(OptionsFor(box));

        var ex1 = Assert.Throws<PathValidationException>(() => policy.Plan(null, box.OutputDir));
        Assert.Equal("missing_stl_path", ex1.Code);

        var ex2 = Assert.Throws<PathValidationException>(() => policy.Plan("part.stl", box.OutputDir));
        Assert.Equal("relative_path", ex2.Code);

        Cleanup(box.Root);
    }

    [Fact]
    public void Plan_unsupported_extension_is_rejected()
    {
        var box = MakeSandbox("part.obj");
        var policy = new PathPolicy(OptionsFor(box));
        var ex = Assert.Throws<PathValidationException>(() => policy.Plan(box.Stl, box.OutputDir));
        Assert.Equal("unsupported_extension", ex.Code);
        Cleanup(box.Root);
    }

    [Fact]
    public void Plan_missing_input_is_rejected()
    {
        var box = MakeSandbox();
        var policy = new PathPolicy(OptionsFor(box));
        var ex = Assert.Throws<PathValidationException>(() => policy.Plan(Path.Combine(box.InputDir, "nope.stl"), box.OutputDir));
        Assert.Equal("missing_file", ex.Code);
        Cleanup(box.Root);
    }

    [Fact]
    public void Plan_input_outside_allowed_root_is_rejected()
    {
        var root = MakeTempDir();
        Directory.CreateDirectory(root);
        var outside = Path.Combine(root, "elsewhere");
        Directory.CreateDirectory(outside);
        var stl = Path.Combine(outside, "x.stl");
        File.WriteAllText(stl, "abc");

        var options = new ServiceOptions
        {
            AllowedInputRoots = [Path.Combine(root, "in")],  // does not include "elsewhere"
            AllowedOutputRoots = [root],
        };
        var policy = new PathPolicy(options);
        var ex = Assert.Throws<PathValidationException>(() => policy.Plan(stl, root));
        Assert.Equal("input_outside_allowed_root", ex.Code);
        Cleanup(root);
    }

    [Fact]
    public void Plan_symlink_input_escaping_the_root_is_rejected()
    {
        var root = MakeTempDir();
        Directory.CreateDirectory(root);
        var real = Path.Combine(root, "real");
        Directory.CreateDirectory(real);
        File.WriteAllText(Path.Combine(real, "target.stl"), "abc");
        var under = Path.Combine(root, "under");
        Directory.CreateDirectory(under);
        var link = Path.Combine(under, "link.stl");
        File.CreateSymbolicLink(link, Path.Combine(real, "target.stl"));

        var options = new ServiceOptions
        {
            AllowedInputRoots = [under],                     // "under" is allowed
            AllowedOutputRoots = [root],
        };
        var policy = new PathPolicy(options);
        // link resolves to real/target.stl which is outside the single allowed
        // root "under", so this must be rejected.
        var ex = Assert.Throws<PathValidationException>(() => policy.Plan(link, root));
        Assert.Equal("input_outside_allowed_root", ex.Code);
        Cleanup(root);
    }

    [Fact]
    public void Plan_input_too_large_is_rejected()
    {
        var box = MakeSandbox();
        var policy = new PathPolicy(OptionsFor(box, maxBytes: 64)); // stl is 128 bytes
        var ex = Assert.Throws<PathValidationException>(() => policy.Plan(box.Stl, box.OutputDir));
        Assert.Equal("input_too_large", ex.Code);
        Cleanup(box.Root);
    }

    [Fact]
    public void Plan_output_outside_allowed_root_is_rejected()
    {
        var box = MakeSandbox();
        var policy = new PathPolicy(OptionsFor(box, maxBytes: 1_000_000));
        var elsewhere = Path.Combine(box.Root, "elsewhere");
        Directory.CreateDirectory(elsewhere);
        var ex = Assert.Throws<PathValidationException>(() => policy.Plan(box.Stl, elsewhere));
        Assert.Equal("output_outside_allowed_root", ex.Code);
        Cleanup(box.Root);
    }

    [Fact]
    public void Plan_empty_output_dir_uses_input_parent_and_is_allowed()
    {
        var box = MakeSandbox();
        // Default output policy (no configured output roots) falls back to the
        // input's parent directory.
        var options = new ServiceOptions
        {
            AllowedInputRoots = [box.InputDir],
            AllowedOutputRoots = [],
            Jobs = { MaxInputSizeBytes = 1_000_000 },
        };
        var policy = new PathPolicy(options);
        var (input, output) = policy.Plan(box.Stl, null);
        Assert.Equal(Path.GetFullPath(box.InputDir), Path.GetFullPath(output.Directory));
        Assert.Equal("part", input.Stem);
        Assert.Equal(Path.Combine(Path.GetFullPath(box.InputDir), "part.stp"), Path.GetFullPath(output.StepPath));
        Cleanup(box.Root);
    }

    [Fact]
    public void Plan_derives_deterministic_stem_based_output_names()
    {
        var box = MakeSandbox("my.part.stl");
        var policy = new PathPolicy(OptionsFor(box));
        var (_, output) = policy.Plan(box.Stl, box.OutputDir);
        Assert.Equal("my.part.stp", Path.GetFileName(output.StepPath));
        Assert.Equal("my.part_iso.png", Path.GetFileName(output.IsoPngPath));
        Assert.Equal("my.part_section.png", Path.GetFileName(output.SectionPngPath));
        Assert.Equal("my.part.report.json", Path.GetFileName(output.ReportPath));
        Cleanup(box.Root);
    }

    [Fact]
    public void Resolve_detects_existing_artifacts()
    {
        var box = MakeSandbox();
        var policy = new PathPolicy(OptionsFor(box));
        var step = Path.Combine(box.OutputDir, "part.stp");
        File.WriteAllText(step, "existing");

        var ex = Assert.Throws<PathValidationException>(() => policy.Resolve(box.Stl, box.OutputDir));
        Assert.Equal("output_already_exists", ex.Code);
        Cleanup(box.Root);
    }

    [Fact]
    public void Resolve_returns_sha256_of_the_input()
    {
        var box = MakeSandbox();
        var policy = new PathPolicy(OptionsFor(box));
        var (input, _) = policy.Resolve(box.Stl, box.OutputDir);
        Assert.True(input.Sha256.Length is 64);
        Assert.True(input.Sha256.All(char.IsAsciiHexDigit));
        Cleanup(box.Root);
    }

    private static void Cleanup(string root)
    {
        try { Directory.Delete(root, true); } catch { /* best effort */ }
    }
}