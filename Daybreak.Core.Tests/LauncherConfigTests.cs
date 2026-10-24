using Daybreak.Core.Storage;
using Xunit;

namespace Daybreak.Core.Tests;

public class LauncherConfigTests
{
    [Fact]
    public void Load_WhenNoFile_ReturnsDefaults()
    {
        var config = LauncherConfig.Load();
        Assert.Equal("zh-CN", config.Language);
        Assert.Equal("Dark", config.Theme);
        Assert.Equal("official", config.Mirror);
        Assert.Equal(4096, config.MaxMemoryMb);
    }

    [Fact]
    public void SaveThenLoad_RoundTripsAllFields()
    {
        var original = new LauncherConfig
        {
            Language = "en-US",
            Theme = "Light",
            GameDirectory = @"D:\mc\game",
            JavaDirectory = @"D:\mc\java",
            MaxMemoryMb = 8192,
            Mirror = "bmclapi",
        };
        original.Save();

        var loaded = LauncherConfig.Load();
        Assert.Equal(original.Language, loaded.Language);
        Assert.Equal(original.Theme, loaded.Theme);
        Assert.Equal(original.GameDirectory, loaded.GameDirectory);
        Assert.Equal(original.JavaDirectory, loaded.JavaDirectory);
        Assert.Equal(original.MaxMemoryMb, loaded.MaxMemoryMb);
        Assert.Equal(original.Mirror, loaded.Mirror);
    }
}
