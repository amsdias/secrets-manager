using SecretsManager.Core;
using Windows.Security.Credentials;
using Windows.Security.Cryptography;

namespace SecretsManager.App;

/// <summary>
/// Windows Hello unlock for SecretStore: a Hello key (KeyCredentialManager, TPM-backed) signs the challenge saved in the
/// seal file, and only that signature opens the sealed password (see StorePasswordSeal). Every GetPasswordAsync asks for
/// Windows Hello.
/// </summary>
sealed class WindowsHelloPasswordProvider(string sealPath) : IStorePasswordProvider
{
    const string KeyName = "SecretsManager.SecretStore";

    public static string DefaultSealPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SecretsManager", "secretstore.hello");

    public async Task<bool> IsAvailableAsync()
    {
        try { return await KeyCredentialManager.IsSupportedAsync(); }
        catch (Exception) { return false; }
    }

    public bool IsEnabled => File.Exists(sealPath);

    public async Task<string?> GetPasswordAsync()
    {
        if (!IsEnabled) return null;
        var file = await File.ReadAllBytesAsync(sealPath);
        var opened = await KeyCredentialManager.OpenAsync(KeyName);
        if (opened.Status != KeyCredentialStatus.Success) throw new SecretsOperationException(Describe(opened.Status));
        var signature = await SignAsync(opened.Credential, StorePasswordSeal.ReadChallenge(file));
        return signature is null ? null : StorePasswordSeal.Unseal(file, signature);
    }

    public async Task EnableAsync(string password)
    {
        var created = await KeyCredentialManager.RequestCreateAsync(KeyName, KeyCredentialCreationOption.ReplaceExisting);
        if (created.Status == KeyCredentialStatus.UserCanceled) throw new OperationCanceledException();
        if (created.Status != KeyCredentialStatus.Success) throw new SecretsOperationException(Describe(created.Status));
        var challenge = StorePasswordSeal.NewChallenge();
        var signature = await SignAsync(created.Credential, challenge) ?? throw new OperationCanceledException();
        Directory.CreateDirectory(Path.GetDirectoryName(sealPath)!);
        await File.WriteAllBytesAsync(sealPath, StorePasswordSeal.Seal(password, challenge, signature));
    }

    public async Task DisableAsync()
    {
        File.Delete(sealPath);
        try { await KeyCredentialManager.DeleteAsync(KeyName); }
        catch (Exception) { /* no key: nothing to delete */ }
    }

    static async Task<byte[]?> SignAsync(KeyCredential credential, byte[] challenge)
    {
        var result = await credential.RequestSignAsync(CryptographicBuffer.CreateFromByteArray(challenge));
        if (result.Status == KeyCredentialStatus.UserCanceled) return null;
        if (result.Status != KeyCredentialStatus.Success) throw new SecretsOperationException(Describe(result.Status));
        CryptographicBuffer.CopyToByteArray(result.Result, out var bytes);
        return bytes;
    }

    static string Describe(KeyCredentialStatus status) => status switch
    {
        KeyCredentialStatus.NotFound => "The Windows Hello key for SecretStore is missing. Turn Windows Hello unlock off and on again in Store settings.",
        KeyCredentialStatus.UserPrefersPassword => "Windows Hello was declined; enter the password instead.",
        KeyCredentialStatus.SecurityDeviceLocked => "The security device (TPM) is locked. Try again later.",
        KeyCredentialStatus.CredentialAlreadyExists => "A Windows Hello key already exists.",
        _ => $"Windows Hello failed ({status}).",
    };
}
