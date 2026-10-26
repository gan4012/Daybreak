using System.Reflection;
using System.Diagnostics;
using System.Windows.Controls;

namespace Daybreak.Launcher.Views.Pages;

public partial class AboutPage : Page
{
    public AboutPage()
    {
        InitializeComponent();

        // 从程序集版本获取版本号
        var asm = Assembly.GetExecutingAssembly();
        var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>();
        var version = info?.InformationalVersion ?? asm.GetName().Version?.ToString() ?? "0.1.0";

        // 设置 XAML 中 TextBlock 的文本
        VersionText.Text = $"版本 {version}";
    }

    private void OnOpenGitHub(object sender, System.Windows.RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "https://github.com/zcode-daybreak/Daybreak/issues",
                UseShellExecute = true,
            });
        }
        catch
        {
            // 浏览器打开失败时忽略
        }
    }
}
