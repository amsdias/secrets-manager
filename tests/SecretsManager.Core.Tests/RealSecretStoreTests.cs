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
    [Fact(Explicit = true)]
    public async Task Full_store_lifecycle_against_the_real_SecretStore()
    {
        var folder = SecretsService.DefaultSecretStoreFolder;
        Assert.SkipWhen(Directory.Exists(folder), $"A SecretStore already exists at {folder}; refusing to touch it.");

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

            // Prompt mode in a host without UI must fail as "locked", not hang.
            await svc.SetStoreConfigAsync(new StoreConfig(StoreAuthentication.Password, 1, StoreInteraction.Prompt), null);
            await Task.Delay(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
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
}
