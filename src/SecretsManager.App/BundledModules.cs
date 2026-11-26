namespace SecretsManager.App;

/// <summary>
/// PowerShell modules shipped inside the exe: Microsoft.PowerShell.SecretManagement and .SecretStore (third_party, MIT)
/// and SecretsManager.WindowsVault (src/WindowsVault). They are copied to a stable folder, because SecretManagement
/// records a vault's module path when it is registered and a single-file build runs from a temp extraction folder.
/// The folder goes at the END of PSModulePath, so modules the user installed take precedence; on a PC without them, the
/// app still works with nothing else installed.
/// </summary>
static class BundledModules
{
    const string Prefix = "Modules\\";

    public static string Root => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SecretsManager", "Modules");

    /// <summary>Copies changed files and appends <see cref="Root"/> to this process's PSModulePath.</summary>
    /// <returns>The SecretsManager.WindowsVault psd1 path, or null if the modules couldn't be installed.</returns>
    public static string? EnsureInstalled()
    {
        string? result = null;
        try
        {
            var assembly = typeof(BundledModules).Assembly;
            foreach (var resource in assembly.GetManifestResourceNames().Where(n => n.StartsWith(Prefix, StringComparison.Ordinal)))
            {
                using var stream = assembly.GetManifestResourceStream(resource)!;
                using var buffer = new MemoryStream();
                stream.CopyTo(buffer);
                var bytes = buffer.ToArray();
                var target = Path.Combine(Root, resource[Prefix.Length..]);
                if (File.Exists(target) && File.ReadAllBytes(target).AsSpan().SequenceEqual(bytes)) continue;
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.WriteAllBytes(target, bytes);
            }
            var psd1 = Path.Combine(Root, "SecretsManager.WindowsVault", "SecretsManager.WindowsVault.psd1");
            if (File.Exists(psd1)) result = psd1;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* keep going with what's there */ }

        var path = Environment.GetEnvironmentVariable("PSModulePath") ?? "";
        if (!path.Split(';').Contains(Root, StringComparer.OrdinalIgnoreCase))
            Environment.SetEnvironmentVariable("PSModulePath", path.Length == 0 ? Root : path.TrimEnd(';') + ";" + Root);
        return result;
    }
}
