namespace Daybreak.Updater;

// M5 里程碑实现:独立更新器,负责差量下载与替换主程序文件。
// 设计约束:必须是独立小 exe,不依赖主程序的任何程序集,便于自我覆盖。
internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        Console.WriteLine("Daybreak Updater 占位入口 —— 更新流程将在 M5 实现。");
        Console.WriteLine(string.Join(' ', args));
    }
}
