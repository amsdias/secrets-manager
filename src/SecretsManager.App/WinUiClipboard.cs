using SecretsManager.Core.App;
using Windows.ApplicationModel.DataTransfer;

namespace SecretsManager.App;

/// <summary>
/// Clipboard writes are excluded from Windows clipboard history (Win+V) and cloud clipboard sync,
/// so a copied secret doesn't outlive the app's auto-clear. Must be used from the UI thread.
/// </summary>
sealed class WinUiClipboard : IClipboard
{
    public Task SetTextAsync(string text)
    {
        var package = new DataPackage();
        package.SetText(text);
        Clipboard.SetContentWithOptions(package, new ClipboardContentOptions { IsAllowedInHistory = false, IsRoamable = false });
        Clipboard.Flush(); // keep the content available until we clear it, not only while this process lives
        return Task.CompletedTask;
    }

    public async Task<string?> GetTextAsync()
    {
        var content = Clipboard.GetContent();
        return content.Contains(StandardDataFormats.Text) ? await content.GetTextAsync() : null;
    }

    public Task ClearAsync()
    {
        Clipboard.Clear();
        return Task.CompletedTask;
    }
}
