using System.Security.Cryptography;
using SecretsManager.Core;
using SecretsManager.Core.App;

namespace SecretsManager.Core.Tests;

/// <summary>Windows Hello unlock (with a fake provider), the sealed-password format, and the "get" command.</summary>
public sealed class HelloAndCliTests
{
    readonly FakeSecretsService _svc = new();
    readonly FakeClipboard _clip = new();
    readonly FakeStorePasswordProvider _hello = new();

    AppStore Store() => new(_svc, _clip, storePassword: _hello) { WindowsVaultModulePath = @"C:\m\SecretsManager.WindowsVault.psd1" };
    static Func<PromptRequest, PromptResult?> Password(string pw) => _ => new PromptResult(Password: pw);

    FakeSecretsService.Vault AddStore(string password = "store-pw")
    {
        var v = _svc.AddVault("Prod", password: password, isDefault: true, module: VaultInfo.SecretStoreModule);
        v.Secrets["prod-key"] = (new TextSecret("P"), new());
        _svc.IsSecretStoreInitialized = true;
        return v;
    }

    // ------------------------------------------------------------------ seal format

    [Fact]
    public void Sealed_password_opens_only_with_the_same_signature()
    {
        var challenge = StorePasswordSeal.NewChallenge();
        var signature = RandomNumberGenerator.GetBytes(256);
        var file = StorePasswordSeal.Seal("s3cret pw", challenge, signature);
        Assert.Equal(challenge, StorePasswordSeal.ReadChallenge(file));
        Assert.DoesNotContain("s3cret", System.Text.Encoding.UTF8.GetString(file));
        Assert.Equal("s3cret pw", StorePasswordSeal.Unseal(file, signature));
        Assert.Throws<SealMismatchException>(() => StorePasswordSeal.Unseal(file, RandomNumberGenerator.GetBytes(256)));
    }

    // ------------------------------------------------------------------ app

    [Fact]
    public async Task A_locked_SecretStore_opens_with_Windows_Hello_without_a_typed_password()
    {
        AddStore();
        _hello.Sealed = "store-pw";
        var store = Store();
        var prompts = new PromptScript(store);
        await store.InitializeAsync();
        Assert.Empty(prompts.Asked);
        Assert.Equal(["prod-key"], store.State.Secrets.Select(s => s.Name));
        Assert.Equal(1, _hello.Prompts);
        Assert.True(store.State.HelloEnabled);
    }

    [Fact]
    public async Task Cancelling_Windows_Hello_falls_back_to_the_typed_password()
    {
        AddStore();
        _hello.Sealed = "store-pw";
        _hello.Cancel = true;
        var store = Store();
        var prompts = new PromptScript(store, Password("store-pw"));
        await store.InitializeAsync();
        Assert.Equal(PromptKind.Password, Assert.Single(prompts.Asked).Kind);
        Assert.Single(store.State.Secrets);
    }

    [Fact]
    public async Task Turning_Hello_on_checks_the_password_first_and_off_forgets_it()
    {
        AddStore();
        var store = Store();
        var prompts = new PromptScript(store, Password("store-pw")); // the initial unlock
        await store.InitializeAsync();
        prompts.Then(Password("wrong"));
        prompts.Then(Password("store-pw"));
        await store.SetHelloEnabledAsync(true);
        Assert.Contains("not accepted", prompts.Asked[^1].Message);
        Assert.Equal("store-pw", _hello.Sealed);
        Assert.True(store.State.HelloEnabled);

        await store.SetHelloEnabledAsync(false);
        Assert.Null(_hello.Sealed);
        Assert.False(store.State.HelloEnabled);
    }

    [Fact]
    public async Task Changing_the_store_password_reseals_it_and_resetting_turns_Hello_off()
    {
        AddStore();
        _hello.Sealed = "store-pw";
        var store = Store();
        var prompts = new PromptScript(store);
        await store.InitializeAsync();
        prompts.Then(_ => new PromptResult(CurrentPassword: "store-pw", Password: "new-pw"));
        await store.ChangeStorePasswordAsync();
        Assert.Equal("new-pw", _hello.Sealed);

        prompts.Then(_ => new PromptResult());                  // confirm
        prompts.Then(_ => new PromptResult(Text: "RESET"));     // type RESET
        prompts.Then(_ => new PromptResult(Option: true));      // no password
        await store.ResetStoreAsync();
        Assert.Null(_hello.Sealed);
        Assert.False(store.State.HelloEnabled);
    }

    [Fact]
    public async Task The_vault_form_registers_a_Windows_account_vault_with_the_bundled_module()
    {
        AddStore();
        _hello.Sealed = "store-pw";
        var store = Store();
        _ = new PromptScript(store);
        await store.InitializeAsync();
        store.OpenVaultForm();
        Assert.Equal((VaultKind.WindowsAccount, "Dev"), (store.State.VaultForm!.Kind, store.State.VaultForm.Name));
        await store.SaveVaultFormAsync();
        Assert.Contains(@"Register Dev C:\m\SecretsManager.WindowsVault.psd1", _svc.Calls);
    }

    // ------------------------------------------------------------------ get command

    async Task<(int Code, string Out, string Err)> Cli(params string[] args)
    {
        var (o, e) = (new StringWriter(), new StringWriter());
        var code = await SecretsCli.RunAsync(args, _svc, _hello, o, e);
        return (code, o.ToString(), e.ToString());
    }

    [Fact]
    public async Task Get_reads_a_dev_key_silently_without_touching_SecretStore()
    {
        AddStore();
        _svc.AddVault("Dev", module: @"C:\m\SecretsManager.WindowsVault.psd1").Secrets["dev-key"] = (new TextSecret("D"), new());
        _hello.Sealed = "store-pw";
        Assert.Equal((0, "D", ""), await Cli("get", "dev-key"));
        Assert.Equal(0, _hello.Prompts);
        Assert.DoesNotContain(_svc.Calls, c => c.StartsWith("UnlockStore"));
    }

    [Fact]
    public async Task Get_unlocks_SecretStore_with_Windows_Hello_for_every_read()
    {
        AddStore();
        _hello.Sealed = "store-pw";
        Assert.Equal((0, "P", ""), await Cli("get", "prod-key"));
        Assert.Equal(1, _hello.Prompts);

        _hello.Cancel = true;
        _svc.Vaults["Prod"].Unlocked = false; // a new process: SecretStore starts locked again
        var (code, _, err) = await Cli("get", "prod-key");
        Assert.Equal((SecretsCli.Denied, "Cancelled."), (code, err.Trim()));
    }

    [Fact]
    public async Task Get_explains_how_to_reach_a_locked_SecretStore_without_Hello()
    {
        AddStore();
        var (code, _, err) = await Cli("get", "prod-key");
        Assert.Equal(SecretsCli.Denied, code);
        Assert.Contains("Unlock with Windows Hello", err);
    }

    [Fact]
    public async Task Get_formats_each_kind_and_reports_what_is_missing()
    {
        var v = _svc.AddVault("Dev", isDefault: true);
        v.Secrets["cred"] = (new CredentialSecret("me", "pw"), new());
        v.Secrets["hash"] = (new HashtableSecret(new Dictionary<string, string> { ["a"] = "1" }), new());
        v.Secrets["bytes"] = (new BytesSecret([1, 2, 3]), new());
        Assert.Equal("pw", (await Cli("get", "cred")).Out);
        Assert.Equal("me", (await Cli("get", "cred", "--field", "username")).Out);
        Assert.Equal("{\"a\":\"1\"}", (await Cli("get", "HASH", "--vault", "dev")).Out);
        Assert.Equal("AQID", (await Cli("get", "bytes")).Out);
        Assert.Equal(SecretsCli.NotFound, (await Cli("get", "nope")).Code);
        Assert.Equal(SecretsCli.Failed, (await Cli("get", "cred", "--vault", "missing")).Code);
        Assert.Equal(SecretsCli.Failed, (await Cli("frobnicate")).Code);
    }
}
