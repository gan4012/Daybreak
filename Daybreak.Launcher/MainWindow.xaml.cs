using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;
using Daybreak.Launcher.ViewModels;
using Wpf.Ui.Controls;

namespace Daybreak.Launcher;

public partial class MainWindow : FluentWindow
{
    private bool _syncingSelection;

    public MainWindow(MainWindowViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        Loaded += (_, _) => MainNav.SelectedIndex = 0;
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
