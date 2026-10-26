using CommunityToolkit.Mvvm.ComponentModel;
using Daybreak.Core.Server;

namespace Daybreak.Launcher.ViewModels;

/// <summary>
/// 服务器列表项的 UI 包装。
///
/// 为什么不直接用 Core 的 SavedServer:它是纯数据模型(无 INotifyPropertyChanged),
/// 查询到在线状态后直接改它的属性,界面不会刷新——
/// 实测表现为「状态栏说查询成功、列表却还是灰点和 —」。
/// 所以这里用可观察包装类暴露所有会变的字段。
/// </summary>
public partial class SavedServerItemViewModel : ObservableObject
{
    /// <summary>被包装的持久化数据。</summary>
    public SavedServer Server { get; }

    public SavedServerItemViewModel(SavedServer server) => Server = server;

    public string DisplayName => Server.DisplayName;
    public string Address => Server.Address;
    public string Host => Server.Host;
    public int Port => Server.Port;

    [ObservableProperty]
    private bool _isOnline;

    [ObservableProperty]
    private string _playerDisplay = "—";

    [ObservableProperty]
    private string _latencyDisplay = "—";

    [ObservableProperty]
    private string _motd = "";

    [ObservableProperty]
    private string _errorText = "";

    /// <summary>是否收藏(用于按钮文案切换)。</summary>
    [ObservableProperty]
    private bool _favorite;

    /// <summary>是否正在查询(避免重复点击)。</summary>
    [ObservableProperty]
    private bool _isRefreshing;

    /// <summary>收藏按钮文案。</summary>
    public string FavoriteLabel => Favorite ? "已收藏" : "收藏";

    partial void OnFavoriteChanged(bool value) => OnPropertyChanged(nameof(FavoriteLabel));

    /// <summary>把查询结果同步到界面属性。</summary>
    public void ApplyStatus(ServerStatus? status)
    {
        Server.Status = status;

        if (status is null)
        {
            IsOnline = false;
            PlayerDisplay = "—";
            LatencyDisplay = "—";
            Motd = "";
            ErrorText = "";
            return;
        }

        IsOnline = status.Online;
        PlayerDisplay = status.PlayerDisplay;
        LatencyDisplay = status.LatencyMs > 0 ? $"{status.LatencyMs} ms" : "—";
        Motd = status.Motd ?? "";
        ErrorText = status.Error ?? "";
    }

    /// <summary>添加/加载时用已有状态初始化(不触发额外查询)。</summary>
    public void InitializeFromServer()
    {
        Favorite = Server.Favorite;
        ApplyStatus(Server.Status);
    }
}