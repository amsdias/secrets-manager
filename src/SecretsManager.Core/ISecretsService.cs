namespace SecretsManager.Core;

/// <summary>Everything the app does with SecretManagement / SecretStore. Implemented over hosted PowerShell.</summary>
public interface ISecretsService
{
    Task<bool> IsSecretStoreModuleAvailableAsync();
    bool IsSecretStoreInitialized { get; }

    Task<IReadOnlyList<VaultInfo>> GetVaultsAsync();
    Task RegisterVaultAsync(string name, string moduleName, string? description, IReadOnlyDictionary<string, string>? parameters, bool makeDefault);
    Task UnregisterVaultAsync(string name);
    Task SetDefaultVaultAsync(string name);
    Task UnlockVaultAsync(string name, string password);

    Task<IReadOnlyList<SecretEntry>> GetSecretsAsync(string vault);
    Task<SecretValue> GetSecretValueAsync(string vault, string name, SecretKind kind);
    Task SetSecretAsync(string vault, string name, SecretValue value, IReadOnlyDictionary<string, string>? metadata, bool noClobber);
    Task SetSecretMetadataAsync(string vault, string name, IReadOnlyDictionary<string, string> metadata);
    Task RemoveSecretAsync(string vault, string name);
    /// <summary>SecretManagement has no rename: copy to the new name (never overwriting), then remove the old one.</summary>
    Task RenameSecretAsync(string vault, string name, string newName);
    /// <summary>Copies value + metadata; never overwrites an existing secret.</summary>
    Task CopySecretAsync(string sourceVault, string name, string targetVault);

    Task InitializeStoreAsync(string? password, int passwordTimeout);
    Task ResetStoreAsync(string? password, int passwordTimeout);
    Task<StoreConfig> GetStoreConfigAsync();
    Task SetStoreConfigAsync(StoreConfig config, string? password);
    Task ChangeStorePasswordAsync(string currentPassword, string newPassword);
    Task UnlockStoreAsync(string password);
}
