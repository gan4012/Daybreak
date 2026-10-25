using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;
using Daybreak.Core.Storage;
using Daybreak.Launcher.ViewModels;
using Wpf.Ui.Controls;

namespace Daybreak.Launcher;

public partial class MainWindow : FluentWindow
{
    private bool _syncingSelection;
    private bool _restoringState = true; // 防止首次加载时保存默认值

    public MainWindow(MainWindowViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;

        Loaded += OnLoaded;
        Closing += OnClosing;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        MainNav.SelectedIndex = 0;

        // 恢复窗口状态
        var config = LauncherConfig.Load();
        if (config.WindowLeft.HasValue && config.WindowTop.HasValue &&
            config.WindowWidth.HasValue && config.WindowHeight.HasValue)
        {
            Left = Math.Max(SystemParameters.VirtualScreenLeft, config.WindowLeft.Value);
            Top = Math.Max(0, config.WindowTop.Value);
            Width = Math.Min(SystemParameters.VirtualScreenWidth - 100, Math.Max(800, config.WindowWidth.Value));
            Height = Math.Min(SystemParameters.VirtualScreenHeight - 100, Math.Max(500, config.WindowHeight.Value));
            WindowState = config.WindowMaximized ? WindowState.Maximized : WindowState.Normal;
        }

        _restoringState = false;
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_restoringState)
            return;

        var config = LauncherConfig.Load();
        config.WindowLeft = Left;
        config.WindowTop = Top;
        config.WindowWidth = Width;
        config.WindowHeight = Height;
        config.WindowMaximized = WindowState == WindowState.Maximized;
        config.Save();
    }

    private void OnMainNavSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (MainNav.SelectedItem is not NavItemViewModel item)
            return;

        _syncingSelection = true;
        FooterNav.SelectedIndex = -1;
        _syncingSelection = false;

        ContentHolder.Content = App.GetService(item.PageType);
    }

    private void OnFooterNavSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingSelection)
            return;

        if (FooterNav.SelectedItem is not NavItemViewModel item)
            return;

        _syncingSelection = true;
        MainNav.SelectedIndex = -1;
        _syncingSelection = false;

        ContentHolder.Content = App.GetService(item.PageType);
    }

    private void OnNavToggle(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel viewModel)
            return;

        viewModel.IsNavCollapsed = !viewModel.IsNavCollapsed;

        // 先写本地值再播动画:FillBehavior.Stop 会在播完回落到本地宽度
        var target = viewModel.IsNavCollapsed ? 64.0 : 240.0;
        SidebarPanel.Width = target;
        var animation = new DoubleAnimation(target, TimeSpan.FromMilliseconds(180))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut },
        };
        SidebarPanel.BeginAnimation(WidthProperty, animation);

        NavToggleIcon.Symbol = viewModel.IsNavCollapsed
            ? SymbolRegular.PanelLeftExpand24
            : SymbolRegular.PanelLeftContract24;
    }
}
