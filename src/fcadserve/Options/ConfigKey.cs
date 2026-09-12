namespace FcadServe.Options;

/// <summary>
/// Helpers for the FCADSERVE_* environment-key naming. Keys like
/// STATE_ROOT and JOBS__TIMEOUT_SECONDS (the env-name equivalent of the
/// "Jobs:TimeoutSeconds" binding path) are rewritten to the PascalCase
/// paths the configuration binder expects: "__" separates nested sections
/// and is joined with ':', while single '_' joins the word tokens inside a
/// section, e.g. JOBS__TIMEOUT_SECONDS -> Jobs:TimeoutSeconds and
/// ALLOWED_INPUT_ROOTS -> AllowedInputRoots.
/// </summary>
public static class ConfigKey
{
    public static string Normalize(string key)
    {
        var segments = key.Split("__", StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < segments.Length; i++)
        {
            var tokens = segments[i].Split('_', StringSplitOptions.RemoveEmptyEntries);
            segments[i] = string.Concat(tokens.Select(t => char.ToUpperInvariant(t[0]) + t[1..].ToLowerInvariant()));
        }
        return string.Join(':', segments);
    }
}