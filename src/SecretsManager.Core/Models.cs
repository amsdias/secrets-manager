using System.Globalization;

namespace SecretsManager.Core;

/// <summary>Secret types supported by SecretManagement (mirrors Microsoft.PowerShell.SecretManagement.SecretType).</summary>
public enum SecretKind { String, SecureString, PSCredential, Hashtable, ByteArray, Unknown }

public sealed record VaultInfo(
    string Name,
    string ModuleName,
    bool IsDefault,
    string? Description,
    IReadOnlyDictionary<string, string> Parameters)
{
    public const string SecretStoreModule = "Microsoft.PowerShell.SecretStore";

    public const string WindowsVaultModule = "SecretsManager.WindowsVault";

    public bool IsSecretStore =>
        ModuleName.Equals(SecretStoreModule, StringComparison.OrdinalIgnoreCase) ||
        ModuleName.Replace('/', '\\').Contains("\\" + SecretStoreModule, StringComparison.OrdinalIgnoreCase);

    /// <summary>The app's own DPAPI vault (src/WindowsVault): readable without a password or prompt.</summary>
    public bool IsWindowsVault => ModuleName.Contains(WindowsVaultModule, StringComparison.OrdinalIgnoreCase);

    public string KindLabel => IsSecretStore ? "SecretStore" : IsWindowsVault ? "Windows account" : ModuleName;
}

/// <summary>A secret's listing entry. Never carries the secret value.</summary>
public sealed record SecretEntry(string Name, SecretKind Kind, string VaultName, IReadOnlyDictionary<string, string> Metadata)
{
    public string MetadataText => KeyValueText.Format(Metadata, "; ");
}

/// <summary>A secret value, held as plain text in memory while the app works with it.</summary>
public abstract record SecretValue
{
    public abstract SecretKind Kind { get; }
    /// <summary>Human-readable rendering for the viewer.</summary>
    public abstract string DisplayText { get; }
    /// <summary>What "Copy" puts on the clipboard: the password for credentials, the value otherwise.</summary>
    public virtual string CopyText => DisplayText;
}

public sealed record TextSecret(string Text) : SecretValue
{
    public override SecretKind Kind => SecretKind.String;
    public override string DisplayText => Text;
}

public sealed record SecureTextSecret(string Text) : SecretValue
{
    public override SecretKind Kind => SecretKind.SecureString;
    public override string DisplayText => Text;
}

public sealed record CredentialSecret(string UserName, string Password) : SecretValue
{
    public override SecretKind Kind => SecretKind.PSCredential;
    public override string DisplayText => $"UserName: {UserName}{Environment.NewLine}Password: {Password}";
    public override string CopyText => Password;
}

public sealed record HashtableSecret(IReadOnlyDictionary<string, string> Entries) : SecretValue
{
    public override SecretKind Kind => SecretKind.Hashtable;
    public override string DisplayText => KeyValueText.Format(Entries, Environment.NewLine);
}

public sealed record BytesSecret(byte[] Data) : SecretValue
{
    public override SecretKind Kind => SecretKind.ByteArray;
    public override string DisplayText => Convert.ToBase64String(Data, Base64FormattingOptions.InsertLineBreaks);
    public override string CopyText => Convert.ToBase64String(Data);
}

public enum StoreAuthentication { Password, None }
public enum StoreInteraction { None, Prompt }

public sealed record StoreConfig(StoreAuthentication Authentication, int PasswordTimeout, StoreInteraction Interaction);

internal static class Formatting
{
    /// <summary>Metadata / hashtable values come back as object; render them stably.</summary>
    public static string ToText(object? value) => value switch
    {
        null => "",
        DateTime d => d.ToString("o", CultureInfo.InvariantCulture),
        DateTimeOffset d => d.ToString("o", CultureInfo.InvariantCulture),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? "",
    };
}
