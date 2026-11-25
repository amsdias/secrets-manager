using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace SecretsManager.Core;

/// <summary>
/// Supplies the SecretStore password without typing it (Windows Hello in the app). The password is only ever kept
/// sealed: see <see cref="StorePasswordSeal"/>.
/// </summary>
public interface IStorePasswordProvider
{
    /// <summary>Windows Hello is set up on this PC.</summary>
    Task<bool> IsAvailableAsync();
    /// <summary>A sealed password exists.</summary>
    bool IsEnabled { get; }
    /// <summary>Asks for Windows Hello and returns the password; null if the user cancelled.</summary>
    Task<string?> GetPasswordAsync();
    /// <summary>Seals this password (asks for Windows Hello). Replaces an earlier one.</summary>
    Task EnableAsync(string password);
    Task DisableAsync();
}

/// <summary>The sealed password no longer opens: the Windows Hello key was replaced, or the file was changed.</summary>
public sealed class SealMismatchException() : SecretsOperationException(
    "The Windows Hello key no longer matches the saved password. Turn Windows Hello unlock off and on again in Store settings.");

/// <summary>
/// Seals a password with a key that only a Windows Hello signature can produce. A Hello key (TPM-backed RSA) signs a
/// random challenge stored next to the ciphertext; RSA PKCS#1 v1.5 signatures are deterministic, so the same key always
/// gives the same signature. SHA-256 of the signature is the AES-256-GCM key. Without a Hello verification (PIN, face,
/// fingerprint), nothing on the PC, including other programs running as the user, can produce that signature.
/// </summary>
public static class StorePasswordSeal
{
    const string Format = "SecretsManager.StorePasswordSeal";
    const int Version = 1;
    static readonly byte[] Aad = Encoding.UTF8.GetBytes($"{Format}/{Version}");

    public static byte[] NewChallenge() => RandomNumberGenerator.GetBytes(32);

    public static byte[] Seal(string password, byte[] challenge, byte[] signature)
    {
        var key = SHA256.HashData(signature);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var plain = Encoding.UTF8.GetBytes(password);
        var cipher = new byte[plain.Length];
        var tag = new byte[16];
        try
        {
            using var aes = new AesGcm(key, 16);
            aes.Encrypt(nonce, plain, cipher, tag, Aad);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
            CryptographicOperations.ZeroMemory(key);
        }
        return Encoding.UTF8.GetBytes(new JsonObject
        {
            ["format"] = Format, ["version"] = Version,
            ["challenge"] = Convert.ToBase64String(challenge), ["nonce"] = Convert.ToBase64String(nonce),
            ["tag"] = Convert.ToBase64String(tag), ["data"] = Convert.ToBase64String(cipher),
        }.ToJsonString());
    }

    public static byte[] ReadChallenge(byte[] sealedFile) => Convert.FromBase64String(Field(Parse(sealedFile), "challenge"));

    /// <exception cref="SealMismatchException">The signature doesn't open it.</exception>
    public static string Unseal(byte[] sealedFile, byte[] signature)
    {
        var o = Parse(sealedFile);
        var (nonce, tag, cipher) = (Convert.FromBase64String(Field(o, "nonce")), Convert.FromBase64String(Field(o, "tag")), Convert.FromBase64String(Field(o, "data")));
        var key = SHA256.HashData(signature);
        var plain = new byte[cipher.Length];
        try
        {
            using var aes = new AesGcm(key, 16);
            aes.Decrypt(nonce, cipher, tag, plain, Aad);
            return Encoding.UTF8.GetString(plain);
        }
        catch (AuthenticationTagMismatchException) { throw new SealMismatchException(); }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
            CryptographicOperations.ZeroMemory(key);
        }
    }

    static JsonObject Parse(byte[] file)
    {
        try
        {
            var o = JsonNode.Parse(file)?.AsObject();
            if (o is null || (string?)o["format"] != Format || (int?)o["version"] != Version) throw new FormatException();
            return o;
        }
        catch (Exception ex) when (ex is FormatException or System.Text.Json.JsonException or InvalidOperationException)
        {
            throw new SecretsOperationException("The saved Windows Hello unlock data is damaged. Turn Windows Hello unlock off and on again.", ex);
        }
    }

    static string Field(JsonObject o, string name) => (string?)o[name] ?? throw new SecretsOperationException($"The saved Windows Hello unlock data has no '{name}'.");
}
