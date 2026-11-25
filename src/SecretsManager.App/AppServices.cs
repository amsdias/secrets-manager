using Microsoft.UI.Reactor;
using SecretsManager.Core.App;

namespace SecretsManager.App;

/// <summary>Composition root handed to the Reactor component tree (ReactorApp.Run creates the root itself).</summary>
static class AppServices
{
    public static AppStore Store { get; set; } = null!;
    /// <summary>The icon from the exe's embedded resources, for the tray (a single-file build has no icon file on disk).</summary>
    public static WindowIcon? TrayIcon { get; set; }
    /// <summary>UI tests only (--ui-test-file &lt;path&gt;): export/import use this path instead of showing a file dialog.</summary>
    public static string? UiTestFilePath { get; set; }
}
