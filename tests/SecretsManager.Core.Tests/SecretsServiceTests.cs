using SecretsManager.Core;

namespace SecretsManager.Core.Tests;

/// <summary>
/// Integration tests: real hosted PowerShell + SecretManagement, against temporary vaults that use
/// tests/TestVault (a file-backed extension vault). The real SecretStore is never touched.
/// </summary>
public sealed class TestVaultFixture : IAsyncLifetime
{
    public PowerShellHost Host { get; } = new();
    public SecretsService Service { get; }
    readonly List<(string Name, string Path)> _vaults = [];

    public TestVaultFixture() =>
        Service = new SecretsService(Host, Path.Combine(Path.GetTempPath(), "sg-no-store-" + Guid.NewGuid().ToString("N")));

    static string ModulePath
    {
        get
        {
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            {
                var candidate = Path.Combine(dir.FullName, "tests", "TestVault", "TestVault.psd1");
                if (File.Exists(candidate)) return candidate;
            }
            throw new FileNotFoundException("tests/TestVault/TestVault.psd1 not found above " + AppContext.BaseDirectory);
        }
    }

    public async Task<string> NewVaultAsync(string? password = null)
    {
        var name = "SgCs_" + Guid.NewGuid().ToString("N")[..8];
        var path = Path.Combine(Path.GetTempPath(), name + ".clixml");
        var p = new Dictionary<string, string> { ["Path"] = path };
        if (password is not null) p["Password"] = password;
        await Service.RegisterVaultAsync(name, ModulePath, "test vault", p, makeDefault: false);
        _vaults.Add((name, path));
        return name;
    }

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        foreach (var (name, path) in _vaults)
        {
            try { await Service.UnregisterVaultAsync(name); } catch { }
            File.Delete(path);
            File.Delete(path + ".unlocked");
        }
        Host.Dispose();
    }
}

public class SecretsServiceTests(TestVaultFixture fx) : IClassFixture<TestVaultFixture>
{
    readonly SecretsService _svc = fx.Service;

    [Fact]
    public async Task Lists_registered_vault_with_parameters()
    {
        var name = await fx.NewVaultAsync();
        var vault = Assert.Single(await _svc.GetVaultsAsync(), v => v.Name == name);
        Assert.False(vault.IsSecretStore);
        Assert.Equal("test vault", vault.Description);
        Assert.EndsWith(".clixml", vault.Parameters["Path"]);
    }

    [Fact]
    public async Task Round_trips_every_secret_type_with_its_kind()
    {
        var v = await fx.NewVaultAsync();
        SecretValue[] values =
        [
            new TextSecret("plain"), new SecureTextSecret("hidden"), new CredentialSecret("me", "pw"),
            new HashtableSecret(new Dictionary<string, string> { ["k"] = "v" }), new BytesSecret([9, 8, 7]),
        ];
        foreach (var value in values) await _svc.SetSecretAsync(v, value.Kind.ToString(), value, null, noClobber: false);

        var list = await _svc.GetSecretsAsync(v);
        Assert.Equal(["ByteArray", "Hashtable", "PSCredential", "SecureString", "String"], list.Select(s => s.Name));
        foreach (var entry in list)
        {
            Assert.Equal(entry.Name, entry.Kind.ToString());
            var back = await _svc.GetSecretValueAsync(v, entry.Name, entry.Kind);
            var expected = values.Single(x => x.Kind == entry.Kind);
            Assert.Equal(expected.DisplayText, back.DisplayText);
            Assert.Equal(expected.Kind, back.Kind);
        }
    }

    [Fact]
    public async Task Stores_and_updates_metadata()
    {
        var v = await fx.NewVaultAsync();
        await _svc.SetSecretAsync(v, "m", new TextSecret("x"), new Dictionary<string, string> { ["env"] = "prod" }, false);
        Assert.Equal("env=prod", Assert.Single(await _svc.GetSecretsAsync(v)).MetadataText);
        await _svc.SetSecretMetadataAsync(v, "m", new Dictionary<string, string> { ["env"] = "dev" });
        Assert.Equal("env=dev", Assert.Single(await _svc.GetSecretsAsync(v)).MetadataText);
    }

    [Fact]
    public async Task NoClobber_refuses_to_overwrite()
    {
        var v = await fx.NewVaultAsync();
        await _svc.SetSecretAsync(v, "s", new TextSecret("one"), null, false);
        var ex = await Assert.ThrowsAsync<SecretsOperationException>(() => _svc.SetSecretAsync(v, "s", new TextSecret("two"), null, true));
        Assert.Contains("already exists", ex.Message);
        Assert.Equal("one", (await _svc.GetSecretValueAsync(v, "s", SecretKind.String)).DisplayText);
    }

    [Fact]
    public async Task Renames_keeping_metadata_and_copies_across_vaults()
    {
        var v = await fx.NewVaultAsync();
        var other = await fx.NewVaultAsync();
        await _svc.SetSecretAsync(v, "old", new CredentialSecret("u", "p"), new Dictionary<string, string> { ["a"] = "1" }, false);
        await _svc.RenameSecretAsync(v, "old", "new");
        var entry = Assert.Single(await _svc.GetSecretsAsync(v));
        Assert.Equal(("new", "a=1"), (entry.Name, entry.MetadataText));

        await _svc.CopySecretAsync(v, "new", other);
        Assert.Equal(new CredentialSecret("u", "p"), await _svc.GetSecretValueAsync(other, "new", SecretKind.PSCredential));
        Assert.Single(await _svc.GetSecretsAsync(v)); // source untouched
    }

    [Fact]
    public async Task Removes_a_secret()
    {
        var v = await fx.NewVaultAsync();
        await _svc.SetSecretAsync(v, "gone", new TextSecret("x"), null, false);
        await _svc.RemoveSecretAsync(v, "gone");
        Assert.Empty(await _svc.GetSecretsAsync(v));
    }

    [Fact]
    public async Task Locked_vault_throws_VaultLocked_until_unlocked()
    {
        var v = await fx.NewVaultAsync(password: "letmein");
        await Assert.ThrowsAsync<VaultLockedException>(() => _svc.GetSecretsAsync(v));
        await Assert.ThrowsAsync<SecretsOperationException>(() => _svc.UnlockVaultAsync(v, "wrong"));
        await _svc.UnlockVaultAsync(v, "letmein");
        Assert.Empty(await _svc.GetSecretsAsync(v));
    }

    [Fact]
    public async Task Refuses_to_initialize_over_an_existing_store()
    {
        var folder = Directory.CreateTempSubdirectory("sg-store-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(folder, "storefile"), "");
            var svc = new SecretsService(fx.Host, folder);
            Assert.True(svc.IsSecretStoreInitialized);
            var ex = await Assert.ThrowsAsync<SecretsOperationException>(() => svc.InitializeStoreAsync("pw", 900));
            Assert.Contains("already exists", ex.Message);
        }
        finally { Directory.Delete(folder, true); }
    }
}
