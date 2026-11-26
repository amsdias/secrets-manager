using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Time.Testing;
using SecretsManager.Core;
using SecretsManager.Core.App;

namespace SecretsManager.Core.Tests;

/// <summary>"secrets list", expiry warnings from metadata, and the GitHub update check.</summary>
public sealed class ExpiryUpdateListTests
{
    readonly FakeSecretsService _svc = new();
    readonly FakeStorePasswordProvider _hello = new();
    static readonly DateOnly Today = new(2026, 11, 26);

    static Dictionary<string, string> Meta(params (string K, string V)[] kv) => kv.ToDictionary(x => x.K, x => x.V);

    async Task<(int Code, string Out, string Err)> Cli(params string[] args)
    {
        var (o, e) = (new StringWriter(), new StringWriter());
        var code = await SecretsCli.RunAsync(args, _svc, _hello, o, e, Today);
        return (code, o.ToString(), e.ToString());
    }

    void AddDevAndStore()
    {
        var dev = _svc.AddVault("Dev", module: @"C:\m\SecretsManager.WindowsVault.psd1");
        dev.Secrets["api-key"] = (new TextSecret("SECRET-A"), Meta(("env", "dev"), ("expires", "2026-12-01")));
        dev.Secrets["db-login"] = (new CredentialSecret("me", "SECRET-B"), new());
        var prod = _svc.AddVault("Prod", password: "store-pw", isDefault: true, module: VaultInfo.SecretStoreModule);
        prod.Secrets["prod-key"] = (new TextSecret("SECRET-C"), new());
        _svc.IsSecretStoreInitialized = true;
        _hello.Sealed = "store-pw";
    }

    // ------------------------------------------------------------------ list

    [Fact]
    public async Task List_shows_names_and_metadata_never_values_and_skips_a_locked_SecretStore_without_asking()
    {
        AddDevAndStore();
        var (code, output, err) = await Cli("list");
        Assert.Equal(SecretsCli.Found, code);
        Assert.Contains("api-key", output);
        Assert.Contains("env=dev", output);
        Assert.Contains("(expires in 5 days)", output);
        Assert.Contains("db-login", output);
        Assert.DoesNotContain("prod-key", output);
        Assert.DoesNotContain("SECRET-", output);
        Assert.Contains("secrets list --vault Prod", err);
        Assert.Equal(0, _hello.Prompts);
    }

    [Fact]
    public async Task List_of_a_named_SecretStore_vault_unlocks_it_with_Windows_Hello()
    {
        AddDevAndStore();
        var (code, output, _) = await Cli("list", "--vault", "prod");
        Assert.Equal((SecretsCli.Found, 1), (code, _hello.Prompts));
        Assert.Contains("prod-key", output);
        Assert.DoesNotContain("api-key", output);
    }

    [Fact]
    public async Task List_filters_by_name_and_prints_json()
    {
        AddDevAndStore();
        var (code, output, _) = await Cli("list", "API", "--json");
        Assert.Equal(SecretsCli.Found, code);
        var item = Assert.Single(JsonDocument.Parse(output).RootElement.EnumerateArray());
        Assert.Equal("api-key", item.GetProperty("name").GetString());
        Assert.Equal("Dev", item.GetProperty("vault").GetString());
        Assert.Equal("String", item.GetProperty("type").GetString());
        Assert.Equal("dev", item.GetProperty("metadata").GetProperty("env").GetString());
        Assert.Equal("2026-12-01", item.GetProperty("expires").GetString());
        Assert.Equal("soon", item.GetProperty("expiry").GetString());
        Assert.Equal(SecretsCli.Failed, (await Cli("list", "--field", "username")).Code);
    }

    [Fact]
    public async Task Get_of_an_expired_secret_still_prints_it_with_a_warning()
    {
        _svc.AddVault("Dev").Secrets["old"] = (new TextSecret("v"), Meta(("Expires", "2026-11-20")));
        var (code, output, err) = await Cli("get", "old");
        Assert.Equal((SecretsCli.Found, "v"), (code, output));
        Assert.Contains("expired 6 days ago (2026-11-20)", err);
    }

    // ------------------------------------------------------------------ expiry

    [Theory]
    [InlineData("env=ui\rexpires=2020-01-31")]   // WinUI TextBox line breaks
    [InlineData("env=ui\r\nexpires=2020-01-31")]
    [InlineData("env=ui\nexpires=2020-01-31")]
    public void Key_value_lines_split_on_every_kind_of_line_break(string text)
    {
        var parsed = KeyValueText.Parse(text);
        Assert.Equal(("ui", "2020-01-31"), (parsed["env"], parsed["expires"]));
    }

    [Theory]
    [InlineData("2026-11-25", ExpiryState.Expired, "expired yesterday")]
    [InlineData("2026-11-26", ExpiryState.Soon, "expires today")]
    [InlineData("2026-11-27", ExpiryState.Soon, "expires tomorrow")]
    [InlineData("2026-12-10", ExpiryState.Soon, "expires in 14 days")]
    [InlineData("2026-12-11", ExpiryState.Ok, null)]
    [InlineData("2027-01-31T12:00:00Z", ExpiryState.Ok, null)]
    [InlineData("next tuesday", ExpiryState.None, null)]
    public void Expiry_is_read_from_the_expires_metadata(string value, ExpiryState state, string? warning)
    {
        var e = SecretExpiry.Of(Meta(("expiry", value)), Today);
        Assert.Equal((state, warning), (e.State, e.Warning));
    }

    [Fact]
    public async Task The_vault_status_counts_expired_and_expiring_secrets_and_the_date_moves_on()
    {
        var v = _svc.AddVault("Dev", isDefault: true);
        v.Secrets["a"] = (new TextSecret("1"), Meta(("expires", "2026-11-01")));
        v.Secrets["b"] = (new TextSecret("2"), Meta(("expires", "2026-11-30")));
        v.Secrets["c"] = (new TextSecret("3"), Meta(("expires", "2026-11-28")));
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 11, 26, 12, 0, 0, TimeZoneInfo.Local.GetUtcOffset(new DateTime(2026, 11, 26))));
        time.SetLocalTimeZone(TimeZoneInfo.Local);
        var store = new AppStore(_svc, new FakeClipboard(), time);
        _ = new PromptScript(store);
        await store.InitializeAsync();
        Assert.Equal("1 secret has expired, 2 expire within 14 days.", store.State.ExpirySummary);
        Assert.Equal("expires in 4 days", store.State.ExpiryOf(store.State.Secrets.Single(s => s.Name == "b")).Warning);

        time.Advance(TimeSpan.FromDays(3));
        for (var i = 0; i < 10 && store.State.Today != new DateOnly(2026, 11, 29); i++) await Task.Delay(20, TestContext.Current.CancellationToken);
        Assert.Equal("expires tomorrow", store.State.ExpiryOf(store.State.Secrets.Single(s => s.Name == "b")).Warning);
        Assert.Equal(ExpiryState.Expired, store.State.ExpiryOf(store.State.Secrets.Single(s => s.Name == "c")).State);
    }

    // ------------------------------------------------------------------ update check

    sealed class FakeUpdates(UpdateInfo? latest) : IUpdateChecker
    {
        public int Calls { get; private set; }
        public Task<UpdateInfo?> GetLatestAsync(CancellationToken ct) { Calls++; return Task.FromResult(latest); }
    }

    static readonly Uri ReleaseUrl = new("https://github.com/amsdias/secrets-manager/releases/tag/v0.2.0");

    [Theory]
    [InlineData("0.2.0", true)]
    [InlineData("0.1.1", false)]
    [InlineData("0.1.0", false)]
    public async Task A_newer_release_is_offered_once_checked(string latest, bool offered)
    {
        var updates = new FakeUpdates(new UpdateInfo(Version.Parse(latest), ReleaseUrl));
        var store = new AppStore(_svc, new FakeClipboard()) { Updates = updates, CurrentVersion = new Version(0, 1, 1, 0) };
        await store.CheckForUpdatesAsync();
        Assert.Equal(offered, store.State.UpdateAvailable is not null);
    }

    [Fact]
    public async Task The_update_check_can_be_turned_off_and_on()
    {
        var settings = new InMemorySettingsStore(new AppSettings(CheckForUpdates: false));
        var updates = new FakeUpdates(new UpdateInfo(new Version(9, 0, 0), ReleaseUrl));
        var store = new AppStore(_svc, new FakeClipboard(), settings: settings) { Updates = updates, CurrentVersion = new Version(0, 1, 1) };
        await store.CheckForUpdatesAsync();
        Assert.Equal((0, null), (updates.Calls, store.State.UpdateAvailable));

        await store.UpdateSettingsAsync(s => s with { CheckForUpdates = true });
        for (var i = 0; i < 10 && store.State.UpdateAvailable is null; i++) await Task.Delay(20, TestContext.Current.CancellationToken);
        Assert.Equal(new Version(9, 0, 0), store.State.UpdateAvailable?.Version);

        await store.UpdateSettingsAsync(s => s with { CheckForUpdates = false });
        Assert.Null(store.State.UpdateAvailable);
        Assert.False(settings.Current.CheckForUpdates);
    }

    sealed class StubHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Request = request;
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
        }
    }

    [Fact]
    public async Task GitHub_reply_is_parsed_and_only_this_repositorys_release_links_are_accepted()
    {
        var handler = new StubHandler(HttpStatusCode.OK, """{"tag_name":"v0.2.0","html_url":"https://github.com/amsdias/secrets-manager/releases/tag/v0.2.0"}""");
        var latest = await new GitHubUpdateChecker(new HttpClient(handler)).GetLatestAsync(TestContext.Current.CancellationToken);
        Assert.Equal((new Version(0, 2, 0), ReleaseUrl), (latest?.Version, latest?.Url));
        Assert.Equal("https://api.github.com/repos/amsdias/secrets-manager/releases/latest", handler.Request!.RequestUri!.ToString());
        Assert.Equal("SecretsManager/1", handler.Request.Headers.UserAgent.ToString());

        Assert.Null(GitHubUpdateChecker.Parse(JsonDocument.Parse("""{"tag_name":"v9.0.0","html_url":"https://evil.example/releases/x"}""").RootElement));
        Assert.Null(GitHubUpdateChecker.Parse(JsonDocument.Parse("""{"tag_name":"nightly","html_url":"https://github.com/amsdias/secrets-manager/releases/tag/nightly"}""").RootElement));
        Assert.Null(await new GitHubUpdateChecker(new HttpClient(new StubHandler(HttpStatusCode.Forbidden, "rate limited"))).GetLatestAsync(TestContext.Current.CancellationToken));
        Assert.Null(await new GitHubUpdateChecker(new HttpClient(new StubHandler(HttpStatusCode.OK, "<html>"))).GetLatestAsync(TestContext.Current.CancellationToken));
    }
}
