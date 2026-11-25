namespace SecretsManager.Core;

/// <summary>Callback that asks the user for a vault password. Returns null when the user cancels.</summary>
public delegate Task<string?> PasswordProvider(string vaultName, int attempt);

public static class LockedRetry
{
    /// <summary>
    /// Runs <paramref name="operation"/>; when it fails because the vault is locked, asks for the
    /// password, unlocks and retries. A wrong password just leads to another prompt (up to
    /// <paramref name="maxPrompts"/>). Cancelling throws <see cref="OperationCanceledException"/>.
    /// </summary>
    public static async Task<T> RunAsync<T>(
        Func<Task<T>> operation,
        string vaultName,
        PasswordProvider? passwordProvider,
        Func<string, Task> unlock,
        int maxPrompts = 3)
    {
        for (var attempt = 1; ; attempt++)
        {
            try { return await operation(); }
            catch (VaultLockedException) when (passwordProvider is not null && attempt <= maxPrompts)
            {
                var password = await passwordProvider(vaultName, attempt);
                if (password is null) throw new OperationCanceledException($"Unlock of vault '{vaultName}' was cancelled.");
                try { await unlock(password); }
                catch (SecretsOperationException) { /* wrong password: the retry fails as locked and re-prompts */ }
            }
        }
    }

    public static Task RunAsync(Func<Task> operation, string vaultName, PasswordProvider? passwordProvider, Func<string, Task> unlock, int maxPrompts = 3) =>
        RunAsync(async () => { await operation(); return true; }, vaultName, passwordProvider, unlock, maxPrompts);
}
