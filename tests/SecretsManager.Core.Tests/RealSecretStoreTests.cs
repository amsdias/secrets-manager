using SecretsManager.Core;

namespace SecretsManager.Core.Tests;

/// <summary>
/// Exercises the REAL Microsoft.PowerShell.SecretStore through hosted PowerShell. Explicit only:
///   dotnet test --project tests/SecretsManager.Core.Tests -- --explicit only
/// It refuses to run when a SecretStore already exists for this user, and deletes the store folder
/// it created when done, so an existing store is never touched.
/// </summary>
public class RealSecretStoreTests
{
    /// <summary>
    /// One test, scenarios in a fixed order: SecretStore keeps process-wide static state (unlocked store, file watcher,
    /// folder created once), so separate tests in one process influence each other. Each scenario deletes the store folder
    /// at the end; after that, SecretStore's own password timeout stopped working, so the timeout check must come first.
    /// </summary>
    [Fact(Explicit = true)]
    public async Task Real_SecretStore_scenarios()
    {
        Assert.SkipWhen(Directory.Exists(SecretsService.DefaultSecretStoreFolder),
            $"A SecretStore already exists at {SecretsService.DefaultSecretStoreFolder}; refusing to touch it.");
        await Full_store_lifecycle();
        await Store_created_without_a_password_exists_and_is_usable();
    }

    static async Task Full_store_lifecycle()
    {
        var folder = SecretsService.DefaultSecretStoreFolder;
        Assert.False(Directory.Exists(folder), $"A SecretStore exists at {folder}; refusing to touch it.");
        // SecretStore creates this folder only once per process (static constructor); an earlier scenario deleted it.
        Directory.CreateDirectory(folder);

        using var host = new PowerShellHost();
        var svc = new SecretsService(host);
        const string vault = "SgRealStoreTest";
        try
        {
            Assert.True(await svc.IsSecretStoreModuleAvailableAsync());
            await svc.RegisterVaultAsync(vault, VaultInfo.SecretStoreModule, null, null, makeDefault: false);
            await svc.InitializeStoreAsync("Init#12345", 900);
            Assert.True(svc.IsSecretStoreInitialized);
            await Assert.ThrowsAsync<SecretsOperationException>(() => svc.InitializeStoreAsync("x", 900));

            Assert.Equal(new StoreConfig(StoreAuthentication.Password, 900, StoreInteraction.None), await svc.GetStoreConfigAsync());
            Assert.True((await svc.GetVaultsAsync()).Single(v => v.Name == vault).IsSecretStore);

            await svc.SetSecretAsync(vault, "s", new SecureTextSecret("hello"), new Dictionary<string, string> { ["env"] = "x" }, true);
            var entry = Assert.Single(await svc.GetSecretsAsync(vault));
            Assert.Equal((SecretKind.SecureString, "env=x"), (entry.Kind, entry.MetadataText));
            Assert.Equal(new SecureTextSecret("hello"), await svc.GetSecretValueAsync(vault, "s", entry.Kind));

            await svc.ChangeStorePasswordAsync("Init#12345", "New#12345");
            await Assert.ThrowsAnyAsync<SecretsOperationException>(() => svc.UnlockStoreAsync("Init#12345"));
            await svc.UnlockStoreAsync("New#12345");

            // The app's Lock: SecretStore has no lock cmdlet, so this resets its internal unlocked state.
            Assert.True(await svc.LockStoreAsync());
            await Assert.ThrowsAsync<VaultLockedException>(() => svc.GetSecretsAsync(vault));
            await svc.UnlockStoreAsync("New#12345");
            Assert.Single(await svc.GetSecretsAsync(vault));

            // Prompt mode in a host without UI must fail as "locked", not hang.
            await svc.SetStoreConfigAsync(new StoreConfig(StoreAuthentication.Password, 1, StoreInteraction.Prompt), null);
            await Task.Delay(TimeSpan.FromSeconds(3));
            using (var fresh = new PowerShellHost())
                await Assert.ThrowsAsync<VaultLockedException>(() => new SecretsService(fresh).GetSecretsAsync(vault));

            await svc.UnlockStoreAsync("New#12345");
            await svc.SetStoreConfigAsync(new StoreConfig(StoreAuthentication.None, 900, StoreInteraction.None), "New#12345");
            Assert.Equal(StoreAuthentication.None, (await svc.GetStoreConfigAsync()).Authentication);
        }
        finally
        {
            try { await svc.UnregisterVaultAsync(vault); } catch { }
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
        Assert.False(Directory.Exists(folder));
    }

    // Reset-SecretStore without a password writes only storeconfig; the app must still end up with a usable store.
    static async Task Store_created_without_a_password_exists_and_is_usable()
    {
        var folder = SecretsService.DefaultSecretStoreFolder;
        Assert.False(Directory.Exists(folder), $"A SecretStore exists at {folder}; refusing to touch it.");
        // SecretStore creates this folder only once per process (static constructor); an earlier scenario deleted it.
        Directory.CreateDirectory(folder);

        using var host = new PowerShellHost();
        var svc = new SecretsService(host);
        const string vault = "SgRealStoreNoPw";
        try
        {
            // Registering first is the app's order, and it leaves a storeconfig without a store file.
            await svc.RegisterVaultAsync(vault, VaultInfo.SecretStoreModule, null, null, makeDefault: false);
            Assert.False(svc.IsSecretStoreInitialized);

            await svc.InitializeStoreAsync(null, 900);
            Assert.True(svc.IsSecretStoreInitialized);
            Assert.Equal(StoreAuthentication.None, (await svc.GetStoreConfigAsync()).Authentication);

            await svc.SetSecretAsync(vault, "s", new TextSecret("hello"), null, true);
            Assert.Equal("s", Assert.Single(await svc.GetSecretsAsync(vault)).Name);
            using (var fresh = new PowerShellHost()) // a new process needs no unlock either
                Assert.Single(await new SecretsService(fresh).GetSecretsAsync(vault));
        }
        finally
        {
            try { await svc.UnregisterVaultAsync(vault); } catch { }
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
        Assert.False(Directory.Exists(folder));
    }
}
