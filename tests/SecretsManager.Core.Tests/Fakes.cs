using System.Collections.Concurrent;
using SecretsManager.Core;
using SecretsManager.Core.App;

namespace SecretsManager.Core.Tests;

/// <summary>In-memory ISecretsService. Vaults with a password start locked.</summary>
public sealed class FakeSecretsService : ISecretsService
{
    public sealed class Vault
    {
        public required VaultInfo Info { get; set; }
        public string? Password { get; init; }
        public bool Unlocked { get; set; }
        public Dictionary<string, (SecretValue Value, Dictionary<string, string> Meta)> Secrets { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    public Dictionary<string, Vault> Vaults { get; } = new(StringComparer.OrdinalIgnoreCase);
    public bool StoreModuleAvailable { get; set; } = true;
    public bool IsSecretStoreInitialized { get; set; }
    public StoreConfig StoreConfig { get; set; } = new(StoreAuthentication.Password, 900, StoreInteraction.None);
    public List<string> Calls { get; } = [];
    public (string? Password, int Timeout)? LastReset { get; private set; }

    public Vault AddVault(string name, string? password = null, bool isDefault = false, string module = "Fake")
    {
        var v = new Vault { Info = new VaultInfo(name, module, isDefault, null, new Dictionary<string, string>()), Password = password };
        Vaults[name] = v;
        return v;
    }

    Vault Open(string name)
    {
        var v = Vaults[name];
        if (v.Password is not null && !v.Unlocked) throw new VaultLockedException($"A valid password is required to access {name}.");
        return v;
    }

    public Task<bool> IsSecretStoreModuleAvailableAsync() => Task.FromResult(StoreModuleAvailable);
    public Task<IReadOnlyList<VaultInfo>> GetVaultsAsync() => Task.FromResult<IReadOnlyList<VaultInfo>>(Vaults.Values.Select(v => v.Info).OrderBy(v => v.Name).ToList());

    public Task RegisterVaultAsync(string name, string moduleName, string? description, IReadOnlyDictionary<string, string>? parameters, bool makeDefault)
    {
        Calls.Add($"Register {name} {moduleName}");
        AddVault(name, module: moduleName, isDefault: makeDefault);
        return Task.CompletedTask;
    }

    public Task UnregisterVaultAsync(string name) { Calls.Add($"Unregister {name}"); Vaults.Remove(name); return Task.CompletedTask; }

    public Task SetDefaultVaultAsync(string name)
    {
        foreach (var v in Vaults.Values) v.Info = v.Info with { IsDefault = v.Info.Name == name };
        return Task.CompletedTask;
    }

    public Task UnlockVaultAsync(string name, string password)
    {
        Calls.Add($"Unlock {name} {password}");
        var v = Vaults[name];
        if (v.Password != password) throw new SecretsOperationException("The provided password is incorrect.");
        v.Unlocked = true;
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<SecretEntry>> GetSecretsAsync(string vault) =>
        Task.FromResult<IReadOnlyList<SecretEntry>>(Open(vault).Secrets.OrderBy(k => k.Key)
            .Select(k => new SecretEntry(k.Key, k.Value.Value.Kind, vault, k.Value.Meta)).ToList());

    public Task<SecretValue> GetSecretValueAsync(string vault, string name, SecretKind kind) => Task.FromResult(Open(vault).Secrets[name].Value);

    public Task SetSecretAsync(string vault, string name, SecretValue value, IReadOnlyDictionary<string, string>? metadata, bool noClobber)
    {
        var v = Open(vault);
        if (noClobber && v.Secrets.ContainsKey(name)) throw new SecretsOperationException($"A secret with name {name} already exists in vault {vault}.");
        var meta = metadata is { Count: > 0 } ? new Dictionary<string, string>(metadata) : v.Secrets.TryGetValue(name, out var old) ? old.Meta : new();
        v.Secrets[name] = (value, meta);
        Calls.Add($"Set {vault}/{name}");
        return Task.CompletedTask;
    }

    public Task SetSecretMetadataAsync(string vault, string name, IReadOnlyDictionary<string, string> metadata)
    {
        var v = Open(vault);
        v.Secrets[name] = (v.Secrets[name].Value, new Dictionary<string, string>(metadata));
        return Task.CompletedTask;
    }

    public Task RemoveSecretAsync(string vault, string name) { Open(vault).Secrets.Remove(name); Calls.Add($"Remove {vault}/{name}"); return Task.CompletedTask; }

    public async Task RenameSecretAsync(string vault, string name, string newName)
    {
        var s = Open(vault).Secrets[name];
        await SetSecretAsync(vault, newName, s.Value, s.Meta, true);
        await RemoveSecretAsync(vault, name);
    }

    public Task CopySecretAsync(string sourceVault, string name, string targetVault)
    {
        var s = Open(sourceVault).Secrets[name];
        return SetSecretAsync(targetVault, name, s.Value, s.Meta, true);
    }

    public Task InitializeStoreAsync(string? password, int passwordTimeout)
    {
        if (IsSecretStoreInitialized) throw new SecretsOperationException("A SecretStore already exists.");
        return ResetStoreAsync(password, passwordTimeout);
    }

    public Task ResetStoreAsync(string? password, int passwordTimeout)
    {
        LastReset = (password, passwordTimeout);
        IsSecretStoreInitialized = true;
        // Like the real Reset-SecretStore: the new store's configuration reflects the chosen authentication.
        StoreConfig = new(password is null ? StoreAuthentication.None : StoreAuthentication.Password, passwordTimeout, StoreInteraction.None);
        Calls.Add("ResetStore");
        return Task.CompletedTask;
    }

    public Task<StoreConfig> GetStoreConfigAsync() => Task.FromResult(StoreConfig);
    public Task SetStoreConfigAsync(StoreConfig config, string? password) { StoreConfig = config; Calls.Add($"SetStoreConfig {config} pw={password}"); return Task.CompletedTask; }
    public Task ChangeStorePasswordAsync(string currentPassword, string newPassword) { Calls.Add($"ChangePw {currentPassword}->{newPassword}"); return Task.CompletedTask; }
    public Task UnlockStoreAsync(string password) { Calls.Add($"UnlockStore {password}"); return Task.CompletedTask; }

    /// <summary>Like the real one: locks every SecretStore vault that has a password.</summary>
    public Task<bool> LockStoreAsync()
    {
        Calls.Add("LockStore");
        foreach (var v in Vaults.Values.Where(v => v.Info.IsSecretStore)) v.Unlocked = false;
        return Task.FromResult(true);
    }
}

public sealed class FakeClipboard : IClipboard
{
    public string? Text { get; set; }
    public Task SetTextAsync(string text) { Text = text; return Task.CompletedTask; }
    public Task<string?> GetTextAsync() => Task.FromResult(Text);
    public Task ClearAsync() { Text = null; return Task.CompletedTask; }
}

/// <summary>Answers AppStore prompts in order, recording what was asked. Unscripted prompts are cancelled.</summary>
public sealed class PromptScript
{
    readonly ConcurrentQueue<Func<PromptRequest, PromptResult?>> _answers = new();
    public List<PromptRequest> Asked { get; } = [];
    int _lastId;

    public PromptScript(AppStore store, params Func<PromptRequest, PromptResult?>[] answers)
    {
        foreach (var a in answers) _answers.Enqueue(a);
        store.Changed += () =>
        {
            if (store.State.Prompt is not { } p || p.Id == _lastId) return;
            _lastId = p.Id;
            lock (Asked) Asked.Add(p);
            var answer = _answers.TryDequeue(out var f) ? f(p) : null;
            _ = Task.Run(() => store.CompletePrompt(answer)); // answer asynchronously, like a real dialog
        };
    }

    public void Then(Func<PromptRequest, PromptResult?> answer) => _answers.Enqueue(answer);
}
