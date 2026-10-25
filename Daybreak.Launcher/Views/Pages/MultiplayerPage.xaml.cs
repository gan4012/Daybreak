using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Daybreak.Core.Server;
using Daybreak.Launcher.ViewModels;
using Wpf.Ui.Controls;

namespace Daybreak.Launcher.Views.Pages;

/// <summary>
/// 联机页:我的服务端 / 服务器列表 / 局域网。
///
/// 列表项里的按钮需要拿到**该项自身**作为参数,而项目不使用 Converter,
/// 所以这些操作走 code-behind 从 sender 的 DataContext 取对象,再转交命令。
/// 业务逻辑仍然全在 ViewModel 里,这里只做参数传递。
/// </summary>
public partial class MultiplayerPage : Page
{
    private MultiplayerViewModel ViewModel => (MultiplayerViewModel)DataContext;

    public MultiplayerPage()
    {
        InitializeComponent();
        DataContext = App.GetService<MultiplayerViewModel>();
    }

    // ===== Tab =====

    private void OnTabChanged(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded)
            return;

        ViewModel.SelectedTabIndex = TabServers.IsChecked == true ? 0
            : TabList.IsChecked == true ? 1
            : 2;
    }

    // ===== 我的服务端 =====

    private void OnOpenCreateServer(object sender, RoutedEventArgs e)
    {
        var dialog = new CreateServerDialog
        {
            Owner = Window.GetWindow(this),
        };

        if (dialog.ShowDialog() == true && dialog.Created)
            ViewModel.RefreshServersCommand.Execute(null);
    }

    private void OnOpenServerFolder(object sender, RoutedEventArgs e)
    {
        var item = (sender as FrameworkElement)?.DataContext as ServerItemViewModel;
        ViewModel.OpenServerFolderCommand.Execute(item);
    }

    private void OnDeleteServer(object sender, RoutedEventArgs e)
    {
        var item = (sender as FrameworkElement)?.DataContext as ServerItemViewModel;
        ViewModel.DeleteServerCommand.Execute(item);
    }

    private void OnAcceptEula(object sender, RoutedEventArgs e)
    {
        var item = (sender as FrameworkElement)?.DataContext as ServerItemViewModel;
        ViewModel.AcceptEulaCommand.Execute(item);
    }

    /// <summary>控制台回车即发送(开服时敲命令的常规操作习惯)。</summary>
    private void OnConsoleKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
            return;

        ViewModel.SendConsoleCommandCommand.Execute(null);
        e.Handled = true;
    }

    // ===== 服务器列表 =====

    private void OnJoinSavedServer(object sender, RoutedEventArgs e)
    {
        var item = (sender as FrameworkElement)?.DataContext as SavedServerItemViewModel;
        ViewModel.JoinServerCommand.Execute(item);
    }

    private void OnToggleFavorite(object sender, RoutedEventArgs e)
    {
        var item = (sender as FrameworkElement)?.DataContext as SavedServerItemViewModel;
        ViewModel.ToggleFavoriteCommand.Execute(item);
    }

    private void OnRemoveSavedServer(object sender, RoutedEventArgs e)
    {
        var item = (sender as FrameworkElement)?.DataContext as SavedServerItemViewModel;
        ViewModel.RemoveSavedServerCommand.Execute(item);
    }

    // ===== 局域网 =====

    private void OnJoinLanWorld(object sender, RoutedEventArgs e)
    {
        var world = (sender as FrameworkElement)?.DataContext as LanWorld;
        ViewModel.JoinLanWorldCommand.Execute(world);
    }

    /// <summary>页面卸载时停掉后台监听,避免一直占着 UDP 4445。</summary>
    private void OnUnloaded(object sender, RoutedEventArgs e) => ViewModel.Cleanup();
}