using SecretsManager.Core.App;

namespace SecretsManager.App;

/// <summary>Composition root handed to the Reactor component tree (ReactorApp.Run creates the root itself).</summary>
static class AppServices
{
    public static AppStore Store { get; set; } = null!;
}
