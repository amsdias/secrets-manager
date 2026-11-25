namespace SecretsManager.App;

/// <summary>
/// Installs the bundled SecretsManager.WindowsVault module (embedded from src/WindowsVault) to a stable folder.
/// SecretManagement stores the module path when a vault is registered, and a single-file build runs from a temp
/// extraction folder, so the module must not be used from there.
/// </summary>
static class WindowsVaultModule
{
    const string Prefix = "WindowsVault\\";

    public static string Root => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SecretsManager", "Modules");

    /// <summary>Copies changed files; returns the module's psd1 path, or null if it couldn't be installed.</summary>
    public static string? EnsureInstalled()
    {
        try
        {
            var assembly = typeof(WindowsVaultModule).Assembly;
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
            return File.Exists(psd1) ? psd1 : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }
}
