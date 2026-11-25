using Microsoft.UI.Reactor;
using SecretsManager.App;
using SecretsManager.Core;
using SecretsManager.Core.App;

// Command line ("get <name>", "help"): no window. Blocking here keeps Main synchronous for WinUI's STA thread.
if (CliHost.IsCliCommand(args)) Environment.Exit(CliHost.RunAsync(args).GetAwaiter().GetResult());

string? Arg(string name) => args.SkipWhile(a => a != name).Skip(1).FirstOrDefault();

using var host = new PowerShellHost();
AppServices.Store = new AppStore(new SecretsService(host), new WinUiClipboard(),
    settings: new JsonFileSettingsStore(Arg("--settings-file") ?? JsonFileSettingsStore.DefaultPath),
    storePassword: new WindowsHelloPasswordProvider(WindowsHelloPasswordProvider.DefaultSealPath))
{
    WindowsVaultModulePath = WindowsVaultModule.EnsureInstalled(),
};
AppServices.UiTestFilePath = Arg("--ui-test-file");

using (var stream = typeof(AppServices).Assembly.GetManifestResourceStream("app.ico"))
    if (stream is not null)
    {
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        AppServices.TrayIcon = WindowIcon.FromBytes(buffer.ToArray());
    }

// The window caption needs an icon file; without one (single-file build) Windows uses the exe's own icon.
var iconFile = Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico");
ReactorApp.Run<SecretsApp>(new WindowSpec
{
    Title = "PowerShell Secrets",
    Width = 1180,
    Height = 740,
    Icon = File.Exists(iconFile) ? WindowIcon.FromPath(iconFile) : null,
    PersistPlacement = true, // size, position and maximized state are restored on the next start
    PersistenceId = "main",
});
