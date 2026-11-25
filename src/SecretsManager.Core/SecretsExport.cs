using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SecretsManager.Core;

/// <summary>One secret in an export file: its value and (unencrypted-in-the-vault) metadata.</summary>
public sealed record ExportedSecret(string Name, SecretValue Value, IReadOnlyDictionary<string, string> Metadata);

/// <summary>The export file opened, but with the wrong password, or it has been modified since it was written.</summary>
public sealed class ExportPasswordException() : SecretsOperationException("The password is wrong, or the file has been changed since it was exported.");

/// <summary>
/// Password-encrypted export files. The whole payload (names, values and metadata) is encrypted with AES-256-GCM
/// under a key derived from the password with PBKDF2-SHA256. The envelope's format, version and iteration count are
/// authenticated as associated data, so they can't be altered without the password check failing.
/// </summary>
public static class SecretsExport
{
    public const string FileExtension = ".smsecrets";
    public const int DefaultIterations = 600_000; // OWASP 2023 guidance for PBKDF2-HMAC-SHA256
    const string Format = "SecretsManager.Export";
    const int Version = 1, SaltBytes = 16, NonceBytes = 12, TagBytes = 16, KeyBytes = 32;
    const int MinIterations = 1_000, MaxIterations = 10_000_000; // a damaged file must not hang the app

    public static byte[] Encrypt(IReadOnlyList<ExportedSecret> secrets, string password, int iterations = DefaultIterations)
    {
        ArgumentException.ThrowIfNullOrEmpty(password);
        ArgumentOutOfRangeException.ThrowIfLessThan(iterations, MinIterations);
        var plain = Encoding.UTF8.GetBytes(new JsonArray(secrets.Select(ToJson).ToArray<JsonNode?>()).ToJsonString());
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var nonce = RandomNumberGenerator.GetBytes(NonceBytes);
        var key = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, KeyBytes);
        var cipher = new byte[plain.Length];
        var tag = new byte[TagBytes];
        try
        {
            using var aes = new AesGcm(key, TagBytes);
            aes.Encrypt(nonce, plain, cipher, tag, AssociatedData(iterations));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
            CryptographicOperations.ZeroMemory(key);
        }
        var envelope = new JsonObject
        {
            ["format"] = Format, ["version"] = Version, ["kdf"] = "PBKDF2-SHA256", ["iterations"] = iterations,
            ["salt"] = Convert.ToBase64String(salt), ["nonce"] = Convert.ToBase64String(nonce),
            ["tag"] = Convert.ToBase64String(tag), ["data"] = Convert.ToBase64String(cipher),
        };
        return Encoding.UTF8.GetBytes(envelope.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <exception cref="ExportPasswordException">Wrong password, or the file was modified.</exception>
    /// <exception cref="SecretsOperationException">The file isn't a (supported) export file.</exception>
    public static IReadOnlyList<ExportedSecret> Decrypt(byte[] file, string password)
    {
        JsonObject envelope;
        int iterations;
        byte[] salt, nonce, tag, cipher;
        try
        {
            envelope = JsonNode.Parse(file)?.AsObject() ?? throw new FormatException();
            if ((string?)envelope["format"] != Format) throw new FormatException();
            if ((int?)envelope["version"] != Version)
                throw new SecretsOperationException($"This export file was written by a newer version of the app (format version {envelope["version"]}).");
            iterations = (int?)envelope["iterations"] ?? throw new FormatException();
            if (iterations is < MinIterations or > MaxIterations || (string?)envelope["kdf"] != "PBKDF2-SHA256") throw new FormatException();
            salt = Bytes(envelope, "salt");
            nonce = Bytes(envelope, "nonce");
            tag = Bytes(envelope, "tag");
            cipher = Bytes(envelope, "data");
            if (nonce.Length != NonceBytes || tag.Length != TagBytes) throw new FormatException();
        }
        catch (Exception ex) when (ex is FormatException or JsonException or InvalidOperationException)
        {
            throw new SecretsOperationException("This file is not a Secrets Manager export file.", ex);
        }

        var key = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, KeyBytes);
        var plain = new byte[cipher.Length];
        try
        {
            using var aes = new AesGcm(key, TagBytes);
            aes.Decrypt(nonce, cipher, tag, plain, AssociatedData(iterations));
            return (JsonNode.Parse(plain)?.AsArray() ?? throw new FormatException()).Select(n => FromJson(n!.AsObject())).ToList();
        }
        catch (AuthenticationTagMismatchException) { throw new ExportPasswordException(); }
        catch (Exception ex) when (ex is FormatException or JsonException or InvalidOperationException or KeyNotFoundException)
        {
            throw new SecretsOperationException("The export file decrypted, but its content is not valid.", ex);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
            CryptographicOperations.ZeroMemory(key);
        }
    }

    static byte[] AssociatedData(int iterations) => Encoding.UTF8.GetBytes($"{Format}/{Version}/{iterations}");

    static byte[] Bytes(JsonObject o, string name) =>
        Convert.FromBase64String((string?)o[name] ?? throw new FormatException());

    static JsonObject ToJson(ExportedSecret s)
    {
        var o = new JsonObject { ["name"] = s.Name, ["kind"] = s.Value.Kind.ToString() };
        switch (s.Value)
        {
            case TextSecret t: o["text"] = t.Text; break;
            case SecureTextSecret t: o["text"] = t.Text; break;
            case CredentialSecret c: o["userName"] = c.UserName; o["password"] = c.Password; break;
            case HashtableSecret h: o["entries"] = Map(h.Entries); break;
            case BytesSecret b: o["base64"] = Convert.ToBase64String(b.Data); break;
            default: throw new NotSupportedException($"Secrets of type {s.Value.Kind} cannot be exported.");
        }
        o["metadata"] = Map(s.Metadata);
        return o;
    }

    static ExportedSecret FromJson(JsonObject o)
    {
        string Req(string name) => (string?)o[name] ?? throw new FormatException($"Missing '{name}'.");
        var kind = Enum.Parse<SecretKind>(Req("kind"));
        SecretValue value = kind switch
        {
            SecretKind.String => new TextSecret(Req("text")),
            SecretKind.SecureString => new SecureTextSecret(Req("text")),
            SecretKind.PSCredential => new CredentialSecret(Req("userName"), Req("password")),
            SecretKind.Hashtable => new HashtableSecret(Unmap(o["entries"])),
            SecretKind.ByteArray => new BytesSecret(Convert.FromBase64String(Req("base64"))),
            _ => throw new FormatException($"Unsupported kind {kind}."),
        };
        return new ExportedSecret(Req("name"), value, Unmap(o["metadata"]));
    }

    static JsonObject Map(IReadOnlyDictionary<string, string> d) => new(d.Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)kv.Value)));

    static IReadOnlyDictionary<string, string> Unmap(JsonNode? node) =>
        node is JsonObject o ? o.ToDictionary(kv => kv.Key, kv => (string?)kv.Value ?? "") : new Dictionary<string, string>();
}
