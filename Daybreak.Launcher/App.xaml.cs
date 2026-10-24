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
            services.AddSingleton<MainWindow>();
            services.AddTransient<Views.Pages.HomePage>();
            services.AddTransient<Views.Pages.DownloadPage>();
            services.AddTransient<Views.Pages.VersionsPage>();
            services.AddTransient<Views.Pages.ModsPage>();
            services.AddTransient<Views.Pages.SettingsPage>();
            services.AddTransient<Views.Pages.AboutPage>();
        })
        .Build();

    public static T GetService<T>() where T : class => AppHost.Services.GetRequiredService<T>();

    protected override void OnStartup(StartupEventArgs e)
    {
        LogSetup.Initialize();
        ApplicationThemeManager.Apply(ApplicationTheme.Dark);

        AppHost.Start();
        base.OnStartup(e);

        MainWindow = GetService<MainWindow>();
        MainWindow.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
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
