using FcadServe.Security;

namespace FcadServe.Tests;

public class PathUtilsTests
{
    [Fact]
    public void SplitList_external_and_internal_separators()
    {
        var split = PathUtils.SplitList(["/a;/b,/c;", "/d"]);
        Assert.Equal(["/a", "/b", "/c", "/d"], split);
    }

    [Fact]
    public void SplitList_empty_and_null_returns_empty()
    {
        Assert.Empty(PathUtils.SplitList(null));
        Assert.Empty(PathUtils.SplitList([]));
        Assert.Empty(PathUtils.SplitList([";,", ""]));
    }

    [Theory]
    [InlineData("/data/x.stl", "/data", true)]       // child
    [InlineData("/data", "/data", true)]             // identical
    [InlineData("/data/sub/../x.stl", "/data", true)] // normalized child
    [InlineData("/dataa/x.stl", "/data", false)]     // sibling prefix must not match
    [InlineData("/data/x.stl", "/", true)]           // root
    public void IsWithin_matches_only_inside_root(string path, string root, bool expected)
    {
        var actual = PathUtils.IsWithin(path, root);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void ResolvePath_follows_symlink_components_outside_a_root()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "fcadserve-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(tmp, "real"));
        Directory.CreateDirectory(Path.Combine(tmp, "linkdir"));
        File.WriteAllText(Path.Combine(tmp, "real", "f.stl"), "x");

        // linkdir -> real (an existing directory symlink under tmp)
        Directory.CreateSymbolicLink(Path.Combine(tmp, "linkdir", "actual"), Path.Combine(tmp, "real"));

        var resolved = PathUtils.ResolvePath(Path.Combine(tmp, "linkdir", "actual", "f.stl"));
        Assert.Equal(Path.GetFullPath(Path.Combine(tmp, "real", "f.stl")), Path.GetFullPath(resolved));

        try { Directory.Delete(tmp, true); } catch { /* best effort */ }
    }

    [Fact]
    public void ResolvePath_handles_nonexistent_tail_components()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "fcadserve-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        var resolved = PathUtils.ResolvePath(Path.Combine(tmp, "notyet", "a.stp"));
        Assert.Equal(Path.GetFullPath(Path.Combine(tmp, "notyet", "a.stp")), Path.GetFullPath(resolved));
        try { Directory.Delete(tmp, true); } catch { /* best effort */ }
    }
}