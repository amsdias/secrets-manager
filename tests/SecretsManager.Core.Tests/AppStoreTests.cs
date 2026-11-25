using Microsoft.Extensions.Time.Testing;
using SecretsManager.Core;
using SecretsManager.Core.App;

namespace SecretsManager.Core.Tests;

public class AppStoreTests
{
    readonly FakeSecretsService _svc = new();
    readonly FakeClipboard _clip = new();

    AppStore Store(TimeProvider? time = null) => new(_svc, _clip, time) { ClipboardClearSeconds = 3 };

    static PromptResult Ok(string? text = null) => new(Text: text);
    static Func<PromptRequest, PromptResult?> Password(string pw) => _ => new PromptResult(Password: pw);

    [Fact]
    public async Task Initialize_selects_the_default_vault_and_lists_its_secrets()
    {
        _svc.AddVault("a");
        _svc.AddVault("b", isDefault: true).Secrets["x"] = (new TextSecret("1"), new());
        var store = Store();
        await store.InitializeAsync();
        Assert.Equal("b", store.State.SelectedVault);
        Assert.Equal(["x"], store.State.Secrets.Select(s => s.Name));
        Assert.Null(store.State.EmptyMessage);
    }

    [Fact]
    public async Task First_run_offers_to_register_a_SecretStore_vault()
    {
        var store = Store();
        var prompts = new PromptScript(store, _ => Ok());
        await store.InitializeAsync();
        Assert.Equal(PromptKind.Confirm, Assert.Single(prompts.Asked).Kind);
        Assert.Equal(("SecretStore", VaultInfo.SecretStoreModule, true),
            (store.State.VaultForm!.Name, store.State.VaultForm.ModuleName, store.State.VaultForm.MakeDefault));
    }

    [Fact]
    public async Task Registering_SecretStore_creates_the_store_with_the_chosen_password()
    {
        var store = Store();
        var prompts = new PromptScript(store, _ => null, Password("s3cret!"));
        await store.InitializeAsync();
        store.OpenVaultForm();
        await store.SaveVaultFormAsync();
        Assert.Equal(PromptKind.NewPassword, prompts.Asked[1].Kind);
        Assert.Equal(("s3cret!", 900), _svc.LastReset);
        Assert.Equal("SecretStore", store.State.SelectedVault);
        Assert.Null(store.State.VaultForm);
    }

    [Fact]
    public async Task Locked_vault_prompts_reprompts_after_wrong_password_then_loads()
    {
        _svc.AddVault("locked", password: "right", isDefault: true).Secrets["s"] = (new TextSecret("v"), new());
        var store = Store();
        var prompts = new PromptScript(store, Password("wrong"), Password("right"));
        await store.InitializeAsync();
        Assert.Equal(2, prompts.Asked.Count);
        Assert.Contains("not accepted", prompts.Asked[1].Message);
        Assert.False(store.State.IsVaultLocked);
        Assert.Equal(["s"], store.State.Secrets.Select(x => x.Name));
    }

    [Fact]
    public async Task Cancelling_the_unlock_leaves_the_vault_marked_locked()
    {
        _svc.AddVault("locked", password: "pw", isDefault: true);
        var store = Store();
        _ = new PromptScript(store, _ => null);
        await store.InitializeAsync();
        Assert.True(store.State.IsVaultLocked);
        Assert.Contains("locked", store.State.EmptyMessage);
        Assert.Null(store.State.Error);
    }

    [Fact]
    public async Task Delete_requires_confirmation()
    {
        _svc.AddVault("v", isDefault: true).Secrets["s"] = (new TextSecret("x"), new());
        var store = Store();
        var prompts = new PromptScript(store, _ => null);
        await store.InitializeAsync();
        store.SelectSecret("s");
        await store.DeleteSelectedSecretAsync();
        Assert.True(prompts.Asked.Single().IsDanger);
        Assert.Single(store.State.Secrets);

        prompts.Then(_ => Ok());
        await store.DeleteSelectedSecretAsync();
        Assert.Empty(store.State.Secrets);
        Assert.Empty(_svc.Vaults["v"].Secrets);
    }

    [Fact]
    public async Task New_secret_with_a_taken_name_keeps_the_editor_open_with_an_error()
    {
        _svc.AddVault("v", isDefault: true).Secrets["taken"] = (new TextSecret("orig"), new());
        var store = Store();
        await store.InitializeAsync();
        store.OpenNewSecret();
        store.UpdateEditor(e => e with { Name = "taken" });
        store.GeneratePassword();
        await store.SaveEditorAsync();
        Assert.NotNull(store.State.Editor);
        Assert.Contains("already exists", store.State.Editor!.Error);
        Assert.Equal(new TextSecret("orig"), _svc.Vaults["v"].Secrets["taken"].Value);
    }

    [Fact]
    public async Task Editing_with_a_new_name_writes_the_new_secret_then_removes_the_old_one()
    {
        _svc.AddVault("v", isDefault: true).Secrets["old"] = (new CredentialSecret("u", "p"), new() { ["env"] = "x" });
        var store = Store();
        await store.InitializeAsync();
        store.SelectSecret("old");
        await store.OpenEditSecretAsync();
        Assert.Equal(("u", "p"), (store.State.Editor!.UserName, store.State.Editor.Password));
        store.UpdateEditor(e => e with { Name = "new", Password = "p2" });
        await store.SaveEditorAsync();
        Assert.Null(store.State.Editor);
        Assert.Equal(["new"], _svc.Vaults["v"].Secrets.Keys);
        Assert.Equal(new CredentialSecret("u", "p2"), _svc.Vaults["v"].Secrets["new"].Value);
        Assert.Equal("new", store.State.SelectedSecret);
    }

    [Fact]
    public async Task Clearing_metadata_while_editing_removes_it()
    {
        _svc.AddVault("v", isDefault: true).Secrets["s"] = (new TextSecret("a"), new() { ["env"] = "x" });
        var store = Store();
        await store.InitializeAsync();
        store.SelectSecret("s");
        await store.OpenEditSecretAsync();
        store.UpdateEditor(e => e with { MetadataText = "" });
        await store.SaveEditorAsync();
        Assert.Empty(_svc.Vaults["v"].Secrets["s"].Meta);
    }

    [Fact]
    public async Task Copy_clears_the_clipboard_after_the_countdown_only_if_unchanged()
    {
        var time = new FakeTimeProvider();
        _svc.AddVault("v", isDefault: true).Secrets["cred"] = (new CredentialSecret("me", "pw"), new());
        var store = Store(time);
        await store.InitializeAsync();
        store.SelectSecret("cred");
        await store.CopySelectedSecretAsync();
        Assert.Equal("pw", _clip.Text);               // credentials copy the password
        Assert.Equal(3, store.State.ClipboardSecondsLeft);

        await AdvanceAsync(time, 3);
        Assert.Null(_clip.Text);
        Assert.Null(store.State.ClipboardSecondsLeft);

        await store.CopySelectedSecretAsync();
        _clip.Text = "something the user copied later";
        await AdvanceAsync(time, 3);
        Assert.Equal("something the user copied later", _clip.Text);
    }

    static async Task AdvanceAsync(FakeTimeProvider time, int seconds)
    {
        for (var i = 0; i < seconds; i++)
        {
            time.Advance(TimeSpan.FromSeconds(1));
            await Task.Delay(20); // let the countdown continuation run
        }
        await Task.Delay(50);
    }

    [Fact]
    public async Task Rename_and_copy_to_vault()
    {
        _svc.AddVault("a", isDefault: true).Secrets["s"] = (new TextSecret("x"), new());
        _svc.AddVault("b");
        var store = Store();
        _ = new PromptScript(store, _ => Ok("renamed"), p => new PromptResult(ChoiceIndex: p.Choices.ToList().IndexOf("b")));
        await store.InitializeAsync();
        store.SelectSecret("s");
        await store.RenameSelectedSecretAsync();
        Assert.Equal(["renamed"], store.State.Secrets.Select(s => s.Name));
        await store.CopySelectedSecretToVaultAsync();
        Assert.True(_svc.Vaults["b"].Secrets.ContainsKey("renamed"));
    }

    [Fact]
    public async Task Reset_store_requires_typing_RESET()
    {
        _svc.IsSecretStoreInitialized = true;
        _svc.AddVault("v", isDefault: true);
        var store = Store();
        await store.InitializeAsync();
        var prompts = new PromptScript(store, _ => Ok(), _ => Ok("reset"));
        await store.ResetStoreAsync();
        Assert.Null(_svc.LastReset);
        Assert.Equal("RESET", prompts.Asked[1].RequiredText);

        prompts.Then(_ => Ok());
        prompts.Then(_ => Ok("RESET"));
        prompts.Then(_ => new PromptResult(Option: true));
        await store.ResetStoreAsync();
        Assert.Equal((null, 900), _svc.LastReset!.Value);
    }

    [Fact]
    public async Task Store_settings_turning_on_a_password_asks_for_it()
    {
        _svc.IsSecretStoreInitialized = true;
        _svc.AddVault("v", isDefault: true);
        _svc.StoreConfig = new(StoreAuthentication.None, 900, StoreInteraction.Prompt);
        var store = Store();
        await store.InitializeAsync();
        await store.OpenStoreSettingsAsync();
        store.UpdateStoreSettings(s => s with { Authentication = StoreAuthentication.Password, Interaction = StoreInteraction.None, TimeoutText = "60" });
        _ = new PromptScript(store, Password("newpw"));
        await store.SaveStoreSettingsAsync();
        Assert.Null(store.State.StoreSettings);
        Assert.Equal(new StoreConfig(StoreAuthentication.Password, 60, StoreInteraction.None), _svc.StoreConfig);
        Assert.Contains("pw=newpw", _svc.Calls.Last());
    }

    [Fact]
    public async Task Invalid_timeout_is_reported_in_the_settings_panel()
    {
        _svc.IsSecretStoreInitialized = true;
        _svc.AddVault("v", isDefault: true);
        var store = Store();
        await store.InitializeAsync();
        await store.OpenStoreSettingsAsync();
        store.UpdateStoreSettings(s => s with { TimeoutText = "soon" });
        await store.SaveStoreSettingsAsync();
        Assert.Contains("whole number", store.State.StoreSettings!.Error);
    }

    [Fact]
    public async Task SecretStore_vault_without_a_store_offers_creation_instead_of_unlock()
    {
        _svc.AddVault("SecretStore", isDefault: true, module: VaultInfo.SecretStoreModule);
        var store = Store();
        var prompts = new PromptScript(store);
        await store.InitializeAsync();
        Assert.True(store.State.IsStoreMissing);
        Assert.Contains("Store settings", store.State.EmptyMessage);
        Assert.Empty(prompts.Asked);                       // no "unlock" prompt for a store that doesn't exist
        store.OpenNewSecret();
        Assert.Null(store.State.Editor);

        prompts.Then(_ => Ok());                           // "Create it now?"
        prompts.Then(Password("first-pw"));                // new store password
        await store.OpenStoreSettingsAsync();
        Assert.Equal(("first-pw", 900), _svc.LastReset);
        Assert.False(store.State.IsStoreMissing);
        Assert.Contains("has no secrets", store.State.EmptyMessage);
    }

    [Theory]
    [InlineData(StoreAuthentication.None, false)]
    [InlineData(StoreAuthentication.Password, true)]
    public async Task Unlock_is_only_offered_for_a_SecretStore_that_uses_a_password(StoreAuthentication auth, bool canUnlock)
    {
        _svc.IsSecretStoreInitialized = true;
        _svc.StoreConfig = new(auth, 900, StoreInteraction.None);
        _svc.AddVault("SecretStore", isDefault: true, module: VaultInfo.SecretStoreModule);
        var store = Store();
        var prompts = new PromptScript(store, Password("pw"));
        await store.InitializeAsync();
        Assert.Equal(auth, store.State.StoreAuthentication);
        Assert.Equal(canUnlock, store.State.CanUnlock);

        await store.UnlockSelectedVaultAsync();
        Assert.Equal(canUnlock ? 1 : 0, prompts.Asked.Count);  // no password prompt when there is no password
        if (!canUnlock) Assert.Contains("doesn't use a password", store.State.Status);
    }

    [Fact]
    public async Task Creating_the_store_without_a_password_disables_unlock()
    {
        _svc.AddVault("SecretStore", isDefault: true, module: VaultInfo.SecretStoreModule);
        var store = Store();
        var prompts = new PromptScript(store);
        await store.InitializeAsync();
        Assert.False(store.State.CanUnlock);                       // store missing
        prompts.Then(_ => Ok());                                   // "Create it now?"
        prompts.Then(_ => new PromptResult(Option: true));         // "Don't use a password"
        await store.OpenStoreSettingsAsync();
        Assert.Equal(StoreAuthentication.None, store.State.StoreAuthentication);
        Assert.False(store.State.CanUnlock);
    }

    [Fact]
    public async Task Unlock_stays_available_for_other_vault_types()
    {
        _svc.AddVault("other", password: "pw", isDefault: true);
        var store = Store();
        _ = new PromptScript(store, Password("pw"));
        await store.InitializeAsync();
        Assert.True(store.State.CanUnlock);
    }

    [Fact]
    public async Task Search_filters_on_name_and_metadata()
    {
        var v = _svc.AddVault("v", isDefault: true);
        v.Secrets["github"] = (new TextSecret("1"), new());
        v.Secrets["db"] = (new TextSecret("2"), new() { ["owner"] = "git-team" });
        v.Secrets["other"] = (new TextSecret("3"), new());
        var store = Store();
        await store.InitializeAsync();
        store.SetSearch("GIT");
        Assert.Equal(["db", "github"], store.State.VisibleSecrets.Select(s => s.Name));
        store.SetSearch("zzz");
        Assert.Equal("No secrets match 'zzz'.", store.State.EmptyMessage);
    }
}
