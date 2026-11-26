using System.Runtime.InteropServices;
using System.Text;
using SecretsManager.Core;

namespace SecretsManager.App;

/// <summary>"SecretsManager.exe get …": runs SecretsCli without any window.</summary>
static class CliHost
{
    public static bool IsCliCommand(string[] args) => args.Length > 0 && args[0] is "get" or "help" or "--help" or "-h" or "/?";

    public static async Task<int> RunAsync(string[] args)
    {
        // A GUI-subsystem exe has no console. When run from a terminal without redirection, borrow the parent's.
        if (!Console.IsOutputRedirected || !Console.IsErrorRedirected) AttachConsole(-1);
        var utf8 = new UTF8Encoding(false);
        using var stdout = new StreamWriter(Console.OpenStandardOutput(), utf8) { AutoFlush = true };
        using var stderr = new StreamWriter(Console.OpenStandardError(), utf8) { AutoFlush = true };
        BundledModules.EnsureInstalled();
        using var host = new PowerShellHost();
        return await SecretsCli.RunAsync(args, new SecretsService(host),
            new WindowsHelloPasswordProvider(WindowsHelloPasswordProvider.DefaultSealPath), stdout, stderr);
    }

#pragma warning disable SYSLIB1054 // LibraryImport needs AllowUnsafeBlocks; one blittable call doesn't need it
    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool AttachConsole(int processId);
#pragma warning restore SYSLIB1054
}
