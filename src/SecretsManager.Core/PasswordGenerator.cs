using System.Security.Cryptography;

namespace SecretsManager.Core;

public static class PasswordGenerator
{
    // Ambiguous characters (0/O, 1/l/I) are left out.
    const string Letters = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789";
    const string Symbols = "!#$%&*+-=?@^_~";

    /// <summary>Uniformly random password from a CSPRNG.</summary>
    public static string Generate(int length = 24, bool includeSymbols = true)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(length, 8);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(length, 256);
        return RandomNumberGenerator.GetString(includeSymbols ? Letters + Symbols : Letters, length);
    }
}
