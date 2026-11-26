using System.Text.Json;

namespace SecretsManager.Core;

/// <summary>
/// The command line: "secrets get &lt;name&gt;" prints a secret's value and "secrets list" the names (never values), for
/// scripts and tools such as Claude Code. secrets.exe relays to "SecretsManager.exe get|list".
/// Vaults without a password (Windows-account vaults, a SecretStore without a password) answer silently. A SecretStore
/// with a password is unlocked through <see cref="IStorePasswordProvider"/> (Windows Hello), so every read asks.
/// Without --vault, every other vault is searched before SecretStore, so finding a dev key never triggers Windows Hello.
/// </summary>
public static class SecretsCli
{
    public const int Found = 0, NotFound = 1, Denied = 2, Failed = 3;

    public const string Usage = """
        Usage: secrets get <name> [--vault <vault>] [--field password|username]
               secrets list [<filter>] [--vault <vault>] [--json]

        get   Prints the value of a secret to standard output (no trailing newline).
                String, SecureString  the text
                PSCredential          the password (--field username for the user name)
                Hashtable             JSON
                ByteArray             Base64
              Without --vault, vaults that need no password are searched first, then SecretStore.
              A SecretStore with a password asks for Windows Hello on every read (turn it on in the app: Store settings).
              A secret whose "expires" metadata date has passed (or is near) still prints, with a warning on stderr.

        list  Names, vaults, types and metadata of secrets; never their values. <filter> matches part of the name.
              Without --vault, a locked SecretStore is skipped without asking; --vault <name> lists it (Windows Hello).
              --json prints [{name, vault, type, metadata, expires, expiry}].

        Exit codes: 0 found, 1 not found, 2 cancelled or locked, 3 error.
        Use the value inside a command instead of printing it, e.g. in PowerShell:
          $env:API_KEY = secrets get api-key; ./deploy.ps1
          or in bash: API_KEY="$(secrets get api-key)" ./deploy.sh
        """;

    public static async Task<int> RunAsync(IReadOnlyList<string> args, ISecretsService secrets, IStorePasswordProvider? storePassword,
        TextWriter stdout, TextWriter stderr, DateOnly? today = null)
    {
        if (args.Count == 0 || args[0] is "-h" or "--help" or "help" or "/?") { stdout.WriteLine(Usage); return Found; }
        if (args[0] is not ("get" or "list")) { stderr.WriteLine($"Unknown command '{args[0]}'.{Environment.NewLine}{Usage}"); return Failed; }
        var list = args[0] == "list";

        string? name = null, vaultName = null, field = null;
        var json = false;
        for (var i = 1; i < args.Count; i++)
        {
            switch (args[i])
            {
                case "--vault" when i + 1 < args.Count: vaultName = args[++i]; break;
                case "--field" when i + 1 < args.Count && !list: field = args[++i].ToLowerInvariant(); break;
                case "--json" when list: json = true; break;
                case var a when !a.StartsWith("--") && name is null: name = a; break;
                default: stderr.WriteLine($"Unexpected argument '{args[i]}'.{Environment.NewLine}{Usage}"); return Failed;
            }
        }
        if (name is null && !list) { stderr.WriteLine(Usage); return Failed; }
        if (field is not (null or "password" or "username")) { stderr.WriteLine("--field must be password or username."); return Failed; }
        var day = today ?? DateOnly.FromDateTime(DateTime.Now);

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
            return list ? await ListAsync(candidates) : await GetAsync(candidates, name!);
        }
        catch (OperationCanceledException) { stderr.WriteLine("Cancelled."); return Denied; }
        catch (VaultLockedException ex) { stderr.WriteLine(ex.Message); return Denied; }
        catch (SecretsOperationException ex) { stderr.WriteLine(ex.Message); return Failed; }

        async Task<int> GetAsync(IEnumerable<VaultInfo> candidates, string name)
        {
            foreach (var vault in candidates)
            {
                if (await EntriesOf(vault, skipLockedStore: false) is not { } entries) continue;
                var entry = entries.FirstOrDefault(e => e.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                if (entry is null) continue;
                var value = await Unlocked(vault, () => secrets.GetSecretValueAsync(vault.Name, entry.Name, entry.Kind));
                if (SecretExpiry.Of(entry.Metadata, day) is { Warning: { } warning } expiry)
                    stderr.WriteLine($"Warning: secret '{entry.Name}' {warning} ({expiry.Date:yyyy-MM-dd}).");
                stdout.Write(Format(value, field));
                stdout.Flush();
                return Found;
            }
            stderr.WriteLine(vaultName is null ? $"Secret '{name}' was not found." : $"Secret '{name}' was not found in vault '{vaultName}'.");
            return NotFound;
        }

        async Task<int> ListAsync(IEnumerable<VaultInfo> candidates)
        {
            var rows = new List<SecretEntry>();
            foreach (var vault in candidates)
                if (await EntriesOf(vault, skipLockedStore: vaultName is null) is { } entries)
                    rows.AddRange(entries.Where(e => name is null || e.Name.Contains(name, StringComparison.OrdinalIgnoreCase)));

            if (json)
            {
                stdout.Write(JsonSerializer.Serialize(rows.Select(e =>
                {
                    var expiry = SecretExpiry.Of(e.Metadata, day);
                    return new Dictionary<string, object?>
                    {
                        ["name"] = e.Name, ["vault"] = e.VaultName, ["type"] = e.Kind.ToString(), ["metadata"] = e.Metadata,
                        ["expires"] = expiry.State == ExpiryState.None ? null : expiry.Date.ToString("yyyy-MM-dd"),
                        ["expiry"] = expiry.State == ExpiryState.None ? null : expiry.State.ToString().ToLowerInvariant(),
                    };
                })));
                stdout.Flush();
                return Found;
            }
            if (rows.Count == 0) { stderr.WriteLine("No secrets found."); return Found; }
            string[][] table =
            [
                ["NAME", "VAULT", "TYPE", "METADATA"],
                .. rows.Select(e => new[]
                {
                    e.Name, e.VaultName, e.Kind.ToString(),
                    e.MetadataText + (SecretExpiry.Of(e.Metadata, day).Warning is { } w ? $"  ({w})" : ""),
                }),
            ];
            var widths = Enumerable.Range(0, 3).Select(c => table.Max(r => r[c].Length)).ToArray();
            foreach (var r in table)
                stdout.WriteLine(string.Join("  ", r.Take(3).Select((cell, c) => cell.PadRight(widths[c]))) + "  " + r[3]);
            stdout.Flush();
            return Found;
        }

        // Null when the vault is skipped: a locked vault while searching every vault (other than SecretStore for get,
        // which unlocks it with Windows Hello), or a locked SecretStore while listing every vault (listing never asks).
        async Task<IReadOnlyList<SecretEntry>?> EntriesOf(VaultInfo vault, bool skipLockedStore)
        {
            try
            {
                if (skipLockedStore && vault.IsSecretStore) return await secrets.GetSecretsAsync(vault.Name);
                return await Unlocked(vault, () => secrets.GetSecretsAsync(vault.Name));
            }
            catch (VaultLockedException) when (vaultName is null && vault.IsSecretStore && skipLockedStore)
            {
                stderr.WriteLine($"Skipped vault '{vault.Name}': SecretStore is locked. 'secrets list --vault {vault.Name}' lists it (asks for Windows Hello).");
                return null;
            }
            catch (VaultLockedException ex) when (vaultName is null && !vault.IsSecretStore)
            {
                stderr.WriteLine($"Skipped locked vault '{vault.Name}': {ex.Message}");
                return null;
            }
        }

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
