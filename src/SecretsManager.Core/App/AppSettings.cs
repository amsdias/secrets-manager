using System.Text.Json;

namespace SecretsManager.Core.App;

/// <summary>App preferences (not secrets). AutoLockMinutes: 0 turns auto-lock off.</summary>
public sealed record AppSettings(int AutoLockMinutes = 10);

public interface IAppSettingsStore
{
    AppSettings Load();
    void Save(AppSettings settings);
}

/// <summary>Settings as JSON in a file; a missing or unreadable file gives the defaults.</summary>
public sealed class JsonFileSettingsStore(string path) : IAppSettingsStore
{
    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SecretsManager", "settings.json");

    public AppSettings Load()
    {
        try { return File.Exists(path) ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path)) ?? new() : new(); }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { return new(); }
    }

    public void Save(AppSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
    }
}

public sealed class InMemorySettingsStore(AppSettings? initial = null) : IAppSettingsStore
{
    public AppSettings Current { get; private set; } = initial ?? new();
    public AppSettings Load() => Current;
    public void Save(AppSettings settings) => Current = settings;
}
