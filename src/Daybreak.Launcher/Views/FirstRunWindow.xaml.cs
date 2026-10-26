using System.IO;
using System.Windows;
using Daybreak.Core.Storage;
using Serilog;
using Wpf.Ui.Controls;

namespace Daybreak.Launcher.Views;

/// <summary>
/// 首次启动引导:三步向导(游戏目录 → 内存/镜像 → 完成)。
/// 只做"写入 LauncherConfig"这件事,不直接操控主窗口,避免与导航逻辑耦合。
/// </summary>
public partial class FirstRunWindow : FluentWindow
{
    private const int TotalSteps = 3;

    private readonly LauncherConfig _config;
    private int _step = 1;

    /// <summary>用户是否走完了引导(跳过也算完成,只是不再弹)。</summary>
    public bool Completed { get; private set; }

    public FirstRunWindow(LauncherConfig config)
    {
        _config = config;
        InitializeComponent();

        var defaultGameDir = config.GameDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), ".minecraft");

        GameDirBox.Text = defaultGameDir;
        MemoryBox.Text = config.MaxMemoryMb.ToString();
        // 0 = BMCLAPI(引导里默认推荐国内加速),1 = 官方源
        MirrorCombo.SelectedIndex = config.Mirror == "official" ? 1 : 0;

        UpdateGameDirHint();
        GameDirBox.TextChanged += (_, _) => UpdateGameDirHint();

        Render();
    }

    private void UpdateGameDirHint()
    {
        var dir = GameDirBox.Text.Trim();
        if (dir.Length == 0)
        {
            GameDirHint.Text = "";
            return;
        }

        try
        {
            var looksLikeMinecraft = Directory.Exists(Path.Combine(dir, "versions")) ||
                                     File.Exists(Path.Combine(dir, "launcher_profiles.json"));

            GameDirHint.Text = Directory.Exists(dir)
                ? looksLikeMinecraft
                    ? "检测到已有 Minecraft 目录(versions 存在),将复用其中的版本与存档。"
                    : "目录已存在,但没有发现 versions 文件夹;继续安装游戏会在此基础上创建。"
                : "目录尚不存在,将在安装游戏时自动创建。";
        }
        catch (Exception ex)
        {
            GameDirHint.Text = "路径校验失败:" + ex.Message;
        }
    }

    private void Render()
    {
        Step1Panel.Visibility = _step == 1 ? Visibility.Visible : Visibility.Collapsed;
        Step2Panel.Visibility = _step == 2 ? Visibility.Visible : Visibility.Collapsed;
        Step3Panel.Visibility = _step == 3 ? Visibility.Visible : Visibility.Collapsed;

        TitleText.Text = _step switch
        {
            1 => "欢迎使用破晓启动器",
            2 => "性能与下载设置",
            _ => "一切就绪",
        };

        StepIndicator.Text = $"第 {_step} / {TotalSteps} 步";
        BackButton.Visibility = _step > 1 ? Visibility.Visible : Visibility.Collapsed;
        SkipButton.Visibility = _step < TotalSteps ? Visibility.Visible : Visibility.Collapsed;
        NextButton.Content = _step < TotalSteps ? "下一步" : "开始使用";
    }

    private void OnBack(object sender, RoutedEventArgs e)
    {
        if (_step > 1)
        {
            _step--;
            Render();
        }
    }

    private void OnNext(object sender, RoutedEventArgs e)
    {
        // 每一步离开前先落盘该步的设置,用户中途关窗也不会白填
        if (_step == 1)
        {
            var dir = GameDirBox.Text.Trim();
            if (dir.Length == 0)
            {
                GameDirHint.Text = "游戏目录不能为空。";
                return;
            }

            try
            {
                Directory.CreateDirectory(dir);
            }
            catch (Exception ex)
            {
                GameDirHint.Text = "无法创建该目录:" + ex.Message;
                return;
            }

            _config.GameDirectory = dir;
            _config.Save();
        }
        else if (_step == 2)
        {
            if (!int.TryParse(MemoryBox.Text.Trim(), out var memory) || memory is < 1024 or > 32_768)
            {
                MemoryHint.Text = "内存需在 1024 ~ 32768 MB 之间。";
                return;
            }

            _config.MaxMemoryMb = memory;
            _config.Mirror = MirrorCombo.SelectedIndex == 1 ? "official" : "bmclapi";
            _config.Save();
        }

        if (_step < TotalSteps)
        {
            _step++;
            Render();
            return;
        }

        Finish();
    }

    private void OnSkip(object sender, RoutedEventArgs e) => Finish();

    private void Finish()
    {
        // 跳过时也要保存已有改动(第一步可能已写入目录),并标记引导已完成
        _config.HasCompletedFirstRun = true;
        _config.Save();
        Completed = true;

        Log.Information("首次启动引导完成:目录={Dir} 内存={Mem}MB 镜像={Mirror}",
            _config.GameDirectory ?? "(默认)", _config.MaxMemoryMb, _config.Mirror);

        Close();
    }
}