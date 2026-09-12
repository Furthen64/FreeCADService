using FcadServe.Options;

namespace FcadServe.Tests;

public class ConfigKeyTests
{
    [Theory]
    [InlineData("STATE_ROOT", "StateRoot")]
    [InlineData("JOBS__TIMEOUT_SECONDS", "Jobs:TimeoutSeconds")]
    [InlineData("FREE__CAD__MODE", "Free:Cad:Mode")]
    [InlineData("ALLOWED_INPUT_ROOTS", "AllowedInputRoots")]
    [InlineData("Xvfb__Screen", "Xvfb:Screen")]
    public void Normalize_upper_case_env_key_to_pascal_case(string input, string expected)
        => Assert.Equal(expected, ConfigKey.Normalize(input));
}