using Daybreak.Core.Auth;
using Xunit;

namespace Daybreak.Core.Tests;

public class AccountStoreTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(
        Path.GetTempPath(), "daybreak-account-tests", Guid.NewGuid().ToString("N"));

    public AccountStoreTests() => AccountStore.DirectoryOverrideForTests = _tempDir;

    public void Dispose() => AccountStore.DirectoryOverrideForTests = null;

    [Fact]
    public void Load_WhenNoFile_ReturnsEmptyWithClientToken()
    {
        var store = AccountStore.Load();
        Assert.Empty(store.Accounts);
        Assert.Null(store.ActiveId);
        Assert.False(string.IsNullOrEmpty(store.ClientToken));
    }

    [Fact]
    public void UpsertActive_SavesAndMarksActive()
    {
        var store = AccountStore.Load();
        var account = new StoredAccount(
            "external:https://api/:uuid-1", AccountType.External, "Alice",
            "uuid-1", "https://api/", "测试服", "token-1");
        store.UpsertActive(account);

        var reloaded = AccountStore.Load();
        Assert.Single(reloaded.Accounts);
        Assert.Equal("Alice", reloaded.ActiveId is null ? null : reloaded.GetActive()!.Name);
        Assert.Equal("token-1", reloaded.GetActive()!.AccessToken);
    }

    [Fact]
    public void UpsertSameId_ReplacesInsteadOfDuplicating()
    {
        var store = AccountStore.Load();
        store.UpsertActive(new StoredAccount("offline:Bob", AccountType.Offline, "Bob"));
        store.UpsertActive(new StoredAccount("offline:Bob", AccountType.Offline, "Bob"));

        var reloaded = AccountStore.Load();
        Assert.Single(reloaded.Accounts);
    }

    [Fact]
    public void Remove_ClearsActiveWhenNeeded()
    {
        var store = AccountStore.Load();
        store.UpsertActive(new StoredAccount("offline:A", AccountType.Offline, "A"));
        store.UpsertActive(new StoredAccount("offline:B", AccountType.Offline, "B"));
        store.Remove("offline:B");

        var reloaded = AccountStore.Load();
        Assert.Single(reloaded.Accounts);
        Assert.Equal("offline:A", reloaded.ActiveId);

        reloaded.Remove("offline:A");
        Assert.Null(reloaded.ActiveId);
    }
}
