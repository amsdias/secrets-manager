using System.Text;
using Microsoft.Extensions.Time.Testing;
using SecretsManager.Core;
using SecretsManager.Core.App;

namespace SecretsManager.Core.Tests;

/// <summary>Export/import, locking, metadata columns and search, settings.</summary>
public sealed class FeatureTests : IDisposable
{
    readonly FakeSecretsService _svc = new();
    readonly FakeClipboard _clip = new();
    readonly string _dir = Directory.CreateTempSubdirectory("sm-tests-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    AppStore Store(TimeProvider? time = null, IAppSettingsStore? settings = null) =>
        new(_svc, _clip, time, settings) { ClipboardClearSeconds = 3, ExportIterations = 1_000 };

    static Func<PromptRequest, PromptResult?> Password(string pw) => _ => new PromptResult(Password: pw);
    static Func<PromptRequest, PromptResult?> Choice(int i) => _ => new PromptResult(ChoiceIndex: i);

    static readonly ExportedSecret[] AllKinds =
    [
        new("text", new TextSecret("plain"), new Dictionary<string, string> { ["env"] = "prod" }),
        new("secure", new SecureTextSecret("s3cret"), new Dictionary<string, string>()),
        new("cred", new CredentialSecret("me", "pw"), new Dictionary<string, string> { ["owner"] = "ops", ["env"] = "dev" }),
        new("hash", new HashtableSecret(new Dictionary<string, string> { ["a"] = "1", ["b"] = "2" }), new Dictionary<string, string>()),
        new("bytes", new BytesSecret([0, 1, 2, 255]), new Dictionary<string, string>()),
    ];

    // ------------------------------------------------------------------ export file format

    [Fact]
    public void Export_file_round_trips_every_kind_and_hides_the_content()
    {
        var file = SecretsExport.Encrypt(AllKinds, "pw#1", iterations: 1_000);
        var text = Encoding.UTF8.GetString(file);
        Assert.DoesNotContain("s3cret", text);
        Assert.DoesNotContain("secure", text); // names are encrypted too

        var back = SecretsExport.Decrypt(file, "pw#1");
        Assert.Equal(AllKinds.Select(s => (s.Name, s.Value.Kind, s.Value.DisplayText)), back.Select(s => (s.Name, s.Value.Kind, s.Value.DisplayText)));
        Assert.Equal("ops", back.Single(s => s.Name == "cred").Metadata["owner"]);
    }

    [Fact]
    public void Wrong_password_and_tampering_are_detected()
    {
        var file = SecretsExport.Encrypt(AllKinds, "right", iterations: 1_000);
        Assert.Throws<ExportPasswordException>(() => SecretsExport.Decrypt(file, "wrong"));

        // Changing the authenticated iteration count must not go unnoticed either.
        var tampered = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(file).Replace("\"iterations\": 1000", "\"iterations\": 1001"));
        Assert.NotEqual(file, tampered);
        Assert.Throws<ExportPasswordException>(() => SecretsExport.Decrypt(tampered, "right"));
    }

    [Fact]
    public void A_file_that_is_not_an_export_is_reported_as_such()
    {
        var ex = Assert.Throws<SecretsOperationException>(() => SecretsExport.Decrypt(Encoding.UTF8.GetBytes("{\"hello\":1}"), "x"));
        Assert.IsNotType<ExportPasswordException>(ex);
        Assert.Throws<SecretsOperationException>(() => SecretsExport.Decrypt([1, 2, 3], "x"));
    }

    // ------------------------------------------------------------------ export / import flows

    [Fact]
    public async Task Export_then_import_into_another_vault_skipping_existing_secrets()
    {
        var src = _svc.AddVault("src", isDefault: true);
        foreach (var s in AllKinds) src.Secrets[s.Name] = (s.Value, new Dictionary<string, string>(s.Metadata));
        var dst = _svc.AddVault("dst");
        dst.Secrets["text"] = (new TextSecret("keep me"), new());

        var store = Store();
        var path = Path.Combine(_dir, "out" + SecretsExport.FileExtension);
        var prompts = new PromptScript(store, Password("exp#1"));
        await store.InitializeAsync();
        await store.ExportSelectedVaultAsync(path);
        Assert.Null(store.State.Error);
        Assert.Contains("Exported 5", store.State.Status);

        await store.SelectVaultAsync("dst");
        prompts.Then(Password("exp#1"));
        prompts.Then(Choice(0)); // skip existing
        await store.ImportIntoSelectedVaultAsync(path);

        Assert.Equal(PromptKind.Choice, prompts.Asked[^1].Kind);
        Assert.Contains("1 of the 5", prompts.Asked[^1].Message);
        Assert.Equal("keep me", ((TextSecret)dst.Secrets["text"].Value).Text);
        Assert.Equal(new CredentialSecret("me", "pw"), dst.Secrets["cred"].Value);
        Assert.Equal("ops", dst.Secrets["cred"].Meta["owner"]);
        Assert.Contains("Imported 4", store.State.Status);
        Assert.Contains("Skipped 1", store.State.Status);
        Assert.Equal(5, store.State.Secrets.Length);
    }

    [Fact]
    public async Task Import_can_overwrite_and_asks_again_after_a_wrong_password()
    {
        var file = SecretsExport.Encrypt([new("text", new TextSecret("new"), new Dictionary<string, string>())], "right", 1_000);
        var path = Path.Combine(_dir, "in" + SecretsExport.FileExtension);
        await File.WriteAllBytesAsync(path, file, TestContext.Current.CancellationToken);
        var v = _svc.AddVault("v", isDefault: true);
        v.Secrets["text"] = (new TextSecret("old"), new());

        var store = Store();
        var prompts = new PromptScript(store);
        await store.InitializeAsync();
        prompts.Then(Password("wrong"));
        prompts.Then(Password("right"));
        prompts.Then(Choice(1)); // overwrite
        await store.ImportIntoSelectedVaultAsync(path);

        Assert.Contains("didn't open", prompts.Asked[1].Message);
        Assert.Equal("new", ((TextSecret)v.Secrets["text"].Value).Text);
        Assert.Null(store.State.Error);
    }

    [Fact]
    public async Task Cancelling_the_export_password_writes_nothing()
    {
        _svc.AddVault("v", isDefault: true).Secrets["s"] = (new TextSecret("x"), new());
        var store = Store();
        _ = new PromptScript(store, _ => null);
        await store.InitializeAsync();
        var path = Path.Combine(_dir, "none.smsecrets");
        await store.ExportSelectedVaultAsync(path);
        Assert.False(File.Exists(path));
        Assert.Equal("Export cancelled.", store.State.Status);
    }

    // ------------------------------------------------------------------ locking

    FakeSecretsService.Vault AddStoreVault()
    {
        var v = _svc.AddVault("store", password: "pw", isDefault: true, module: VaultInfo.SecretStoreModule);
        v.Secrets["s"] = (new TextSecret("x"), new());
        _svc.IsSecretStoreInitialized = true;
        return v;
    }

    [Fact]
    public async Task Lock_drops_open_values_and_relocks_a_password_SecretStore()
    {
        AddStoreVault();
        var store = Store();
        var prompts = new PromptScript(store, Password("pw"));
        await store.InitializeAsync();
        store.SelectSecret("s");
        await store.ViewSecretAsync();
        await store.CopySelectedSecretAsync();
        Assert.NotNull(store.State.Viewer);

        await store.LockAsync();
        Assert.Null(store.State.Viewer);
        Assert.Null(_clip.Text);
        Assert.True(store.State.IsVaultLocked);
        Assert.Empty(store.State.Secrets);
        Assert.Contains("LockStore", _svc.Calls);

        prompts.Then(Password("pw"));
        await store.RefreshAsync(); // asks for the password again
        Assert.Equal(2, prompts.Asked.Count(p => p.Kind == PromptKind.Password));
        Assert.Single(store.State.Secrets);
    }

    [Fact]
    public async Task Lock_leaves_the_list_of_a_SecretStore_without_a_password()
    {
        var v = AddStoreVault();
        _svc.Vaults["store"] = new FakeSecretsService.Vault { Info = v.Info }; // no password
        _svc.Vaults["store"].Secrets["s"] = (new TextSecret("x"), new());
        _svc.StoreConfig = new(StoreAuthentication.None, 900, StoreInteraction.None);
        var store = Store();
        await store.InitializeAsync();
        await store.LockAsync();
        Assert.False(store.State.IsVaultLocked);
        Assert.Single(store.State.Secrets);
    }

    [Fact]
    public async Task Auto_lock_fires_after_the_idle_time_and_activity_postpones_it()
    {
        AddStoreVault();
        var time = new FakeTimeProvider();
        var store = Store(time, new InMemorySettingsStore(new AppSettings(AutoLockMinutes: 1)));
        _ = new PromptScript(store, Password("pw"));
        await store.InitializeAsync();
        Assert.False(store.State.IsVaultLocked);

        await AdvanceAsync(time, 50);
        store.NotifyActivity();
        await AdvanceAsync(time, 50); // 100 s since start, but only 50 s idle
        Assert.False(store.State.IsVaultLocked);

        await AdvanceAsync(time, 15);
        Assert.True(store.State.IsVaultLocked);
        Assert.Contains("without activity", store.State.Status);
    }

    [Fact]
    public async Task Auto_lock_off_never_locks_and_the_choice_is_saved()
    {
        AddStoreVault();
        var time = new FakeTimeProvider();
        var settings = new InMemorySettingsStore(new AppSettings(AutoLockMinutes: 1));
        var store = Store(time, settings);
        _ = new PromptScript(store, Password("pw"));
        await store.InitializeAsync();
        await store.SetAutoLockMinutesAsync(0);
        Assert.Equal(0, settings.Current.AutoLockMinutes);

        await AdvanceAsync(time, 120);
        Assert.False(store.State.IsVaultLocked);
    }

    static async Task AdvanceAsync(FakeTimeProvider time, int seconds)
    {
        for (var i = 0; i < seconds; i += 5)
        {
            time.Advance(TimeSpan.FromSeconds(Math.Min(5, seconds - i)));
            await Task.Delay(15);
        }
        await Task.Delay(50);
    }

    [Fact]
    public void Settings_file_round_trips_and_falls_back_to_defaults()
    {
        var path = Path.Combine(_dir, "sub", "settings.json");
        var store = new JsonFileSettingsStore(path);
        Assert.Equal(new AppSettings(), store.Load());
        store.Save(new AppSettings(AutoLockMinutes: 30));
        Assert.Equal(30, new JsonFileSettingsStore(path).Load().AutoLockMinutes);
        File.WriteAllText(path, "not json");
        Assert.Equal(new AppSettings(), store.Load());
    }

    // ------------------------------------------------------------------ metadata

    static AppState WithSecrets(params (string Name, Dictionary<string, string> Meta)[] secrets) => new()
    {
        Secrets = [.. secrets.Select(s => new SecretEntry(s.Name, SecretKind.String, "v", s.Meta))],
    };

    [Fact]
    public void Metadata_columns_are_the_most_used_keys()
    {
        var s = WithSecrets(
            ("a", new() { ["env"] = "prod", ["owner"] = "ops", ["rare"] = "1" }),
            ("b", new() { ["Env"] = "dev", ["owner"] = "dev" }),
            ("c", new() { ["env"] = "test", ["zeta"] = "z", ["alpha"] = "a" }));
        Assert.Equal(["env", "owner", "alpha"], s.MetadataColumns);
        Assert.Equal("dev", AppState.MetadataValue(s.Secrets[1], "env"));
        Assert.Equal("rare=1", AppState.OtherMetadataText(s.Secrets[0], s.MetadataColumns));
    }

    [Fact]
    public void Key_value_search_matches_that_metadata_key_only()
    {
        var s = WithSecrets(
            ("db", new() { ["env"] = "production" }),
            ("api", new() { ["myenv"] = "prod" }),
            ("web", new() { ["env"] = "dev" })) with { Search = "env=prod" };
        Assert.Equal(["db", "api"], s.VisibleSecrets.Select(x => x.Name)); // "api" still matches the metadata text
        Assert.Equal(["db"], (s with { Search = "ENV = product" }).VisibleSecrets.Select(x => x.Name));
        Assert.Equal(["web"], (s with { Search = "we" }).VisibleSecrets.Select(x => x.Name));
    }
}
