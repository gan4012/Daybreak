using CmlLib.Core.Auth;
using CmlLib.Core.Auth.Microsoft;
using CmlLib.Core.Auth.Microsoft.Sessions;
using Microsoft.Identity.Client;
using XboxAuthNet.Game.Accounts;
using XboxAuthNet.Game.Msal;
using XboxAuthNet.Game.SessionStorages;

namespace Daybreak.Core.Auth;

/// <summary>设备码回调参数:用户需要在浏览器里输入的验证码与打开的地址。</summary>
public record DeviceCodeInfo(string UserCode, string VerificationUrl, DateTime ExpiresOn);

/// <summary>
/// 微软正版登录门面:MSAL 设备码流程 + Xbox Live/XSTS + Minecraft 认证,
/// 多账号与令牌缓存由 CmlLib JELoginHandler(AccountManager + MSAL cache)持久化。
/// </summary>
public class MicrosoftAuthenticator
{
    /// <summary>
    /// Azure 应用 ClientId。设备码流程要求该应用开启公共客户端流。
    /// TODO 发布前注册自主品牌应用并替换;开发期暂用 CmlLib 示例应用的 ClientId。
    /// </summary>
    public const string DefaultClientId = "499c8d36-be2a-4231-9ebd-ef291b7bb64c";

    private readonly string _clientId;
    private readonly string _accountStorePath;

    public MicrosoftAuthenticator(string? accountStorePath = null, string? clientId = null)
    {
        _accountStorePath = accountStorePath
            ?? Path.Combine(AccountStore.DefaultDirectory, "msauth-accounts.json");
        _clientId = string.IsNullOrWhiteSpace(clientId) ? DefaultClientId : clientId;
    }

    private JELoginHandler BuildLoginHandler() =>
        new JELoginHandlerBuilder()
            .WithAccountManager(_accountStorePath)
            .Build();

    private async Task<IPublicClientApplication> BuildMsalAppAsync()
    {
        var settings = new MsalCacheSettings
        {
            // 令牌缓存放在启动器自己的目录,避免污染全局
            CacheDir = AccountStore.DefaultDirectory,
            CacheFileName = "msal-cache.txt",
        };
        return await MsalClientHelper.BuildApplicationWithCache(_clientId, settings);
    }

    /// <summary>设备码登录:回调展示验证码,等待用户在浏览器完成授权。</summary>
    public async Task<MSession> LoginWithDeviceCodeAsync(Action<DeviceCodeInfo> onDeviceCode)
    {
        var loginHandler = BuildLoginHandler();
        var app = await BuildMsalAppAsync();
        var authenticator = loginHandler.CreateAuthenticatorWithNewAccount();
        authenticator.AddMsalOAuth(app, msal => msal.DeviceCode(code =>
        {
            var info = new DeviceCodeInfo(
                code.UserCode,
                code.VerificationUrl,
                code.ExpiresOn.LocalDateTime);
            onDeviceCode(info);
            return Task.CompletedTask;
        }));
        authenticator.AddXboxAuthForJE(xbox => xbox.Basic());
        authenticator.AddForceJEAuthenticator();
        return await authenticator.ExecuteForLauncherAsync();
    }

    /// <summary>静默续期:用缓存的 MSAL 令牌走完整链路,无需用户交互。</summary>
    public async Task<MSession> TrySilentLoginAsync()
    {
        var loginHandler = BuildLoginHandler();
        var app = await BuildMsalAppAsync();
        var authenticator = loginHandler.CreateAuthenticatorWithNewAccount();
        authenticator.AddMsalOAuth(app, msal => msal.Silent());
        authenticator.AddXboxAuthForJE(xbox => xbox.Basic());
        authenticator.AddJEAuthenticator();
        return await authenticator.ExecuteForLauncherAsync();
    }

    /// <summary>列出登录管理器里的微软账号(展示用)。</summary>
    public IReadOnlyList<IXboxGameAccount> GetAccounts()
    {
        var loginHandler = BuildLoginHandler();
        return loginHandler.AccountManager.GetAccounts().ToList();
    }
}
