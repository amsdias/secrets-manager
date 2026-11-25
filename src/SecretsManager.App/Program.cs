using Microsoft.UI.Reactor;
using SecretsManager.App;
using SecretsManager.Core;
using SecretsManager.Core.App;

using var host = new PowerShellHost();
AppServices.Store = new AppStore(new SecretsService(host), new WinUiClipboard(),
    settings: new JsonFileSettingsStore(JsonFileSettingsStore.DefaultPath));
var icon = Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico");
ReactorApp.Run<SecretsApp>("PowerShell Secrets", width: 1180, height: 740,
    icon: File.Exists(icon) ? WindowIcon.FromPath(icon) : null);
