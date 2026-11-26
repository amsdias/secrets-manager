using System.Globalization;

namespace SecretsManager.Core;

public enum ExpiryState { None, Ok, Soon, Expired }

/// <summary>
/// A secret's expiry, from an "expires" metadata value (also "expiry" or "expiration"): a date such as 2027-01-31, or an
/// ISO 8601 date and time. The secret counts as expired from the day after that date; it is "Soon" from
/// <see cref="SoonDays"/> days before.
/// </summary>
public readonly record struct SecretExpiry(ExpiryState State, DateOnly Date, int DaysLeft)
{
    public const int SoonDays = 14;
    public static IReadOnlyList<string> Keys { get; } = ["expires", "expiry", "expiration"];

    public static SecretExpiry Of(IReadOnlyDictionary<string, string> metadata, DateOnly today)
    {
        var text = metadata.FirstOrDefault(kv => Keys.Contains(kv.Key, StringComparer.OrdinalIgnoreCase)).Value?.Trim();
        if (string.IsNullOrEmpty(text) || ParseDate(text) is not { } date) return default;
        var days = date.DayNumber - today.DayNumber;
        return new(days < 0 ? ExpiryState.Expired : days <= SoonDays ? ExpiryState.Soon : ExpiryState.Ok, date, days);
    }

    static DateOnly? ParseDate(string text)
    {
        if (DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)) return d;
        if (DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var dt))
            return DateOnly.FromDateTime(dt.LocalDateTime);
        return null;
    }

    /// <summary>"expired 3 days ago", "expires tomorrow", …; null when there is nothing to warn about.</summary>
    public string? Warning => State switch
    {
        ExpiryState.Expired => DaysLeft == -1 ? "expired yesterday" : $"expired {-DaysLeft} days ago",
        ExpiryState.Soon => DaysLeft switch { 0 => "expires today", 1 => "expires tomorrow", _ => $"expires in {DaysLeft} days" },
        _ => null,
    };

    /// <summary>For the status bar: "1 secret has expired, 2 expire within 14 days." or "".</summary>
    public static string Summary(IEnumerable<SecretExpiry> expiries)
    {
        var list = expiries.ToList();
        int expired = list.Count(e => e.State == ExpiryState.Expired), soon = list.Count(e => e.State == ExpiryState.Soon);
        var parts = new List<string>();
        if (expired > 0) parts.Add(expired == 1 ? "1 secret has expired" : $"{expired} secrets have expired");
        if (soon > 0) parts.Add(soon == 1 ? $"1 expires within {SoonDays} days" : $"{soon} expire within {SoonDays} days");
        return parts.Count == 0 ? "" : string.Join(", ", parts) + ".";
    }
}
