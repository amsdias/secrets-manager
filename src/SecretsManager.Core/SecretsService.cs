using System.Collections;
using System.Management.Automation;
using System.Net;
using System.Security;

namespace SecretsManager.Core;

public sealed class SecretsService(PowerShellHost host, string? secretStoreFolder = null) : ISecretsService
{
    // Scripts run in a runspace with $ErrorActionPreference = 'Stop'; SecretManagement and
    // SecretStore autoload. A param() block must be the first statement, so nothing is prepended.

    /// <summary>Folder where SecretStore keeps its files for the current user.</summary>
    public static string DefaultSecretStoreFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Microsoft", "PowerShell", "secretmanagement", "localstore");

    readonly string _storeFolder = secretStoreFolder ?? DefaultSecretStoreFolder;
    bool? _storeModuleAvailable;

    Task<IReadOnlyList<PSObject>> Run(string script, IReadOnlyDictionary<string, object?>? args = null) =>
        host.InvokeAsync(script, args);

    Task<IReadOnlyList<PSObject>> RunStore(string script, IReadOnlyDictionary<string, object?>? args = null) =>
        host.InvokeAsync(script, args);

    // ---------------------------------------------------------------- SecretStore presence

    public async Task<bool> IsSecretStoreModuleAvailableAsync()
    {
        _storeModuleAvailable ??= (await Run("[bool](Get-Module -ListAvailable Microsoft.PowerShell.SecretStore)"))
            .Select(o => o.BaseObject).OfType<bool>().FirstOrDefault();
        return _storeModuleAvailable.Value;
    }

    /// <summary>True once a store data file exists (resetting it then destroys data).</summary>
    public bool IsSecretStoreInitialized => File.Exists(Path.Combine(_storeFolder, "storefile"));

    // ---------------------------------------------------------------- Vaults

    public async Task<IReadOnlyList<VaultInfo>> GetVaultsAsync()
    {
        var rows = await Run("Get-SecretVault | ForEach-Object { [pscustomobject]@{ Name = $_.Name; ModuleName = $_.ModuleName; IsDefault = [bool]$_.IsDefault; Description = $_.Description; Parameters = $_.VaultParameters } }");
        return rows.Select(r => new VaultInfo(
                Str(r, "Name"), Str(r, "ModuleName"), Prop(r, "IsDefault") is true,
                NullIfEmpty(Str(r, "Description")), ToStringDictionary(Prop(r, "Parameters"))))
            .OrderBy(v => v.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public Task RegisterVaultAsync(string name, string moduleName, string? description, IReadOnlyDictionary<string, string>? parameters, bool makeDefault) =>
        Run("""
            param($Name, $ModuleName, $Description, $VaultParameters, [bool]$DefaultVault)
            $p = @{ Name = $Name; ModuleName = $ModuleName }
            if ($Description) { $p.Description = $Description }
            if ($VaultParameters -and $VaultParameters.Count) { $p.VaultParameters = $VaultParameters }
            if ($DefaultVault) { $p.DefaultVault = $true }
            Register-SecretVault @p
            """, Args(("Name", name), ("ModuleName", moduleName), ("Description", description),
                ("VaultParameters", ToHashtable(parameters)), ("DefaultVault", makeDefault)));

    public Task UnregisterVaultAsync(string name) =>
        Run("param($Name) Unregister-SecretVault -Name $Name", Args(("Name", name)));

    public Task SetDefaultVaultAsync(string name) =>
        Run("param($Name) Set-SecretVaultDefault -Name $Name", Args(("Name", name)));

    public Task UnlockVaultAsync(string name, string password) =>
        Run("param($Name, $Password) Unlock-SecretVault -Name $Name -Password $Password",
            Args(("Name", name), ("Password", ToSecure(password))));

    // ---------------------------------------------------------------- Secrets

    public async Task<IReadOnlyList<SecretEntry>> GetSecretsAsync(string vault)
    {
        var rows = await Run("param($Vault) Get-SecretInfo -Vault $Vault -Name '*' | ForEach-Object { [pscustomobject]@{ Name = $_.Name; Type = [string]$_.Type; Metadata = $_.Metadata } }",
            Args(("Vault", vault)));
        return rows.Select(r => new SecretEntry(
                Str(r, "Name"),
                Enum.TryParse<SecretKind>(Str(r, "Type"), true, out var k) ? k : SecretKind.Unknown,
                vault,
                ToStringDictionary(Prop(r, "Metadata"))))
            .OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task<SecretValue> GetSecretValueAsync(string vault, string name, SecretKind kind)
    {
        // Leading comma: a byte[] secret must come back as one object, not one object per byte.
        // (Write-Output -NoEnumerate is not an option: in PowerShell 7 it wraps scalars in a List.)
        var rows = await Run("param($Name, $Vault) ,(Get-Secret -Name $Name -Vault $Vault -AsPlainText)",
            Args(("Name", name), ("Vault", vault)));
        var value = rows.FirstOrDefault()?.BaseObject ?? throw new SecretsOperationException($"Secret '{name}' was not found in vault '{vault}'.");
        return value switch
        {
            PSCredential c => new CredentialSecret(c.UserName, Plain(c.Password)),
            byte[] b => new BytesSecret(b),
            IDictionary d => new HashtableSecret(ToStringDictionary(d)),
            // -AsPlainText returns SecureString secrets as [string]; the stored kind decides.
            SecureString s => kind == SecretKind.String ? new TextSecret(Plain(s)) : new SecureTextSecret(Plain(s)),
            string s => kind == SecretKind.SecureString ? new SecureTextSecret(s) : new TextSecret(s),
            _ => new TextSecret(Formatting.ToText(value)),
        };
    }

    public Task SetSecretAsync(string vault, string name, SecretValue value, IReadOnlyDictionary<string, string>? metadata, bool noClobber) =>
        Run("""
            param($Name, $Vault, $Secret, $Metadata, [bool]$NoClobber)
            $p = @{ Name = $Name; Vault = $Vault }
            if ($Secret -is [securestring]) { $p.SecureStringSecret = $Secret } else { $p.Secret = $Secret }
            if ($Metadata -and $Metadata.Count) { $p.Metadata = $Metadata }
            if ($NoClobber) { $p.NoClobber = $true }
            Set-Secret @p
            """, Args(("Name", name), ("Vault", vault), ("Secret", ToPowerShellValue(value)),
                ("Metadata", ToHashtable(metadata)), ("NoClobber", noClobber)));

    public Task SetSecretMetadataAsync(string vault, string name, IReadOnlyDictionary<string, string> metadata) =>
        Run("param($Name, $Vault, $Metadata) Set-SecretInfo -Name $Name -Vault $Vault -Metadata $Metadata",
            Args(("Name", name), ("Vault", vault), ("Metadata", ToHashtable(metadata) ?? new Hashtable())));

    public Task RemoveSecretAsync(string vault, string name) =>
        Run("param($Name, $Vault) Remove-Secret -Name $Name -Vault $Vault", Args(("Name", name), ("Vault", vault)));

    // Shared by rename and copy: value + metadata to a new name/vault, refusing to overwrite.
    const string CopyFunction = """
        function Copy-SgSecret($Name, $From, $To, $NewName) {
            $value = Get-Secret -Name $Name -Vault $From
            $info = Get-SecretInfo -Vault $From -Name ([WildcardPattern]::Escape($Name)) | Where-Object Name -EQ $Name | Select-Object -First 1
            $p = @{ Name = $NewName; Vault = $To; NoClobber = $true }
            if ($value -is [securestring]) { $p.SecureStringSecret = $value } else { $p.Secret = $value }
            if ($info -and $info.Metadata -and $info.Metadata.Count) {
                $md = @{}; foreach ($k in $info.Metadata.Keys) { $md[$k] = $info.Metadata[$k] }; $p.Metadata = $md
            }
            Set-Secret @p
        }

        """;

    public Task RenameSecretAsync(string vault, string name, string newName) =>
        Run("param($Name, $NewName, $Vault)\n" + CopyFunction + """
            Copy-SgSecret -Name $Name -From $Vault -To $Vault -NewName $NewName
            Remove-Secret -Name $Name -Vault $Vault
            """, Args(("Name", name), ("NewName", newName), ("Vault", vault)));

    public Task CopySecretAsync(string sourceVault, string name, string targetVault) =>
        Run("param($Name, $From, $To)\n" + CopyFunction + "Copy-SgSecret -Name $Name -From $From -To $To -NewName $Name",
            Args(("Name", name), ("From", sourceVault), ("To", targetVault)));

    // ---------------------------------------------------------------- SecretStore

    public Task InitializeStoreAsync(string? password, int passwordTimeout)
    {
        if (IsSecretStoreInitialized)
            throw new SecretsOperationException("A SecretStore already exists for this user. Reset it only if you intend to delete all its secrets.");
        return ResetStoreAsync(password, passwordTimeout);
    }

    /// <summary>DESTRUCTIVE: deletes every SecretStore secret. Always creates the store with Interaction=None.</summary>
    public Task ResetStoreAsync(string? password, int passwordTimeout) =>
        RunStore("""
            param($Password, [int]$PasswordTimeout)
            $p = @{ Scope = 'CurrentUser'; PasswordTimeout = $PasswordTimeout; Interaction = 'None'; Force = $true }
            if ($Password) { $p.Authentication = 'Password'; $p.Password = $Password } else { $p.Authentication = 'None' }
            Reset-SecretStore @p -WarningAction SilentlyContinue
            # Without a password (and with Interaction None), Reset-SecretStore only writes storeconfig; the store file is
            # created on first access. Touch the store now so it exists.
            if (-not $Password) { $null = Get-SecretStoreConfiguration }
            """, Args(("Password", string.IsNullOrEmpty(password) ? null : ToSecure(password)), ("PasswordTimeout", passwordTimeout)));

    public async Task<StoreConfig> GetStoreConfigAsync()
    {
        var r = (await RunStore("Get-SecretStoreConfiguration | ForEach-Object { [pscustomobject]@{ Authentication = [string]$_.Authentication; PasswordTimeout = [int]$_.PasswordTimeout; Interaction = [string]$_.Interaction } }")).Single();
        return new StoreConfig(
            Enum.Parse<StoreAuthentication>(Str(r, "Authentication"), true),
            Convert.ToInt32(Prop(r, "PasswordTimeout")),
            Enum.Parse<StoreInteraction>(Str(r, "Interaction"), true));
    }

    /// <summary>Switching None -> Password needs the new password; Password -> None needs the current one.</summary>
    public Task SetStoreConfigAsync(StoreConfig config, string? password) =>
        RunStore("""
            param($Authentication, [int]$PasswordTimeout, $Interaction, $Password)
            $p = @{ Scope = 'CurrentUser'; Authentication = $Authentication; PasswordTimeout = $PasswordTimeout; Interaction = $Interaction; Confirm = $false }
            if ($Password) { $p.Password = $Password }
            Set-SecretStoreConfiguration @p
            """, Args(("Authentication", config.Authentication.ToString()), ("PasswordTimeout", config.PasswordTimeout),
                ("Interaction", config.Interaction.ToString()), ("Password", string.IsNullOrEmpty(password) ? null : ToSecure(password))));

    public Task ChangeStorePasswordAsync(string currentPassword, string newPassword)
    {
        if (string.IsNullOrEmpty(newPassword)) throw new ArgumentException("The new password cannot be empty.", nameof(newPassword));
        return RunStore("param($Password, $NewPassword) Set-SecretStorePassword -Password $Password -NewPassword $NewPassword",
            Args(("Password", ToSecure(currentPassword)), ("NewPassword", ToSecure(newPassword))));
    }

    public Task UnlockStoreAsync(string password) =>
        RunStore("param($Password) Unlock-SecretStore -Password $Password", Args(("Password", ToSecure(password))));

    // ---------------------------------------------------------------- conversions

    static Dictionary<string, object?> Args(params (string Key, object? Value)[] pairs) =>
        pairs.ToDictionary(p => p.Key, p => p.Value);

    static object? Prop(PSObject o, string name) => o.Properties[name]?.Value is { } v ? (v is PSObject p ? p.BaseObject : v) : null;
    static string Str(PSObject o, string name) => Formatting.ToText(Prop(o, name));
    static string? NullIfEmpty(string s) => string.IsNullOrWhiteSpace(s) ? null : s;

    static IReadOnlyDictionary<string, string> ToStringDictionary(object? value)
    {
        var result = new OrderedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (value is PSObject p) value = p.BaseObject;
        if (value is IDictionary d)
            foreach (DictionaryEntry e in d)
            {
                var v = e.Value is PSObject pv ? pv.BaseObject : e.Value;
                result[Formatting.ToText(e.Key)] = v is SecureString ss ? Plain(ss) : Formatting.ToText(v);
            }
        return result;
    }

    static Hashtable? ToHashtable(IReadOnlyDictionary<string, string>? values)
    {
        if (values is null || values.Count == 0) return null;
        var ht = new Hashtable(StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in values) ht[k] = v;
        return ht;
    }

    static object ToPowerShellValue(SecretValue value) => value switch
    {
        TextSecret t => t.Text,
        SecureTextSecret s => ToSecure(s.Text),
        CredentialSecret c => new PSCredential(c.UserName, ToSecure(c.Password)),
        HashtableSecret h => ToHashtable(h.Entries) ?? new Hashtable(),
        BytesSecret b => b.Data,
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    static SecureString ToSecure(string plain)
    {
        var s = new NetworkCredential("", plain).SecurePassword;
        s.MakeReadOnly();
        return s;
    }

    static string Plain(SecureString s) => new NetworkCredential("", s).Password;
}
