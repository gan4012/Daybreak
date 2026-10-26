using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CmlLib.Core.Auth;
using Daybreak.Core.Auth;
using Daybreak.Core.Game;
using Daybreak.Core.Storage;
using Serilog;

namespace Daybreak.Launcher.ViewModels;

/// <summary>启动所需的认证上下文。</summary>
public record LaunchAuth(MSession Session, string? JavaAgentJar, string? AuthServerUrl);

/// <summary>账号卡片的全部状态与命令:离线 / 微软正版(设备码)/ Yggdrasil 外置三种登录与切换。</summary>
public partial class AuthViewModel : ObservableObject
{
    private LauncherConfig _config = LauncherConfig.Load();
    private readonly AccountStore _store = AccountStore.Load();
    private MicrosoftAuthenticator _microsoft;
    private readonly YggdrasilClient _yggdrasil = new();
    private bool _syncingSelection;

    public ObservableCollection<StoredAccount> Accounts { get; } = [];

    /// <summary>当前微软登录是否使用开发期备用 ClientId(提醒用户替换)。</summary>
    public bool IsUsingFallbackClientId => _microsoft.IsUsingFallbackClientId;

    /// <summary>0 离线 / 1 微软正版 / 2 外置登录。</summary>
    [ObservableProperty]
    private int _selectedModeIndex;

    [ObservableProperty]
    private string _playerName;

    [ObservableProperty]
    private string _externalServerUrl = "";

    [ObservableProperty]
    private string _externalUsername = "";

    [ObservableProperty]
    private string _externalPassword = "";

    [ObservableProperty]
    private bool _isAuthBusy;

    [ObservableProperty]
    private string _authStatusText = "";

    [ObservableProperty]
    private bool _deviceCodeVisible;

    [ObservableProperty]
    private string _deviceCodeText = "";

    [ObservableProperty]
    private string _deviceCodeUrl = "";

    [ObservableProperty]
    private StoredAccount? _selectedAccount;

    [ObservableProperty]
    private string _activeAccountDisplay = "未登录";

    /// <summary>当前活动账号的头像 URL(32x32,使用 minotar.net 免费 API)。</summary>
    [ObservableProperty]
    private string? _avatarUrl;

    public AuthViewModel()
    {
        _microsoft = new MicrosoftAuthenticator(clientId: _config.MicrosoftClientId);
        PlayerName = _config.PlayerName;
        LoadAccounts();
    }

    /// <summary>从 LauncherConfig 重新加载 ClientId(由设置页调用,热更新微软登录器)。</summary>
    public void LoadFromConfig()
    {
        _config = LauncherConfig.Load();
        _microsoft = new MicrosoftAuthenticator(clientId: _config.MicrosoftClientId);
        OnPropertyChanged(nameof(IsUsingFallbackClientId));
    }

    partial void OnPlayerNameChanged(string value)
    {
        _config.PlayerName = value;
        _config.Save();
    }

    partial void OnSelectedAccountChanged(StoredAccount? value)
    {
        if (_syncingSelection || value is null)
            return;
        _store.SetActive(value.Id);
        UpdateActiveDisplay();
        Log.Information("切换活动账号:{Id}", value.Id);
    }

    private void LoadAccounts()
    {
        _syncingSelection = true;
        Accounts.Clear();
        foreach (var account in _store.Accounts)
            Accounts.Add(account);
        SelectedAccount = _store.GetActive();
        _syncingSelection = false;
        UpdateActiveDisplay();
    }

    private void UpdateActiveDisplay()
    {
        var active = _store.GetActive();
        ActiveAccountDisplay = active?.Type switch
        {
            AccountType.Microsoft => $"微软正版 · {active.Name}",
            AccountType.External => $"外置 · {active.Name} @ {active.ServerName}",
            AccountType.Offline => $"离线 · {active.Name}",
            _ => "未登录",
        };

        // 生成头像 URL:UUID > 玩家名
        AvatarUrl = active is not null && !string.IsNullOrEmpty(active.Name)
            ? GetAvatarUrl(active.Name, active.Uuid)
            : null;
    }

    /// <summary>获取 minotar.net 头像 URL。</summary>
    private static string? GetAvatarUrl(string name, string? uuid)
    {
        if (string.IsNullOrWhiteSpace(name))
            return null;
        // 有 UUID 用 UUID(更精确),否则用玩家名
        var id = !string.IsNullOrWhiteSpace(uuid) ? uuid : name;
        return $"https://minotar.net/helm/{Uri.EscapeDataString(id)}/32.png";
    }

    private bool CanRunAuth() => !IsAuthBusy;

    [RelayCommand(CanExecute = nameof(CanRunAuth))]
    private void AddOfflineAccount()
    {
        var name = PlayerName.Trim();
        if (string.IsNullOrEmpty(name))
        {
            AuthStatusText = "请先填写玩家名";
            return;
        }

        _store.UpsertActive(new StoredAccount($"offline:{name}", AccountType.Offline, name));
        LoadAccounts();
        AuthStatusText = $"离线账号已保存:{name}";
        Log.Information("保存离线账号 {Name}", name);
    }

    [RelayCommand(CanExecute = nameof(CanRunAuth))]
    private async Task MicrosoftLoginAsync()
    {
        IsAuthBusy = true;
        DeviceCodeVisible = false;
        AuthStatusText = "正在向微软请求设备码…";
        IProgress<DeviceCodeInfo> uiCode = new Progress<DeviceCodeInfo>(info =>
        {
            DeviceCodeText = info.UserCode;
            DeviceCodeUrl = info.VerificationUrl;
            DeviceCodeVisible = true;
            AuthStatusText = "请在浏览器打开下方链接并输入验证码完成授权";
        });

        try
        {
            var session = await _microsoft.LoginWithDeviceCodeAsync(info => uiCode.Report(info));
            var account = new StoredAccount(
                $"msauth:{session.UUID}", AccountType.Microsoft,
                session.Username ?? "微软账号", session.UUID);
            _store.UpsertActive(account);
            LoadAccounts();
            DeviceCodeVisible = false;
            AuthStatusText = $"微软正版登录成功:{session.Username}";
            Log.Information("微软正版登录成功:{Username}", session.Username);
        }
        catch (Exception ex)
        {
            DeviceCodeVisible = false;
            AuthStatusText = "微软登录失败:" + ex.Message;
            Log.Error(ex, "微软登录失败");
        }
        finally
        {
            IsAuthBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanRunAuth))]
    private async Task ExternalLoginAsync()
    {
        if (string.IsNullOrWhiteSpace(ExternalServerUrl) ||
            string.IsNullOrWhiteSpace(ExternalUsername) ||
            string.IsNullOrWhiteSpace(ExternalPassword))
        {
            AuthStatusText = "请填写服务器地址、账号与密码";
            return;
        }

        IsAuthBusy = true;
        AuthStatusText = "正在发现外置认证服务器…";
        try
        {
            var server = await _yggdrasil.DiscoverAsync(ExternalServerUrl);
            AuthStatusText = $"已发现服务器:{server.ServerName},正在登录…";
            var session = await _yggdrasil.AuthenticateAsync(
                server.ApiRoot, ExternalUsername.Trim(), ExternalPassword, _store.ClientToken!);

            _store.UpsertActive(new StoredAccount(
                $"external:{server.ApiRoot}:{session.Uuid}", AccountType.External,
                session.Name, session.Uuid, server.ApiRoot, server.ServerName, session.AccessToken));
            LoadAccounts();
            ExternalPassword = "";
            AuthStatusText = $"外置登录成功:{session.Name} @ {server.ServerName}";
            Log.Information("外置登录成功:{Name} @ {Server}", session.Name, server.ServerName);
        }
        catch (Exception ex)
        {
            AuthStatusText = "外置登录失败:" + ex.Message;
            Log.Warning("外置登录失败:{Message}", ex.Message);
        }
        finally
        {
            IsAuthBusy = false;
        }
    }

    [RelayCommand]
    private void RemoveAccount(StoredAccount? account)
    {
        if (account is null)
            return;
        _store.Remove(account.Id);
        LoadAccounts();
        AuthStatusText = $"已移除账号:{account.Name}";
    }

    [RelayCommand]
    private void OpenDeviceCodeUrl()
    {
        if (!string.IsNullOrEmpty(DeviceCodeUrl))
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = DeviceCodeUrl,
                UseShellExecute = true,
            });
    }

    /// <summary>为启动准备认证上下文(含外置令牌续期与 authlib-injector 就位)。</summary>
    public async Task<LaunchAuth> GetLaunchAuthAsync(Action<string> statusCallback)
    {
        var active = _store.GetActive();
        if (active is null || active.Type == AccountType.Offline)
        {
            var offlineName = string.IsNullOrWhiteSpace(active?.Name) ? PlayerName : active.Name;
            return new LaunchAuth(MSession.CreateOfflineSession(offlineName), null, null);
        }

        if (active.Type == AccountType.Microsoft)
        {
            statusCallback("正在刷新微软正版令牌…");
            try
            {
                var session = await _microsoft.TrySilentLoginAsync();
                return new LaunchAuth(session, null, null);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    "微软正版令牌刷新失败,请重新登录(设备码授权)", ex);
            }
        }

        // 外置登录:validate →(失败)refresh,并确保 authlib-injector.jar 就位
        var client = new YggdrasilClient();
        var apiRoot = active.ServerUrl!;
        YggdrasilSession yggSession;

        if (await client.ValidateAsync(apiRoot, active.AccessToken ?? "", _store.ClientToken!))
        {
            yggSession = new YggdrasilSession(
                active.AccessToken!, active.Uuid!, active.Name, _store.ClientToken!);
        }
        else
        {
            statusCallback("外置登录令牌已过期,正在续期…");
            yggSession = await client.RefreshAsync(
                apiRoot, active.AccessToken ?? "", _store.ClientToken!, active.Uuid!, active.Name);
            _store.UpsertActive(active with { AccessToken = yggSession.AccessToken });
        }

        statusCallback("正在准备 authlib-injector…");
        var jar = await new AuthlibInjectorDownloader()
            .EnsureJarAsync(AccountStore.DefaultDirectory, _config.Mirror);

        var mSession = new MSession
        {
            AccessToken = yggSession.AccessToken,
            UUID = yggSession.Uuid,
            Username = yggSession.Name,
            UserType = "msa",
        };
        return new LaunchAuth(mSession, jar, apiRoot);
    }
}
