namespace SecretsManager.Core;

/// <summary>A SecretManagement / SecretStore operation failed. Message is the PowerShell error text.</summary>
public class SecretsOperationException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>The vault (or SecretStore) needs a password before the operation can run.</summary>
public sealed class VaultLockedException(string message, Exception? inner = null) : SecretsOperationException(message, inner)
{
    // Messages SecretStore / SecretManagement emit when a password is needed, or when a cmdlet
    // tried to prompt on a host that can't (this app hosts PowerShell without an interactive UI).
    static readonly string[] Patterns =
    [
        "A valid password is required",
        "A password is required for",
        "Use the Unlock-SecretStore cmdlet",
        "Unlock-SecretVault",
        "NonInteractive mode",
        "vault is locked",
        "not implemented by the host", // prompt attempted on a host without UI
    ];

    public static bool IsLockedMessage(string? message) =>
        message is not null && Patterns.Any(p => message.Contains(p, StringComparison.OrdinalIgnoreCase));
}
