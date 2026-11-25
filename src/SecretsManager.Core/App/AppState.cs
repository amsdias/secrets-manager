using System.Collections.Immutable;

namespace SecretsManager.Core.App;

public enum PromptKind { Password, NewPassword, ChangePassword, Confirm, Text, Choice }

/// <summary>A modal question the UI must answer by calling <see cref="AppStore.CompletePrompt"/>.</summary>
public sealed record PromptRequest(PromptKind Kind, string Title, string Message)
{
    public int Id { get; init; }
    public string PrimaryText { get; init; } = "OK";
    public bool IsDanger { get; init; }
    public string? InitialText { get; init; }
    /// <summary>NewPassword: label of the "don't use a password" checkbox (null = no checkbox).</summary>
    public string? OptionText { get; init; }
    public IReadOnlyList<string> Choices { get; init; } = [];
    /// <summary>Text: the primary button stays disabled until the input equals this (case-sensitive).</summary>
    public string? RequiredText { get; init; }
}

public sealed record PromptResult(string? Text = null, string? Password = null, string? CurrentPassword = null, bool Option = false, int ChoiceIndex = -1);

public sealed record EditorState
{
    public required string VaultName { get; init; }
    /// <summary>Null when creating a new secret.</summary>
    public string? OriginalName { get; init; }
    public string Name { get; init; } = "";
    public SecretKind Kind { get; init; } = SecretKind.SecureString;
    public string Text { get; init; } = "";
    public string UserName { get; init; } = "";
    public string Password { get; init; } = "";
    public string HashText { get; init; } = "";
    public string BytesBase64 { get; init; } = "";
    public string BytesInfo { get; init; } = "";
    public string MetadataText { get; init; } = "";
    public string OriginalMetadataText { get; init; } = "";
    public bool Reveal { get; init; }
    public bool IsSaving { get; init; }
    public string? Error { get; init; }

    public bool IsNew => OriginalName is null;

    public static IReadOnlyList<SecretKind> EditableKinds { get; } =
        [SecretKind.String, SecretKind.SecureString, SecretKind.PSCredential, SecretKind.Hashtable, SecretKind.ByteArray];

    public static EditorState ForExisting(SecretEntry entry, SecretValue value)
    {
        var metadata = KeyValueText.Format(entry.Metadata, Environment.NewLine);
        var state = new EditorState
        {
            VaultName = entry.VaultName, OriginalName = entry.Name, Name = entry.Name, Kind = value.Kind,
            MetadataText = metadata, OriginalMetadataText = metadata,
        };
        return value switch
        {
            TextSecret t => state with { Text = t.Text },
            SecureTextSecret s => state with { Text = s.Text },
            CredentialSecret c => state with { UserName = c.UserName, Password = c.Password },
            HashtableSecret h => state with { HashText = KeyValueText.Format(h.Entries, Environment.NewLine) },
            BytesSecret b => state with { BytesBase64 = Convert.ToBase64String(b.Data, Base64FormattingOptions.InsertLineBreaks), BytesInfo = $"{b.Data.Length} bytes" },
            _ => state,
        };
    }

    /// <summary>Validates the form and builds the value to store. Throws with a user-facing message.</summary>
    public SecretValue BuildValue() => Kind switch
    {
        SecretKind.String => string.IsNullOrEmpty(Text) ? throw new FormatException("A String secret cannot be empty.") : new TextSecret(Text),
        SecretKind.SecureString => string.IsNullOrEmpty(Text) ? throw new FormatException("A SecureString secret cannot be empty.") : new SecureTextSecret(Text),
        SecretKind.PSCredential => string.IsNullOrWhiteSpace(UserName) ? throw new FormatException("A PSCredential secret needs a user name.") : new CredentialSecret(UserName.Trim(), Password),
        SecretKind.Hashtable => KeyValueText.Parse(HashText) is { Count: > 0 } h ? new HashtableSecret(h) : throw new FormatException("A Hashtable secret needs at least one key=value line."),
        SecretKind.ByteArray => new BytesSecret(DecodeBase64(BytesBase64)),
        _ => throw new FormatException($"Secrets of type {Kind} cannot be edited."),
    };

    static byte[] DecodeBase64(string text)
    {
        var compact = string.Concat(text.Where(c => !char.IsWhiteSpace(c)));
        if (compact.Length == 0) throw new FormatException("A ByteArray secret needs Base64 content (or load a file).");
        try { return Convert.FromBase64String(compact); }
        catch (FormatException) { throw new FormatException("The ByteArray content is not valid Base64."); }
    }
}

public sealed record ViewerState(SecretEntry Entry, SecretValue Value, bool Reveal = false);

public sealed record VaultFormState
{
    public string Name { get; init; } = "";
    public string ModuleName { get; init; } = VaultInfo.SecretStoreModule;
    public string Description { get; init; } = "";
    public string ParametersText { get; init; } = "";
    public bool MakeDefault { get; init; }
    public bool IsSaving { get; init; }
    public string? Error { get; init; }
}

public sealed record StoreSettingsState
{
    public required StoreConfig Original { get; init; }
    public StoreAuthentication Authentication { get; init; }
    public string TimeoutText { get; init; } = "900";
    public StoreInteraction Interaction { get; init; }
    public bool IsSaving { get; init; }
    public string? Error { get; init; }
}

public sealed record AppState
{
    public ImmutableArray<VaultInfo> Vaults { get; init; } = [];
    public string? SelectedVault { get; init; }
    public ImmutableArray<SecretEntry> Secrets { get; init; } = [];
    public string Search { get; init; } = "";
    public string? SelectedSecret { get; init; }
    public bool IsVaultLocked { get; init; }
    /// <summary>The selected vault uses SecretStore, but the store itself has not been created yet.</summary>
    public bool IsStoreMissing { get; init; }
    /// <summary>SecretStore authentication mode, known once a SecretStore vault has loaded (null = unknown).</summary>
    public StoreAuthentication? StoreAuthentication { get; init; }
    public bool HasLoaded { get; init; }
    public int BusyCount { get; init; }
    public string Status { get; init; } = "Starting…";
    public string? Error { get; init; }
    public bool SecretStoreAvailable { get; init; }
    public int? ClipboardSecondsLeft { get; init; }

    public EditorState? Editor { get; init; }
    public ViewerState? Viewer { get; init; }
    public VaultFormState? VaultForm { get; init; }
    public StoreSettingsState? StoreSettings { get; init; }
    public PromptRequest? Prompt { get; init; }

    public bool IsBusy => BusyCount > 0;
    public VaultInfo? CurrentVault => Vaults.FirstOrDefault(v => v.Name == SelectedVault);
    public SecretEntry? CurrentSecret => Secrets.FirstOrDefault(s => s.Name == SelectedSecret);
    /// <summary>Unlock makes no sense for a SecretStore without a password, or one that hasn't been created yet.</summary>
    public bool CanUnlock => CurrentVault is { } v &&
        !(v.IsSecretStore && (IsStoreMissing || StoreAuthentication == Core.StoreAuthentication.None));

    public bool HasPanel => Editor is not null || Viewer is not null || VaultForm is not null || StoreSettings is not null;

    public IReadOnlyList<SecretEntry> VisibleSecrets =>
        string.IsNullOrWhiteSpace(Search)
            ? Secrets
            : Secrets.Where(s => s.Name.Contains(Search.Trim(), StringComparison.OrdinalIgnoreCase) ||
                                 s.MetadataText.Contains(Search.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();

    /// <summary>Message for the empty secrets area, or null when the list has rows.</summary>
    public string? EmptyMessage =>
        !HasLoaded ? "Loading…" :
        Vaults.IsEmpty ? (SecretStoreAvailable
            ? "No vaults are registered yet. Click \"Register…\" to create a SecretStore vault."
            : "No vaults are registered and Microsoft.PowerShell.SecretStore is not installed.") :
        SelectedVault is null ? "Select a vault." :
        IsStoreMissing ? "The SecretStore for this vault hasn't been created yet. Click Store settings to create it." :
        IsVaultLocked ? "This vault is locked. Click \"Unlock…\" or press F5 to enter its password." :
        Secrets.IsEmpty ? "This vault has no secrets yet. Click \"New secret\" (Ctrl+N) to add one." :
        VisibleSecrets.Count == 0 ? $"No secrets match '{Search.Trim()}'." :
        null;
}
