namespace SecretsManager.Core;

/// <summary>
/// "key=value" per line, used to edit metadata, hashtable secrets and vault parameters.
/// Blank lines and lines starting with '#' are ignored; only the first '=' splits.
/// </summary>
public static class KeyValueText
{
    public static OrderedDictionary<string, string> Parse(string? text)
    {
        var result = new OrderedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(text)) return result;

        var lineNo = 0;
        foreach (var raw in text.Split('\n'))
        {
            lineNo++;
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            var idx = line.IndexOf('=');
            if (idx < 1) throw new FormatException($"Line {lineNo} is not in 'key=value' form: '{line}'");
            var key = line[..idx].Trim();
            if (!result.TryAdd(key, line[(idx + 1)..].Trim()))
                throw new FormatException($"Duplicate key '{key}' on line {lineNo}.");
        }
        return result;
    }

    public static string Format(IEnumerable<KeyValuePair<string, string>>? pairs, string separator) =>
        pairs is null ? "" : string.Join(separator, pairs.Select(p => $"{p.Key}={p.Value}"));
}
