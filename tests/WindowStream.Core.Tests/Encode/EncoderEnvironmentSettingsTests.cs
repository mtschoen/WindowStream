using WindowStream.Core.Encode;
using Xunit;

namespace WindowStream.Core.Tests.Encode;

public sealed class EncoderEnvironmentSettingsTests
{
    [Fact]
    public void Load_uses_current_low_latency_defaults_when_variables_are_absent()
    {
        var settings = EncoderEnvironmentSettings.Load(_ => null);

        Assert.Equal(60, settings.FramesPerSecond);
        Assert.Equal(30, settings.GroupOfPicturesLength);
        Assert.Equal("ull", settings.Tune);
        Assert.Equal(1, settings.SurfaceCount);
    }

    [Fact]
    public void Load_applies_each_environment_override_independently()
    {
        var variables = new Dictionary<string, string?>
        {
            ["WINDOWSTREAM_NVENC_FPS"] = "72",
            ["WINDOWSTREAM_NVENC_GOP"] = "45",
            ["WINDOWSTREAM_NVENC_TUNE"] = "ll",
            ["WINDOWSTREAM_NVENC_SURFACES"] = "3"
        };

        var settings = EncoderEnvironmentSettings.Load(
            name => variables.GetValueOrDefault(name));

        Assert.Equal(72, settings.FramesPerSecond);
        Assert.Equal(45, settings.GroupOfPicturesLength);
        Assert.Equal("ll", settings.Tune);
        Assert.Equal(3, settings.SurfaceCount);
    }

    [Fact]
    public void Load_accepts_maximum_integer_overrides()
    {
        var variables = new Dictionary<string, string?>
        {
            ["WINDOWSTREAM_NVENC_FPS"] = "240",
            ["WINDOWSTREAM_NVENC_GOP"] = "600",
            ["WINDOWSTREAM_NVENC_SURFACES"] = "64"
        };

        var settings = EncoderEnvironmentSettings.Load(
            name => variables.GetValueOrDefault(name));

        Assert.Equal(240, settings.FramesPerSecond);
        Assert.Equal(600, settings.GroupOfPicturesLength);
        Assert.Equal(64, settings.SurfaceCount);
    }

    [Fact]
    public void Load_ignores_integer_overrides_above_their_maximums()
    {
        var variables = new Dictionary<string, string?>
        {
            ["WINDOWSTREAM_NVENC_FPS"] = "241",
            ["WINDOWSTREAM_NVENC_GOP"] = "601",
            ["WINDOWSTREAM_NVENC_SURFACES"] = "65"
        };

        var settings = EncoderEnvironmentSettings.Load(
            name => variables.GetValueOrDefault(name));

        Assert.Equal(60, settings.FramesPerSecond);
        Assert.Equal(30, settings.GroupOfPicturesLength);
        Assert.Equal(1, settings.SurfaceCount);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("invalid")]
    public void Load_ignores_invalid_positive_integer_overrides(string value)
    {
        var settings = EncoderEnvironmentSettings.Load(
            name => name switch
            {
                "WINDOWSTREAM_NVENC_FPS" => value,
                "WINDOWSTREAM_NVENC_GOP" => value,
                "WINDOWSTREAM_NVENC_SURFACES" => value,
                _ => null
            });

        Assert.Equal(60, settings.FramesPerSecond);
        Assert.Equal(30, settings.GroupOfPicturesLength);
        Assert.Equal(1, settings.SurfaceCount);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Load_uses_default_tune_when_override_is_blank(string? value)
    {
        var settings = EncoderEnvironmentSettings.Load(
            name => name == "WINDOWSTREAM_NVENC_TUNE" ? value : null);

        Assert.Equal("ull", settings.Tune);
    }

    [Fact]
    public void Load_rejects_null_environment_reader()
    {
        Assert.Throws<ArgumentNullException>(() => EncoderEnvironmentSettings.Load(null!));
    }
}
