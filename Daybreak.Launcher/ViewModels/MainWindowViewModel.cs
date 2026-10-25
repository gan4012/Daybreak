using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Wpf.Ui.Controls;

namespace Daybreak.Launcher.ViewModels;

/// <summary>侧边栏导航项:标签、图标与目标页面类型(路由不变,仅换导航壳)。</summary>
public class NavItemViewModel
{
    public NavItemViewModel(string label, SymbolRegular icon, Type pageType, bool isFooter = false)
    {
        Label = label;
        Icon = icon;
        PageType = pageType;
        IsFooter = isFooter;
    }

    public string Label { get; }

    public SymbolRegular Icon { get; }

    public Type PageType { get; }

    public bool IsFooter { get; }
}

public partial class MainWindowViewModel : ObservableObject
{
    [ObservableProperty]
    private string _appTitle = "Daybreak 破晓启动器";

    [ObservableProperty]
    private bool _isNavCollapsed;

    /// <summary>主导航(面板上部)</summary>
    public IReadOnlyList<NavItemViewModel> NavItemsMain { get; } =
    [
        new("首页", SymbolRegular.Home24, typeof(Views.Pages.HomePage)),
        new("下载游戏", SymbolRegular.ArrowDownload24, typeof(Views.Pages.DownloadPage)),
        new("版本管理", SymbolRegular.Layer24, typeof(Views.Pages.VersionsPage)),
        new("资源市场", SymbolRegular.PuzzlePiece24, typeof(Views.Pages.ModsPage)),
        new("联机", SymbolRegular.Globe24, typeof(Views.Pages.MultiplayerPage)),
    ];

    /// <summary>底部导航(分隔线下方)</summary>
    public IReadOnlyList<NavItemViewModel> NavItemsFooter { get; } =
    [
        new("设置", SymbolRegular.Settings24, typeof(Views.Pages.SettingsPage), isFooter: true),
        new("关于", SymbolRegular.Info24, typeof(Views.Pages.AboutPage), isFooter: true),
    ];

    [RelayCommand]
    private void ToggleNav() => IsNavCollapsed = !IsNavCollapsed;

    /// <summary>
    /// 下载页的 VM(单例)。主窗口订阅它的"后台安装中"状态,
    /// 这样用户切到任何页面都能看到"下载还在跑",不会以为被中断了。
    /// </summary>
    public DownloadViewModel Download { get; } = App.GetService<DownloadViewModel>();
}
