using System.Diagnostics;

// Relays "get <name> …" / "help" to SecretsManager.exe (next to this exe, or SECRETSMANAGER_APP) and returns its exit code.
if (args.Length == 0 || args[0] is not ("get" or "help" or "--help" or "-h" or "/?"))
    args = ["help"];

var app = Environment.GetEnvironmentVariable("SECRETSMANAGER_APP") is { Length: > 0 } overridePath
    ? overridePath
    : Path.Combine(AppContext.BaseDirectory, "SecretsManager.exe");
if (!File.Exists(app))
{
    Console.Error.WriteLine($"SecretsManager.exe not found at {app}.");
    return 3;
}

var psi = new ProcessStartInfo(app)
{
    UseShellExecute = false,
    RedirectStandardOutput = true,
    RedirectStandardError = true,
    CreateNoWindow = true,
};
foreach (var a in args) psi.ArgumentList.Add(a);

using var process = Process.Start(psi)!;
using var stdout = Console.OpenStandardOutput();
using var stderr = Console.OpenStandardError();
await Task.WhenAll(
    process.StandardOutput.BaseStream.CopyToAsync(stdout),
    process.StandardError.BaseStream.CopyToAsync(stderr),
    process.WaitForExitAsync());
return process.ExitCode;
