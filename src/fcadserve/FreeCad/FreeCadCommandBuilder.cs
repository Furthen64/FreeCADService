using System.Diagnostics;
using FcadServe.Options;
using FcadServe.Security;

namespace FcadServe.FreeCad;

/// <summary>
/// Builds FreeCAD worker subprocess invocations. Values are placed into
/// <see cref="ProcessStartInfo.ArgumentList"/> — no string interpolation into a
/// shell command is ever performed.
/// </summary>
public static class FreeCadCommandBuilder
{
    /// <summary>
    /// Builds a StartInfo for a FreeCAD worker invocation. <paramref name="headless"/>
    /// uses the headless FreeCADCmd binary (no display); otherwise the GUI binary is
    /// used and <paramref name="display"/> must name an available X display.
    /// </summary>
    public static ProcessStartInfo Build(ServiceOptions options, string script, string paramsFile, string? display, bool headless)
    {
        var psi = new ProcessStartInfo
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        if (string.Equals(options.FreeCad.Mode, "exec", StringComparison.OrdinalIgnoreCase))
        {
            var command = options.FreeCad.Command
                ?? throw new InvalidOperationException("FCADSERVE_FREECAD__COMMAND is required when FreeCAD mode is 'exec'.");
            psi.FileName = command;
            psi.ArgumentList.Add(script);
        }
        else
        {
            psi.FileName = "setsid";
            psi.ArgumentList.Add("flatpak");
            psi.ArgumentList.Add("run");
            if (headless)
            {
                psi.ArgumentList.Add("--command=FreeCADCmd");
            }
            psi.ArgumentList.Add(options.FreeCad.FlatpakAppId);
            psi.ArgumentList.Add(script);
        }

        // FreeCAD's GUI treats extra positional arguments as documents to open,
        // so the job parameters travel via the environment instead.
        psi.Environment["FCADSERVE_JOB_PARAMS"] = paramsFile;
        if (headless)
        {
            // Headless FreeCADCmd must not pick up the parent's X display, even
            // in "exec" mode where the environment is inherited directly.
            psi.Environment.Remove("DISPLAY");
        }
        else if (!string.IsNullOrEmpty(display))
        {
            psi.Environment["DISPLAY"] = display;
        }
        return psi;
    }

    /// <summary>Applies per-job FreeCAD user configuration isolation, if configured.</summary>
    public static void ApplyUserConfigIsolation(ProcessStartInfo psi, ServiceOptions options, string jobDir)
    {
        if (!options.FreeCad.IsolateUserConfig)
            return;
        psi.Environment["XDG_CONFIG_HOME"] = Path.Combine(jobDir, "fc_config");
        psi.Environment["XDG_CACHE_HOME"] = Path.Combine(jobDir, "fc_cache");
        psi.Environment["XDG_DATA_HOME"] = Path.Combine(jobDir, "fc_data");
    }
}