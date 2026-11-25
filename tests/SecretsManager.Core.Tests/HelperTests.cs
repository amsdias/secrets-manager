using SecretsManager.Core;
using SecretsManager.Core.App;

namespace SecretsManager.Core.Tests;

public class KeyValueTextTests
{
    [Fact]
    public void Parses_skipping_blanks_and_comments_and_splitting_on_first_equals()
    {
        var r = KeyValueText.Parse("a=1\r\n\n# note\n b = x=y ");
        Assert.Equal(["a", "b"], r.Keys);
        Assert.Equal("x=y", r["b"]);
    }

    [Fact]
    public void Rejects_malformed_and_duplicate_lines()
    {
        Assert.Contains("key=value", Assert.Throws<FormatException>(() => KeyValueText.Parse("novalue")).Message);
        Assert.Contains("Duplicate", Assert.Throws<FormatException>(() => KeyValueText.Parse("a=1\nA=2")).Message);
    }
}

public class PasswordGeneratorTests
{
    [Fact]
    public void Honours_length_and_symbol_choice()
    {
        Assert.Equal(40, PasswordGenerator.Generate(40).Length);
        Assert.Matches("^[A-Za-z0-9]{64}$", PasswordGenerator.Generate(64, includeSymbols: false));
        Assert.DoesNotContain('0', PasswordGenerator.Generate(256, includeSymbols: false));
        Assert.NotEqual(PasswordGenerator.Generate(), PasswordGenerator.Generate());
    }
}

public class EditorStateTests
{
    static EditorState E(SecretKind kind) => new() { VaultName = "v", Kind = kind };

    [Fact]
    public void Builds_every_kind()
    {
        Assert.Equal(new TextSecret("abc"), (E(SecretKind.String) with { Text = "abc" }).BuildValue());
        Assert.Equal(new SecureTextSecret("abc"), (E(SecretKind.SecureString) with { Text = "abc" }).BuildValue());
        Assert.Equal(new CredentialSecret("me", "pw"), (E(SecretKind.PSCredential) with { UserName = " me ", Password = "pw" }).BuildValue());
        var h = Assert.IsType<HashtableSecret>((E(SecretKind.Hashtable) with { HashText = "k=v" }).BuildValue());
        Assert.Equal("v", h.Entries["k"]);
        var b = Assert.IsType<BytesSecret>((E(SecretKind.ByteArray) with { BytesBase64 = "AQID\r\n" }).BuildValue());
        Assert.Equal([1, 2, 3], b.Data);
    }

    [Theory]
    [InlineData(SecretKind.String)]
    [InlineData(SecretKind.SecureString)]
    [InlineData(SecretKind.PSCredential)]
    [InlineData(SecretKind.Hashtable)]
    [InlineData(SecretKind.ByteArray)]
    public void Rejects_empty_input(SecretKind kind) => Assert.Throws<FormatException>(() => E(kind).BuildValue());

    [Fact]
    public void Rejects_invalid_base64() =>
        Assert.Contains("Base64", Assert.Throws<FormatException>(() => (E(SecretKind.ByteArray) with { BytesBase64 = "not base64!" }).BuildValue()).Message);

    [Fact]
    public void Round_trips_an_existing_secret()
    {
        var entry = new SecretEntry("db", SecretKind.PSCredential, "v", new Dictionary<string, string> { ["env"] = "prod" });
        var e = EditorState.ForExisting(entry, new CredentialSecret("sa", "pw"));
        Assert.False(e.IsNew);
        Assert.Equal("env=prod", e.MetadataText);
        Assert.Equal(new CredentialSecret("sa", "pw"), e.BuildValue());
    }
}

public class LockedRetryTests
{
    [Fact]
    public async Task Prompts_unlocks_and_retries()
    {
        var unlocked = false; var prompts = 0;
        var r = await LockedRetry.RunAsync(
            () => unlocked ? Task.FromResult("done") : throw new VaultLockedException("A valid password is required"),
            "v", (_, _) => { prompts++; return Task.FromResult<string?>("pw"); },
            _ => { unlocked = true; return Task.CompletedTask; });
        Assert.Equal("done", r);
        Assert.Equal(1, prompts);
    }

    [Fact]
    public async Task Wrong_password_reprompts_then_gives_up_after_max_prompts()
    {
        var prompts = 0;
        await Assert.ThrowsAsync<VaultLockedException>(() => LockedRetry.RunAsync(
            () => throw new VaultLockedException("locked"), "v",
            (_, _) => { prompts++; return Task.FromResult<string?>("wrong"); },
            _ => throw new SecretsOperationException("The provided password is incorrect"), maxPrompts: 3));
        Assert.Equal(3, prompts);
    }

    [Fact]
    public async Task Cancel_throws_OperationCanceled() =>
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => LockedRetry.RunAsync(
            () => throw new VaultLockedException("locked"), "v", (_, _) => Task.FromResult<string?>(null), _ => Task.CompletedTask));

    [Fact]
    public async Task Other_errors_do_not_prompt()
    {
        var prompts = 0;
        await Assert.ThrowsAsync<SecretsOperationException>(() => LockedRetry.RunAsync(
            () => throw new SecretsOperationException("boom"), "v",
            (_, _) => { prompts++; return Task.FromResult<string?>("x"); }, _ => Task.CompletedTask));
        Assert.Equal(0, prompts);
    }

    [Theory]
    [InlineData("A valid password is required to access the Microsoft.PowerShell.SecretStore vault.", true)]
    [InlineData("Use the Unlock-SecretStore cmdlet to provide the required password to access the store.", true)]
    [InlineData("Secret not found", false)]
    public void Recognises_SecretStore_locked_messages(string message, bool locked) =>
        Assert.Equal(locked, VaultLockedException.IsLockedMessage(message));
}
