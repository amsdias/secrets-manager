using System.Text.Json;

namespace SecretsManager.Core;

/// <summary>
/// The command line: "SecretsManager.exe get &lt;name&gt;" prints a secret's value, for scripts and tools such as Claude Code.
/// Vaults without a password (Windows-account vaults, a SecretStore without a password) answer silently. A SecretStore
/// with a password is unlocked through <see cref="IStorePasswordProvider"/> (Windows Hello), so every read asks.
/// Without --vault, every other vault is searched before SecretStore, so finding a dev key never triggers Windows Hello.
/// </summary>
public static class SecretsCli
{
    public const int Found = 0, NotFound = 1, Denied = 2, Failed = 3;

    public const string Usage = """
        Usage: SecretsManager.exe get <name> [--vault <vault>] [--field password|username]

        Prints the value of a secret to standard output (no trailing newline).
          String, SecureString  the text
          PSCredential          the password (--field username for the user name)
          Hashtable             JSON
          ByteArray             Base64

        Without --vault, vaults that need no password are searched first, then SecretStore.
        A SecretStore with a password asks for Windows Hello on every read (turn it on in the app: Store settings).

        Exit codes: 0 found, 1 not found, 2 cancelled or locked, 3 error.
        Use the value inside a command instead of printing it, e.g. in PowerShell:
          $env:API_KEY = SecretsManager.exe get api-key; ./deploy.ps1
        """;

    public static async Task<int> RunAsync(IReadOnlyList<string> args, ISecretsService secrets, IStorePasswordProvider? storePassword,
        TextWriter stdout, TextWriter stderr)
    {
        if (args.Count == 0 || args[0] is "-h" or "--help" or "help" or "/?") { stdout.WriteLine(Usage); return Found; }
        if (args[0] != "get") { stderr.WriteLine($"Unknown command '{args[0]}'.{Environment.NewLine}{Usage}"); return Failed; }

        string? name = null, vaultName = null, field = null;
        for (var i = 1; i < args.Count; i++)
        {
            switch (args[i])
            {
                case "--vault" when i + 1 < args.Count: vaultName = args[++i]; break;
                case "--field" when i + 1 < args.Count: field = args[++i].ToLowerInvariant(); break;
                case var a when !a.StartsWith("--") && name is null: name = a; break;
                default: stderr.WriteLine($"Unexpected argument '{args[i]}'.{Environment.NewLine}{Usage}"); return Failed;
            }
        }
        if (name is null) { stderr.WriteLine(Usage); return Failed; }
        if (field is not (null or "password" or "username")) { stderr.WriteLine("--field must be password or username."); return Failed; }

        try
        {
            var vaults = await secrets.GetVaultsAsync();
            IEnumerable<VaultInfo> candidates;
            if (vaultName is not null)
            {
                candidates = [vaults.FirstOrDefault(v => v.Name.Equals(vaultName, StringComparison.OrdinalIgnoreCase))
                              ?? throw new SecretsOperationException($"There is no vault named '{vaultName}'.")];
            }
            else
            {
                candidates = vaults.OrderBy(v => v.IsSecretStore).ThenByDescending(v => v.IsDefault).ThenBy(v => v.Name);
            }

            foreach (var vault in candidates)
            {
                IReadOnlyList<SecretEntry> entries;
                try { entries = await Unlocked(vault, () => secrets.GetSecretsAsync(vault.Name)); }
                catch (VaultLockedException ex) when (vaultName is null && !vault.IsSecretStore)
                {
                    stderr.WriteLine($"Skipped locked vault '{vault.Name}': {ex.Message}");
                    continue;
                }
                var entry = entries.FirstOrDefault(e => e.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                if (entry is null) continue;
                var value = await Unlocked(vault, () => secrets.GetSecretValueAsync(vault.Name, entry.Name, entry.Kind));
                stdout.Write(Format(value, field));
                stdout.Flush();
                return Found;
            }
            stderr.WriteLine(vaultName is null ? $"Secret '{name}' was not found." : $"Secret '{name}' was not found in vault '{vaultName}'.");
            return NotFound;
        }
        catch (OperationCanceledException) { stderr.WriteLine("Cancelled."); return Denied; }
        catch (VaultLockedException ex) { stderr.WriteLine(ex.Message); return Denied; }
        catch (SecretsOperationException ex) { stderr.WriteLine(ex.Message); return Failed; }

        async Task<T> Unlocked<T>(VaultInfo vault, Func<Task<T>> op)
        {
            try { return await op(); }
            catch (VaultLockedException) when (vault.IsSecretStore)
            {
                if (storePassword is not { IsEnabled: true })
                    throw new VaultLockedException("SecretStore is locked. To read it from the command line, turn on " +
                        "'Unlock with Windows Hello' in the app (Store settings), or keep the secret in a vault without a password.");
                var password = await storePassword.GetPasswordAsync() ?? throw new OperationCanceledException();
                await secrets.UnlockStoreAsync(password);
                return await op();
            }
        }
    }

    public static string Format(SecretValue value, string? field = null) => value switch
    {
        CredentialSecret c => field == "username" ? c.UserName : c.Password,
        HashtableSecret h => JsonSerializer.Serialize(h.Entries),
        BytesSecret b => Convert.ToBase64String(b.Data),
        _ => value.DisplayText,
    };
}
