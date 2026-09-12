using FcadServe.FreeCad;
using FcadServe.Options;

namespace FcadServe.Tests;

public class CommandBuilderTests
{
    [Fact]
    public void Flatpak_headless_uses_FreeCADCmd_and_no_display()
    {
        var psi = FreeCadCommandBuilder.Build(FlatpakOptions(), "/opt/scripts/w.py", "/job/params.json", display: null, headless: true);
        Assert.Equal("setsid", psi.FileName);
        Assert.Equal(
            ["flatpak", "run", "--command=FreeCADCmd", "org.freecad.FreeCAD", "/opt/scripts/w.py"],
            psi.ArgumentList);
        Assert.Equal("/job/params.json", psi.Environment["FCADSERVE_JOB_PARAMS"]);
        Assert.False(psi.Environment.ContainsKey("DISPLAY"));
    }

    [Fact]
    public void Flatpak_render_omits_FreeCADCmd_and_sets_display()
    {
        var psi = FreeCadCommandBuilder.Build(FlatpakOptions(), "/opt/scripts/r.py", "/job/params.json", display: ":11", headless: false);
        Assert.Equal(
            ["flatpak", "run", "org.freecad.FreeCAD", "/opt/scripts/r.py"],
            psi.ArgumentList);
        Assert.Equal(":11", psi.Environment["DISPLAY"]);
    }

    [Fact]
    public void Exec_mode_uses_configured_command_and_no_shell()
    {
        var options = FlatpakOptions();
        options.FreeCad.Mode = "exec";
        options.FreeCad.Command = "/custom/freecad";

        var psi = FreeCadCommandBuilder.Build(options, "/opt/scripts/w.py", "/job/params.json", display: null, headless: true);
        Assert.Equal("/custom/freecad", psi.FileName);
        Assert.Equal("/opt/scripts/w.py", psi.ArgumentList.Single());
        Assert.Equal("/job/params.json", psi.Environment["FCADSERVE_JOB_PARAMS"]);
        Assert.False(psi.UseShellExecute);
    }

    [Fact]
    public void Exec_mode_without_command_throws()
    {
        var options = FlatpakOptions();
        options.FreeCad.Mode = "exec";
        options.FreeCad.Command = null;
        Assert.Throws<InvalidOperationException>(() =>
            FreeCadCommandBuilder.Build(options, "/opt/scripts/w.py", "/job/params.json", display: null, headless: true));
    }

    [Fact]
    public void Script_path_with_quotes_and_spaces_stays_a_single_argument()
    {
        var script = "/tmp/we ird 'x'.py";
        var options = FlatpakOptions();
        options.FreeCad.Mode = "exec";
        options.FreeCad.Command = "/bin/freecad";

        var psi = FreeCadCommandBuilder.Build(options, script, "/job/params.json", display: null, headless: true);
        Assert.Single(psi.ArgumentList);
        Assert.Equal(script, psi.ArgumentList[0]);
        Assert.DoesNotContain("&&", string.Join(" ", psi.ArgumentList));
    }

    [Fact]
    public void User_config_isolation_sets_xdg_dirs_under_job_dir()
    {
        var psi = FreeCadCommandBuilder.Build(FlatpakOptions(), "/opt/scripts/w.py", "/job/params.json", display: null, headless: true);
        FreeCadCommandBuilder.ApplyUserConfigIsolation(psi, FlatpakOptions(), "/job/jobdir");

        Assert.Equal(Path.Combine("/job/jobdir", "fc_config"), psi.Environment["XDG_CONFIG_HOME"]);
        Assert.Equal(Path.Combine("/job/jobdir", "fc_cache"), psi.Environment["XDG_CACHE_HOME"]);
        Assert.Equal(Path.Combine("/job/jobdir", "fc_data"), psi.Environment["XDG_DATA_HOME"]);
    }

    [Fact]
    public void User_config_isolation_disabled_leaves_xdg_unset()
    {
        var options = FlatpakOptions();
        options.FreeCad.IsolateUserConfig = false;
        var psi = FreeCadCommandBuilder.Build(options, "/opt/scripts/w.py", "/job/params.json", display: null, headless: true);
        FreeCadCommandBuilder.ApplyUserConfigIsolation(psi, options, "/job/jobdir");
        Assert.False(psi.Environment.ContainsKey("XDG_CONFIG_HOME"));
    }

    private static ServiceOptions FlatpakOptions() => new()
    {
        FreeCad = { Mode = "flatpak", FlatpakAppId = "org.freecad.FreeCAD" },
    };
}