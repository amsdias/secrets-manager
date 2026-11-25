namespace SecretsManager.Core.App;

/// <summary>Platform clipboard. The app implementation keeps copies out of clipboard history.</summary>
public interface IClipboard
{
    Task SetTextAsync(string text);
    Task<string?> GetTextAsync();
    Task ClearAsync();
}
