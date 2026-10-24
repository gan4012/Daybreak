using System.IO;
using Serilog;

namespace Daybreak.Launcher.Services;

public static class LogSetup
{
    public static void Initialize()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Daybreak", "logs");
        Directory.CreateDirectory(dir);

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.File(
                Path.Combine(dir, "daybreak-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 14,
                shared: true)
            .CreateLogger();

        Log.Information("Daybreak 启动,日志目录 {LogDirectory}", dir);
    }
}
