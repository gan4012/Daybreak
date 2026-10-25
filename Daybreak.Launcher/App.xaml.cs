using System.Windows;
using Daybreak.Launcher.Services;
using Daybreak.Launcher.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;
using Wpf.Ui.Appearance;

namespace Daybreak.Launcher;

public partial class App : Application
{
    private static readonly IHost AppHost = Host
        .CreateDefaultBuilder()
        .ConfigureServices((_, services) =>
        {
            services.AddSingleton<MainWindowViewModel>();
            services.AddSingleton<SettingsViewModel>();
            services.AddSingleton<AuthViewModel>();
            services.AddTransient<HomeViewModel>();

            // DownloadViewModel / MultiplayerViewModel 必须是单例:
            // 它们持有"跨页面存活的任务"(下载进度、正在运行的服务端进程与日志)。
            // 注册成 Transient 时,切走再切回来会拿到一个全新 VM——
            // 表现为「服务端显示已停止(其实还在跑)、再点启动报端口被占用」
            // 以及「下载切个页面就'停了'」。
            services.AddSingleton<DownloadViewModel>();
            services.AddSingleton<MultiplayerViewModel>();

            services.AddTransient<VersionsViewModel>();
            services.AddTransient<ModsViewModel>();
            services.AddSingleton<MainWindow>();
            services.AddTransient<Views.Pages.HomePage>();
            services.AddTransient<Views.Pages.DownloadPage>();
            services.AddTransient<Views.Pages.VersionsPage>();
            services.AddTransient<Views.Pages.ModsPage>();
            services.AddTransient<Views.Pages.MultiplayerPage>();
            services.AddTransient<Views.Pages.SettingsPage>();
            services.AddTransient<Views.Pages.AboutPage>();
        })
        .Build();

    public static T GetService<T>() where T : class => AppHost.Services.GetRequiredService<T>();

    public static object GetService(Type serviceType) => AppHost.Services.GetRequiredService(serviceType);

    protected override void OnStartup(StartupEventArgs e)
    {
        LogSetup.Initialize();

        // 记录提权状态:联机功能依赖管理员权限，出问题时这一行是最关键的排查依据
        Log.Information("启动器启动 — 管理员权限: {Elevated},联机组件: {Component}",
            Services.ElevationService.IsElevated ? "是" : "否",
            Core.Net.EasyTierManager.IsInstalled ? "已就位" : "缺失");

        var config = Core.Storage.LauncherConfig.Load();
        Services.ThemeService.ApplyThemeMode(config.ThemeMode, config.Backdrop);

        // 首次启动引导:只在"真的第一次用"(连配置文件都不存在)时弹出。
        // 已有配置的老用户升级上来时静默标记为已完成,不打扰。
        var isFirstRun = !System.IO.File.Exists(Core.Storage.LauncherConfig.GetFilePath());
        if (isFirstRun && !config.HasCompletedFirstRun)
        {
            // 关键:WPF 默认 ShutdownMode=OnLastWindowClose。
            // 模态引导窗是引导期间唯一的窗口,关闭它会让应用在 MainWindow.Show() 之前直接退出
            // (表现为"走完引导启动器就消失")。所以引导期间必须先切到显式关闭。
            ShutdownMode = ShutdownMode.OnExplicitShutdown;

            var wizard = new Views.FirstRunWindow(config);
            wizard.ShowDialog();   // 模态:引导期间不显示主窗口

            config = Core.Storage.LauncherConfig.Load();   // 引导里可能改了目录/内存
            Services.ThemeService.ApplyThemeMode(config.ThemeMode, config.Backdrop);

            // 回到"关掉主窗口即退出"的正常行为
            ShutdownMode = ShutdownMode.OnMainWindowClose;
        }
        else if (!config.HasCompletedFirstRun)
        {
            config.HasCompletedFirstRun = true;
            config.Save();
        }

        AppHost.Start();
        base.OnStartup(e);

        MainWindow = GetService<MainWindow>();
        if (config.ThemeMode == "System")
            Wpf.Ui.Appearance.SystemThemeWatcher.Watch(MainWindow);
        MainWindow.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // 单例 VM 托管的服务端与局域网监听必须在这里收尾,
        // 否则启动器退出后会留下孤儿 Java 进程占着端口
        try
        {
            GetService<ViewModels.MultiplayerViewModel>()
                .ShutdownAsync()
                .Wait(TimeSpan.FromSeconds(15));
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "退出时清理联机资源失败");
        }

        AppHost.StopAsync(TimeSpan.FromSeconds(3)).GetAwaiter().GetResult();
        Log.CloseAndFlush();
        base.OnExit(e);
    }

    private void OnDispatcherUnhandledException(object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs args)
    {
        Log.Error(args.Exception, "未处理的 UI 异常");
        args.Handled = true;
    }
}
