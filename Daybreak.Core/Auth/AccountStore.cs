using System.Text.Json;

namespace Daybreak.Core.Auth;

/// <summary>账号类型。</summary>
public static class AccountType
{
    public const string Offline = "offline";
    public const string Microsoft = "microsoft";
    public const string External = "external";
}

/// <summary>存储的账号条目(微软账号的令牌由 CmlLib 登录管理器自行持久化,这里只存展示信息;外置账号存 accessToken 供续期)。</summary>
public record StoredAccount(
    string Id,
    string Type,
    string Name,
    string? Uuid = null,
    string? ServerUrl = null,
    string? ServerName = null,
    string? AccessToken = null);

/// <summary>
/// 多账号存储:accounts.json 位于 %APPDATA%/Daybreak。
/// 微软账号令牌的持久化由 JELoginHandler 的 AccountManager 负责,本文件只存索引与展示信息。
/// </summary>
public class AccountStore
{
    public string? ActiveId { get; set; }
    public string? ClientToken { get; set; } = Guid.NewGuid().ToString("N");
    public List<StoredAccount> Accounts { get; set; } = [];

    /// <summary>测试用存储目录重定向;生产环境必须保持为 null。</summary>
    internal static string? DirectoryOverrideForTests;

    public static string DefaultDirectory => DirectoryOverrideForTests
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Daybreak");

    public static string GetFilePath() => Path.Combine(DefaultDirectory, "accounts.json");

    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };

    public static AccountStore Load()
    {
        try
        {
            var path = GetFilePath();
            if (File.Exists(path))
            {
                var store = JsonSerializer.Deserialize<AccountStore>(File.ReadAllText(path), SerializerOptions);
                if (store is not null)
                    return store;
            }
        }
        catch (JsonException)
        {
            // 损坏则回退默认
        }

        return new AccountStore();
    }

    public void Save()
    {
        Directory.CreateDirectory(DefaultDirectory);
        File.WriteAllText(GetFilePath(), JsonSerializer.Serialize(this, SerializerOptions));
    }

    public StoredAccount? Find(string? id) =>
        Accounts.FirstOrDefault(a => a.Id == id);

    /// <summary>新增或按 Id 更新,并设为活动账号。</summary>
    public void UpsertActive(StoredAccount account)
    {
        Accounts.RemoveAll(a => a.Id == account.Id);
        Accounts.Add(account);
        ActiveId = account.Id;
        Save();
    }

    public void SetActive(string id)
    {
        ActiveId = id;
        Save();
    }

    public void Remove(string id)
    {
        Accounts.RemoveAll(a => a.Id == id);
        if (ActiveId == id)
            ActiveId = Accounts.FirstOrDefault()?.Id;
        Save();
    }

    public StoredAccount? GetActive() => Find(ActiveId);
}
