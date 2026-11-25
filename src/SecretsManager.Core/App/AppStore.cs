using System.Collections.Immutable;

namespace SecretsManager.Core.App;

/// <summary>
/// The application: immutable <see cref="AppState"/> plus every user action, with no UI dependency.
/// The UI renders <see cref="State"/>, calls these methods, and answers <see cref="AppState.Prompt"/>
/// via <see cref="CompletePrompt"/>. Methods return Tasks so tests can await whole flows.
/// </summary>
public sealed class AppStore(ISecretsService secrets, IClipboard clipboard, TimeProvider? time = null)
{
    const int DefaultPasswordTimeout = 900;
    const int MaxFileBytes = 1024 * 1024;

    readonly TimeProvider _time = time ?? TimeProvider.System;
    readonly Lock _lock = new();
    AppState _state = new();
    TaskCompletionSource<PromptResult?>? _pendingPrompt;
    int _promptId, _loadVersion;
    CancellationTokenSource? _clipboardCts;
    string? _clipboardValue;

    public int ClipboardClearSeconds { get; init; } = 30;
    public AppState State => _state;
    public event Action? Changed;

    /// <summary>For Reactor's UseExternalStore: returns the unsubscribe action.</summary>
    public Action Subscribe(Action listener)
    {
        Changed += listener;
        return () => Changed -= listener;
    }

    void Update(Func<AppState, AppState> change)
    {
        lock (_lock) _state = change(_state);
        Changed?.Invoke();
    }

    void SetStatus(string status) => Update(s => s with { Status = status });
    public void DismissError() => Update(s => s with { Error = null });

    /// <summary>Busy indicator + uniform error handling for top-level actions.</summary>
    async Task Run(Func<Task> action)
    {
        Update(s => s with { BusyCount = s.BusyCount + 1, Error = null });
        try { await action(); }
        catch (OperationCanceledException) { SetStatus("Cancelled."); }
        catch (Exception ex) { Update(s => s with { Error = ex.Message, Status = "Error: " + ex.Message }); }
        finally { Update(s => s with { BusyCount = s.BusyCount - 1 }); }
    }

    // ------------------------------------------------------------------ prompts

    public Task<PromptResult?> AskAsync(PromptRequest request)
    {
        var tcs = new TaskCompletionSource<PromptResult?>(TaskCreationOptions.RunContinuationsAsynchronously);
        Interlocked.Exchange(ref _pendingPrompt, tcs)?.TrySetResult(null);
        Update(s => s with { Prompt = request with { Id = Interlocked.Increment(ref _promptId) } });
        return tcs.Task;
    }

    /// <summary>Called by the UI after the prompt dialog has closed. Null means cancelled.</summary>
    public void CompletePrompt(PromptResult? result)
    {
        var tcs = Interlocked.Exchange(ref _pendingPrompt, null);
        Update(s => s with { Prompt = null });
        tcs?.TrySetResult(result);
    }

    async Task<string?> AskVaultPassword(string vault, int attempt)
    {
        var message = attempt > 1
            ? $"The password was not accepted. Enter the password for '{vault}'."
            : $"'{vault}' is locked. Enter its password to unlock it.";
        var r = await AskAsync(new PromptRequest(PromptKind.Password, "Unlock vault", message) { PrimaryText = "Unlock" });
        return r?.Password;
    }

    async Task<bool> ConfirmAsync(string title, string message, string primary, bool danger = false) =>
        await AskAsync(new PromptRequest(PromptKind.Confirm, title, message) { PrimaryText = primary, IsDanger = danger }) is not null;

    Task<T> WithVault<T>(string vault, Func<Task<T>> op) =>
        LockedRetry.RunAsync(op, vault, AskVaultPassword, pw => secrets.UnlockVaultAsync(vault, pw));

    Task WithVault(string vault, Func<Task> op) =>
        LockedRetry.RunAsync(op, vault, AskVaultPassword, pw => secrets.UnlockVaultAsync(vault, pw));

    Task<T> WithStore<T>(Func<Task<T>> op) =>
        LockedRetry.RunAsync(op, "SecretStore", AskVaultPassword, secrets.UnlockStoreAsync);

    Task WithStore(Func<Task> op) =>
        LockedRetry.RunAsync(op, "SecretStore", AskVaultPassword, secrets.UnlockStoreAsync);

    // ------------------------------------------------------------------ startup / vaults

    public Task InitializeAsync() => Run(async () =>
    {
        var available = await secrets.IsSecretStoreModuleAvailableAsync();
        Update(s => s with { SecretStoreAvailable = available });
        await ReloadVaultsAsync(null);
        if (State.Vaults.IsEmpty && available &&
            await ConfirmAsync("Welcome", "No secret vaults are registered yet. Register a local SecretStore vault now?", "Register"))
            OpenVaultForm();
    });

    public Task RefreshAsync() => Run(() => ReloadVaultsAsync(State.SelectedVault));

    async Task ReloadVaultsAsync(string? select)
    {
        var vaults = (await secrets.GetVaultsAsync()).ToImmutableArray();
        var pick = vaults.FirstOrDefault(v => v.Name == select) ?? vaults.FirstOrDefault(v => v.IsDefault) ?? vaults.FirstOrDefault();
        Update(s => s with { Vaults = vaults, SelectedVault = pick?.Name, HasLoaded = true });
        if (pick is null) Update(s => s with { Secrets = [], SelectedSecret = null, Status = "No vaults registered." });
        else await LoadSecretsAsync();
    }

    public Task SelectVaultAsync(string? name)
    {
        if (name == State.SelectedVault) return Task.CompletedTask;
        Update(s => s with { SelectedVault = name, Secrets = [], SelectedSecret = null, IsVaultLocked = false, IsStoreMissing = false, Search = "" });
        return Run(LoadSecretsAsync);
    }

    async Task LoadSecretsAsync()
    {
        var vault = State.SelectedVault;
        if (vault is null) return;
        var version = Interlocked.Increment(ref _loadVersion);
        // Registering a SecretStore vault doesn't create the store. Without this check, opening the vault would ask
        // to "unlock" a store that doesn't exist.
        if (State.CurrentVault is { IsSecretStore: true } && !secrets.IsSecretStoreInitialized)
        {
            Update(s => s with { IsStoreMissing = true, IsVaultLocked = false, Secrets = [], SelectedSecret = null, Status = "The SecretStore hasn't been created yet." });
            return;
        }
        try
        {
            var list = (await WithVault(vault, () => secrets.GetSecretsAsync(vault))).ToImmutableArray();
            if (version != _loadVersion) return; // the user moved on to another vault meanwhile
            Update(s => s with
            {
                Secrets = list, IsVaultLocked = false, IsStoreMissing = false,
                SelectedSecret = list.Any(x => x.Name == s.SelectedSecret) ? s.SelectedSecret : null,
                Status = $"{list.Length} secret(s) in '{vault}'.",
            });
            if (State.CurrentVault is { IsSecretStore: true })
            {
                try
                {
                    var config = await secrets.GetStoreConfigAsync();
                    Update(s => s with { StoreAuthentication = config.Authentication });
                }
                catch (SecretsOperationException) { /* informational only: leave the mode unknown */ }
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or VaultLockedException)
        {
            if (version == _loadVersion) Update(s => s with { IsVaultLocked = true, Secrets = [], Status = $"Vault '{vault}' is locked." });
        }
    }

    public Task UnlockSelectedVaultAsync() => Run(async () =>
    {
        if (State.CurrentVault is not { } vault) return;
        if (!State.CanUnlock)
        {
            SetStatus(State.IsStoreMissing ? "The SecretStore hasn't been created yet." : "This SecretStore doesn't use a password, so there is nothing to unlock.");
            return;
        }
        var r = await AskAsync(new PromptRequest(PromptKind.Password, "Unlock vault", $"Enter the password for '{vault.Name}'.") { PrimaryText = "Unlock" });
        if (r?.Password is null) return;
        await secrets.UnlockVaultAsync(vault.Name, r.Password);
        SetStatus($"Vault '{vault.Name}' unlocked.");
        await LoadSecretsAsync();
    });

    public Task SetDefaultVaultAsync() => Run(async () =>
    {
        if (State.SelectedVault is not { } name) return;
        await secrets.SetDefaultVaultAsync(name);
        await ReloadVaultsAsync(name);
        SetStatus($"'{name}' is now the default vault.");
    });

    public Task UnregisterSelectedVaultAsync() => Run(async () =>
    {
        if (State.SelectedVault is not { } name) return;
        if (!await ConfirmAsync("Unregister vault",
                $"Unregister '{name}'?\n\nThe secrets themselves are not deleted; registering the vault again restores access.", "Unregister"))
            return;
        await secrets.UnregisterVaultAsync(name);
        await ReloadVaultsAsync(null);
        SetStatus($"Unregistered '{name}'.");
    });

    public void OpenVaultForm() => Update(s => s with
    {
        VaultForm = new VaultFormState
        {
            Name = s.Vaults.Any(v => v.Name.Equals("SecretStore", StringComparison.OrdinalIgnoreCase)) ? "" : "SecretStore",
            ModuleName = s.SecretStoreAvailable ? VaultInfo.SecretStoreModule : "",
            MakeDefault = !s.Vaults.Any(v => v.IsDefault),
        },
    });

    public void UpdateVaultForm(Func<VaultFormState, VaultFormState> change) =>
        Update(s => s.VaultForm is null ? s : s with { VaultForm = change(s.VaultForm) });

    public void CloseVaultForm() => Update(s => s with { VaultForm = null });

    public async Task SaveVaultFormAsync()
    {
        if (State.VaultForm is not { IsSaving: false } form) return;
        UpdateVaultForm(f => f with { IsSaving = true, Error = null });
        try
        {
            var name = form.Name.Trim();
            var module = form.ModuleName.Trim();
            if (name.Length == 0) throw new FormatException("Enter a vault name.");
            if (module.Length == 0) throw new FormatException("Enter the extension module name or path.");
            var parameters = KeyValueText.Parse(form.ParametersText);
            await secrets.RegisterVaultAsync(name, module, form.Description.Trim(), parameters, form.MakeDefault);
            CloseVaultForm();
            await Run(async () =>
            {
                if (module.Equals(VaultInfo.SecretStoreModule, StringComparison.OrdinalIgnoreCase) && !secrets.IsSecretStoreInitialized)
                    await InitializeStoreFlowAsync();
                await ReloadVaultsAsync(name);
                SetStatus($"Registered vault '{name}'.");
            });
        }
        catch (Exception ex) { UpdateVaultForm(f => f with { Error = ex.Message }); }
        finally { UpdateVaultForm(f => f with { IsSaving = false }); }
    }

    // ------------------------------------------------------------------ secrets

    public void SetSearch(string text) => Update(s => s with { Search = text });
    public void SelectSecret(string? name) => Update(s => s with { SelectedSecret = name });

    public void OpenNewSecret()
    {
        if (State.SelectedVault is not { } vault || State.IsStoreMissing) return;
        Update(s => s with { Editor = new EditorState { VaultName = vault } });
    }

    public Task OpenEditSecretAsync() => Run(async () =>
    {
        if (State.CurrentSecret is not { } entry) return;
        var value = await WithVault(entry.VaultName, () => secrets.GetSecretValueAsync(entry.VaultName, entry.Name, entry.Kind));
        Update(s => s with { Editor = EditorState.ForExisting(entry, value) });
    });

    public void UpdateEditor(Func<EditorState, EditorState> change) =>
        Update(s => s.Editor is null ? s : s with { Editor = change(s.Editor) with { Error = null } });

    public void CloseEditor() => Update(s => s with { Editor = null });

    public void GeneratePassword()
    {
        var pw = PasswordGenerator.Generate();
        UpdateEditor(e => e.Kind == SecretKind.PSCredential ? e with { Password = pw } : e with { Text = pw });
    }

    public void LoadEditorBytes(byte[] data, string fileName)
    {
        if (data.Length > MaxFileBytes)
        {
            Update(s => s.Editor is null ? s : s with { Editor = s.Editor with { Error = $"The file is {data.Length / 1024.0 / 1024.0:0.0} MB; the limit is 1 MB." } });
            return;
        }
        UpdateEditor(e => e with
        {
            BytesBase64 = Convert.ToBase64String(data, Base64FormattingOptions.InsertLineBreaks),
            BytesInfo = $"{data.Length} bytes from {Path.GetFileName(fileName)}",
            Name = e.Name.Length > 0 ? e.Name : Path.GetFileNameWithoutExtension(fileName),
        });
    }

    public async Task SaveEditorAsync()
    {
        if (State.Editor is not { IsSaving: false } e) return;
        Update(s => s with { Editor = e with { IsSaving = true, Error = null } });
        try
        {
            var name = e.Name.Trim();
            if (name.Length == 0) throw new FormatException("Enter a name for the secret.");
            var value = e.BuildValue();
            var metadata = KeyValueText.Parse(e.MetadataText);
            var metadataCleared = !e.IsNew && e.OriginalMetadataText.Trim().Length > 0 && metadata.Count == 0;

            await WithVault(e.VaultName, async () =>
            {
                if (e.IsNew)
                    await secrets.SetSecretAsync(e.VaultName, name, value, metadata, noClobber: true);
                else if (name == e.OriginalName)
                {
                    await secrets.SetSecretAsync(e.VaultName, name, value, metadata, noClobber: false);
                    if (metadataCleared) await secrets.SetSecretMetadataAsync(e.VaultName, name, metadata);
                }
                else
                {
                    // Renamed while editing: write the new name first (refusing to overwrite), then drop the old one.
                    await secrets.SetSecretAsync(e.VaultName, name, value, metadata, noClobber: true);
                    await secrets.RemoveSecretAsync(e.VaultName, e.OriginalName!);
                }
            });
            Update(s => s with { Editor = null, SelectedSecret = name });
            await Run(LoadSecretsAsync);
            SetStatus($"Saved '{name}'.");
        }
        catch (OperationCanceledException) { Update(s => s with { Editor = s.Editor! with { IsSaving = false } }); }
        catch (Exception ex) { Update(s => s.Editor is null ? s : s with { Editor = s.Editor with { IsSaving = false, Error = ex.Message } }); }
    }

    public Task ViewSecretAsync() => Run(async () =>
    {
        if (State.CurrentSecret is not { } entry) return;
        var value = await WithVault(entry.VaultName, () => secrets.GetSecretValueAsync(entry.VaultName, entry.Name, entry.Kind));
        Update(s => s with { Viewer = new ViewerState(entry, value) });
    });

    public void ToggleViewerReveal() => Update(s => s.Viewer is null ? s : s with { Viewer = s.Viewer with { Reveal = !s.Viewer.Reveal } });
    public void CloseViewer() => Update(s => s with { Viewer = null });

    public Task CopyViewerValueAsync() => Run(async () =>
    {
        if (State.Viewer is not { } v) return;
        await CopyToClipboardAsync(v.Value.CopyText, v.Value is CredentialSecret ? $"Password of '{v.Entry.Name}'" : $"'{v.Entry.Name}'");
    });

    public Task CopyViewerUserNameAsync() => Run(async () =>
    {
        if (State.Viewer?.Value is not CredentialSecret c) return;
        await clipboard.SetTextAsync(c.UserName);
        SetStatus("User name copied.");
    });

    public Task CopySelectedSecretAsync() => Run(async () =>
    {
        if (State.CurrentSecret is not { } entry) return;
        var value = await WithVault(entry.VaultName, () => secrets.GetSecretValueAsync(entry.VaultName, entry.Name, entry.Kind));
        await CopyToClipboardAsync(value.CopyText, value is CredentialSecret ? $"Password of '{entry.Name}'" : $"'{entry.Name}'");
    });

    public Task DeleteSelectedSecretAsync() => Run(async () =>
    {
        if (State.CurrentSecret is not { } entry) return;
        if (!await ConfirmAsync("Delete secret", $"Delete '{entry.Name}' from vault '{entry.VaultName}'?\n\nThis cannot be undone.", "Delete", danger: true))
            return;
        await WithVault(entry.VaultName, () => secrets.RemoveSecretAsync(entry.VaultName, entry.Name));
        await LoadSecretsAsync();
        SetStatus($"Deleted '{entry.Name}'.");
    });

    public Task RenameSelectedSecretAsync() => Run(async () =>
    {
        if (State.CurrentSecret is not { } entry) return;
        var r = await AskAsync(new PromptRequest(PromptKind.Text, "Rename secret", $"New name for '{entry.Name}':") { InitialText = entry.Name, PrimaryText = "Rename" });
        var newName = r?.Text?.Trim();
        if (string.IsNullOrEmpty(newName) || newName == entry.Name) return;
        await WithVault(entry.VaultName, () => secrets.RenameSecretAsync(entry.VaultName, entry.Name, newName));
        Update(s => s with { SelectedSecret = newName });
        await LoadSecretsAsync();
        SetStatus($"Renamed '{entry.Name}' to '{newName}'.");
    });

    public Task CopySelectedSecretToVaultAsync() => Run(async () =>
    {
        if (State.CurrentSecret is not { } entry) return;
        var targets = State.Vaults.Select(v => v.Name).Where(n => n != entry.VaultName).ToList();
        if (targets.Count == 0) return;
        var r = await AskAsync(new PromptRequest(PromptKind.Choice, "Copy secret to vault", $"Copy '{entry.Name}' (value and metadata) to:") { Choices = targets, PrimaryText = "Copy" });
        if (r is null || r.ChoiceIndex < 0) return;
        var target = targets[r.ChoiceIndex];
        // Unlock the source first, so each password prompt names the right vault.
        await WithVault(entry.VaultName, () => secrets.GetSecretValueAsync(entry.VaultName, entry.Name, entry.Kind));
        await WithVault(target, () => secrets.CopySecretAsync(entry.VaultName, entry.Name, target));
        SetStatus($"Copied '{entry.Name}' to '{target}'.");
    });

    // ------------------------------------------------------------------ clipboard

    async Task CopyToClipboardAsync(string text, string what)
    {
        if (string.IsNullOrEmpty(text)) throw new InvalidOperationException("There is nothing to copy.");
        await clipboard.SetTextAsync(text);
        _clipboardValue = text;
        var cts = new CancellationTokenSource();
        Interlocked.Exchange(ref _clipboardCts, cts)?.Cancel();
        Update(s => s with { ClipboardSecondsLeft = ClipboardClearSeconds, Status = $"{what} copied to the clipboard." });
        _ = ClipboardCountdownAsync(cts.Token);
    }

    async Task ClipboardCountdownAsync(CancellationToken ct)
    {
        try
        {
            for (var left = ClipboardClearSeconds; left > 0; left--)
            {
                Update(s => s with { ClipboardSecondsLeft = left });
                await Task.Delay(TimeSpan.FromSeconds(1), _time, ct);
            }
            await ClearClipboardIfOursAsync();
            SetStatus("Clipboard cleared.");
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>Clears the clipboard only if it still holds what this app put there. Call on exit too.</summary>
    public async Task ClearClipboardIfOursAsync()
    {
        Interlocked.Exchange(ref _clipboardCts, null)?.Cancel();
        var ours = Interlocked.Exchange(ref _clipboardValue, null);
        if (ours is not null)
        {
            try { if (await clipboard.GetTextAsync() == ours) await clipboard.ClearAsync(); }
            catch { /* clipboard busy or unavailable: nothing more we can do */ }
        }
        Update(s => s with { ClipboardSecondsLeft = null });
    }

    // ------------------------------------------------------------------ SecretStore

    async Task<bool> InitializeStoreFlowAsync()
    {
        var r = await AskAsync(new PromptRequest(PromptKind.NewPassword, "Create SecretStore",
            "The SecretStore for your account has not been created yet. Choose a password to protect it; you will be asked for it again after the password timeout.")
        { OptionText = "Don't use a password (protected only by your Windows account)", PrimaryText = "Create" });
        if (r is null) { SetStatus("SecretStore was not created."); return false; }
        await secrets.InitializeStoreAsync(r.Option ? null : r.Password, DefaultPasswordTimeout);
        Update(s => s with { StoreAuthentication = r.Option ? StoreAuthentication.None : StoreAuthentication.Password, Status = "SecretStore created." });
        return true;
    }

    public Task OpenStoreSettingsAsync() => Run(async () =>
    {
        if (!State.SecretStoreAvailable)
            throw new InvalidOperationException("The Microsoft.PowerShell.SecretStore module is not installed. Install it with: Install-PSResource Microsoft.PowerShell.SecretStore -Scope CurrentUser");
        if (!secrets.IsSecretStoreInitialized)
        {
            if (await ConfirmAsync("SecretStore", "No SecretStore exists for your account yet. Create it now?", "Create") &&
                await InitializeStoreFlowAsync())
                await LoadSecretsAsync();
            return;
        }
        var config = await WithStore(secrets.GetStoreConfigAsync);
        Update(s => s with
        {
            StoreSettings = new StoreSettingsState
            {
                Original = config, Authentication = config.Authentication,
                TimeoutText = config.PasswordTimeout.ToString(), Interaction = config.Interaction,
            },
        });
    });

    public void UpdateStoreSettings(Func<StoreSettingsState, StoreSettingsState> change) =>
        Update(s => s.StoreSettings is null ? s : s with { StoreSettings = change(s.StoreSettings) with { Error = null } });

    public void CloseStoreSettings() => Update(s => s with { StoreSettings = null });

    public async Task SaveStoreSettingsAsync()
    {
        if (State.StoreSettings is not { IsSaving: false } st) return;
        Update(s => s with { StoreSettings = st with { IsSaving = true, Error = null } });
        try
        {
            if (!int.TryParse(st.TimeoutText.Trim(), out var timeout) || timeout < 1)
                throw new FormatException("The password timeout must be a whole number of seconds (1 or more).");
            string? password = null;
            if (st.Original.Authentication == StoreAuthentication.None && st.Authentication == StoreAuthentication.Password)
            {
                var r = await AskAsync(new PromptRequest(PromptKind.NewPassword, "Set SecretStore password", "Choose a password for the SecretStore."));
                if (r is null) throw new OperationCanceledException();
                password = r.Password;
            }
            else if (st.Original.Authentication == StoreAuthentication.Password && st.Authentication == StoreAuthentication.None)
            {
                var r = await AskAsync(new PromptRequest(PromptKind.Password, "Remove SecretStore password", "Enter the current SecretStore password to turn off password protection."));
                if (r is null) throw new OperationCanceledException();
                password = r.Password;
            }
            await WithStore(() => secrets.SetStoreConfigAsync(new StoreConfig(st.Authentication, timeout, st.Interaction), password));
            Update(s => s with { StoreSettings = null, StoreAuthentication = st.Authentication, Status = "SecretStore settings saved." });
        }
        catch (OperationCanceledException) { UpdateStoreSettings(x => x with { IsSaving = false }); }
        catch (Exception ex) { Update(s => s.StoreSettings is null ? s : s with { StoreSettings = s.StoreSettings with { IsSaving = false, Error = ex.Message } }); }
    }

    public Task ChangeStorePasswordAsync() => Run(async () =>
    {
        var r = await AskAsync(new PromptRequest(PromptKind.ChangePassword, "Change SecretStore password", "Enter the current password and choose a new one.") { PrimaryText = "Change" });
        if (r?.CurrentPassword is null || r.Password is null) return;
        await secrets.ChangeStorePasswordAsync(r.CurrentPassword, r.Password);
        SetStatus("SecretStore password changed.");
    });

    /// <summary>DESTRUCTIVE: asks twice (confirm, then type RESET) before deleting every SecretStore secret.</summary>
    public Task ResetStoreAsync() => Run(async () =>
    {
        if (!await ConfirmAsync("Reset SecretStore",
                "Resetting permanently deletes ALL secrets in the SecretStore, for every vault that uses Microsoft.PowerShell.SecretStore.\n\nContinue?",
                "Continue", danger: true))
            return;
        var typed = await AskAsync(new PromptRequest(PromptKind.Text, "Confirm reset", "Type RESET (in capitals) to delete every secret in the SecretStore.")
        { RequiredText = "RESET", PrimaryText = "Reset", IsDanger = true });
        if (typed?.Text != "RESET") { SetStatus("Reset cancelled."); return; }
        var pw = await AskAsync(new PromptRequest(PromptKind.NewPassword, "New SecretStore password", "Choose a password for the new, empty store.")
        { OptionText = "Don't use a password (protected only by your Windows account)" });
        if (pw is null) { SetStatus("Reset cancelled."); return; }
        await secrets.ResetStoreAsync(pw.Option ? null : pw.Password, DefaultPasswordTimeout);
        Update(s => s with { StoreSettings = null, StoreAuthentication = pw.Option ? StoreAuthentication.None : StoreAuthentication.Password });
        await ReloadVaultsAsync(State.SelectedVault);
        SetStatus("SecretStore was reset.");
    });
}
