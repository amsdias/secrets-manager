using Microsoft.UI.Reactor;
using SecretsManager.App;
using SecretsManager.Core;
using SecretsManager.Core.App;

using var host = new PowerShellHost();
AppServices.Store = new AppStore(new SecretsService(host), new WinUiClipboard());
ReactorApp.Run<SecretsApp>("PowerShell Secrets", width: 1180, height: 740);
