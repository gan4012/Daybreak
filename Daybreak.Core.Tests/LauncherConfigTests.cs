using Daybreak.Core.Storage;
using Xunit;

namespace Daybreak.Core.Tests;

public class LauncherConfigTests : IDisposable
{
    // 每个测试独立临时目录,避免与真实 %APPDATA% 及其他测试互相污染
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "daybreak-tests", Guid.NewGuid().ToString("N"));

    public LauncherConfigTests() => LauncherConfig.DirectoryOverrideForTests = _tempDir;

    public void Dispose() => LauncherConfig.DirectoryOverrideForTests = null;

    [Fact]
    public void Load_WhenNoFile_ReturnsDefaults()
    {
        var config = LauncherConfig.Load();
        Assert.Equal("zh-CN", config.Language);
        Assert.Equal("Dark", config.ThemeMode);
        Assert.Equal("Acrylic", config.Backdrop);
        Assert.Equal("official", config.Mirror);
        Assert.Equal(4096, config.MaxMemoryMb);
    }

    [Fact]
    public void Load_WhenFileCorrupted_ReturnsDefaults()
    {
        Directory.CreateDirectory(_tempDir);
        File.WriteAllText(LauncherConfig.GetFilePath(), "{ not valid json !!!");

        var config = LauncherConfig.Load();
        Assert.Equal("Dark", config.ThemeMode);
        Assert.Equal("official", config.Mirror);
    }

    [Fact]
    public void SaveThenLoad_RoundTripsAllFields()
    {
        var original = new LauncherConfig
        {
            Language = "en-US",
            ThemeMode = "System",
            Backdrop = "Mica",
            GameDirectory = @"D:\mc\game",
            JavaDirectory = @"D:\mc\java",
            MaxMemoryMb = 8192,
            Mirror = "bmclapi",
        };
        original.Save();

        var loaded = LauncherConfig.Load();
        Assert.Equal(original.Language, loaded.Language);
        Assert.Equal(original.ThemeMode, loaded.ThemeMode);
        Assert.Equal(original.Backdrop, loaded.Backdrop);
        Assert.Equal(original.GameDirectory, loaded.GameDirectory);
        Assert.Equal(original.JavaDirectory, loaded.JavaDirectory);
        Assert.Equal(original.MaxMemoryMb, loaded.MaxMemoryMb);
        Assert.Equal(original.Mirror, loaded.Mirror);
    }
}
