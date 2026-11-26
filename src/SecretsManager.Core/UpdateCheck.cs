using System.Net.Http.Headers;
using System.Text.Json;

namespace SecretsManager.Core;

public sealed record UpdateInfo(Version Version, Uri Url);

/// <summary>Finds the newest published release. Returns null when it can't tell (offline, rate-limited, unexpected reply).</summary>
public interface IUpdateChecker
{
    Task<UpdateInfo?> GetLatestAsync(CancellationToken ct);
}

/// <summary>
/// GitHub's "latest release" (drafts and pre-releases excluded). One anonymous GET with a generic User-Agent; nothing
/// about the user or their secrets is sent. Only links into this repository's releases are accepted, so a tampered
/// reply can't make the app offer an arbitrary link.
/// </summary>
public sealed class GitHubUpdateChecker(HttpClient http, string repository = GitHubUpdateChecker.Repository) : IUpdateChecker
{
    public const string Repository = "amsdias/secrets-manager";

    public async Task<UpdateInfo?> GetLatestAsync(CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{repository}/releases/latest");
            request.Headers.UserAgent.Add(new ProductInfoHeaderValue("SecretsManager", "1"));
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            using var response = await http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode) return null;
            using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            return Parse(json.RootElement, repository);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException or InvalidOperationException)
        {
            return null;
        }
    }

    public static UpdateInfo? Parse(JsonElement release, string repository = Repository)
    {
        if (release.ValueKind != JsonValueKind.Object ||
            !release.TryGetProperty("tag_name", out var tag) || tag.ValueKind != JsonValueKind.String ||
            !release.TryGetProperty("html_url", out var url) || url.ValueKind != JsonValueKind.String) return null;
        if (!Version.TryParse(tag.GetString()!.TrimStart('v', 'V'), out var version)) return null;
        if (!Uri.TryCreate(url.GetString(), UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || uri.Host != "github.com" ||
            !uri.AbsolutePath.StartsWith($"/{repository}/releases/", StringComparison.OrdinalIgnoreCase)) return null;
        return new(version, uri);
    }

    /// <summary>Compares major.minor.build only (assembly versions carry a 4th part, tags don't).</summary>
    public static bool IsNewer(Version latest, Version current) =>
        new Version(latest.Major, latest.Minor, Math.Max(latest.Build, 0)) > new Version(current.Major, current.Minor, Math.Max(current.Build, 0));
}
