using System.Net;
using System.Reflection;
using Spectre.Console;
using XboxPrefill.Api;
using XboxPrefill.Handlers;
using XboxPrefill.Models.ApiResponses;

namespace XboxPrefill.Test;

public sealed class AccountDisplayNameTests
{
    [Fact]
    public async Task InteractiveLoginGetsGamertagWithProfileContractAndSavesIt()
    {
        using var http = new LoginHandler
        {
            ProfileBody = """{"profileUsers":[{"settings":[{"id":"gamertag","value":"  "},{"id":"gAmErTaG","value":"InteractivePlayer"}]}]}"""
        };
        var saves = 0;
        string? savedName = null;
        XboxAccountManager? account = null;
        account = CreateManager(http, () =>
        {
            saves++;
            savedName = account!.DisplayName;
        });

        await account.LoginAsync();

        Assert.Equal("InteractivePlayer", account.DisplayName);
        Assert.Equal("InteractivePlayer", savedName);
        Assert.Equal(1, saves);
        Assert.Equal(1, http.ProfileRequests);
        Assert.Equal(
            "https://profile.xboxlive.com/users/xuid(profile-xuid)/profile/settings?settings=Gamertag",
            http.ProfileUri);
        Assert.Equal("XBL3.0 x=test-uhs;test-token", http.ProfileAuthorization);
        Assert.Equal("2", http.ProfileContract);
        Assert.Equal("en-US", http.ProfileLanguage);
    }

    [Fact]
    public async Task ImportedLoginGetsGamertagBeforeSaving()
    {
        using var http = new LoginHandler { ProfileBody = Profile("ImportedPlayer") };
        var saves = 0;
        var account = CreateManager(http, () => saves++);
        using var signer = XblRequestSigner.CreateNew();

        await account.ImportAndLoginAsync("imported-refresh", signer.ExportPkcs8Base64());

        Assert.Equal("ImportedPlayer", account.DisplayName);
        Assert.Equal(1, saves);
        Assert.Equal(1, http.ProfileRequests);
    }

    [Fact]
    public async Task RefreshGetsGamertagBeforeSaving()
    {
        using var http = new LoginHandler
        {
            Xuid = "refresh-xuid",
            ProfileBody = Profile("RefreshPlayer")
        };
        var saves = 0;
        var account = CreateManager(http, () => saves++);
        SetAccount(account, ExpiredAccount("refresh-xuid"));

        await account.LoginAsync(false);

        Assert.Equal("RefreshPlayer", account.DisplayName);
        Assert.Equal(1, saves);
        Assert.Equal(1, http.ProfileRequests);
    }

    [Fact]
    public async Task ValidLegacySessionGetsAndSavesGamertagOnlyOnce()
    {
        using var http = new LoginHandler { ProfileBody = Profile("RecoveredPlayer") };
        var saves = 0;
        var account = CreateManager(http, () => saves++);
        SetAccount(account, ValidAccount("profile-xuid"));

        await account.LoginAsync(false);
        await account.LoginAsync(false);

        Assert.Equal("RecoveredPlayer", account.DisplayName);
        Assert.Equal(1, saves);
        Assert.Equal(1, http.ProfileRequests);
    }

    [Fact]
    public async Task SameXuidLookupFailureKeepsProducerResolvedGamertag()
    {
        using var http = new LoginHandler { ProfileBody = Profile("KnownPlayer") };
        var saves = 0;
        var account = CreateManager(http, () => saves++);
        SetAccount(account, ValidAccount("profile-xuid"));
        await account.LoginAsync(false);
        Expire(account.Account);
        http.ProfileStatus = HttpStatusCode.ServiceUnavailable;

        await account.LoginAsync(false);

        Assert.Equal("KnownPlayer", account.DisplayName);
        Assert.Equal(2, saves);
        Assert.Equal(2, http.ProfileRequests);
    }

    [Fact]
    public async Task DifferentXuidLookupFailureClearsProducerResolvedGamertag()
    {
        using var http = new LoginHandler { ProfileBody = Profile("KnownPlayer") };
        var saves = 0;
        var account = CreateManager(http, () => saves++);
        SetAccount(account, ValidAccount("profile-xuid"));
        await account.LoginAsync(false);
        Expire(account.Account);
        http.Xuid = "replacement-xuid";
        http.ProfileStatus = HttpStatusCode.ServiceUnavailable;

        await account.LoginAsync(false);

        Assert.Null(account.DisplayName);
        Assert.Equal("replacement-xuid", account.Xuid);
        Assert.Equal(2, saves);
        Assert.Equal(2, http.ProfileRequests);
        Assert.Contains("xuid(replacement-xuid)", http.ProfileUri, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MissingXuidDoesNotRequestProfileOrRetainOldGamertag()
    {
        using var http = new LoginHandler { Xuid = null, ProfileBody = Profile("WrongPlayer") };
        var saves = 0;
        var account = CreateManager(http, () => saves++);
        var previous = ExpiredAccount("old-xuid");
        previous.DisplayName = "OldPlayer";
        SetAccount(account, previous);

        await account.LoginAsync(false);

        Assert.Null(account.Xuid);
        Assert.Null(account.DisplayName);
        Assert.Equal(0, http.ProfileRequests);
        Assert.Equal(1, saves);
    }

    [Theory]
    [InlineData("{\"profileUsers\":[]}")]
    [InlineData("{\"profileUsers\":[")]
    [InlineData("{\"profileUsers\":[{\"settings\":[{\"id\":\"ModernGamertag\",\"value\":\"WrongPlayer\"}]}]}")]
    public async Task MissingOrMalformedProfileDoesNotFailLogin(string profileBody)
    {
        using var http = new LoginHandler { ProfileBody = profileBody };
        var saves = 0;
        var account = CreateManager(http, () => saves++);
        using var signer = XblRequestSigner.CreateNew();

        await account.ImportAndLoginAsync("imported-refresh", signer.ExportPkcs8Base64());

        Assert.Null(account.DisplayName);
        Assert.Equal(1, saves);
        Assert.Equal(1, http.ProfileRequests);
    }

    [Theory]
    [InlineData("http")]
    [InlineData("transport")]
    [InlineData("timeout")]
    public async Task ProfileFailureDoesNotFailLogin(string failure)
    {
        using var http = new LoginHandler
        {
            ProfileStatus = failure == "http" ? HttpStatusCode.BadGateway : HttpStatusCode.OK,
            ProfileFailure = failure == "http" ? null : failure
        };
        var saves = 0;
        var account = CreateManager(http, () => saves++);
        using var signer = XblRequestSigner.CreateNew();

        await account.ImportAndLoginAsync("imported-refresh", signer.ExportPkcs8Base64());

        Assert.Null(account.DisplayName);
        Assert.Equal(1, saves);
        Assert.Equal(1, http.ProfileRequests);
    }

    [Fact]
    public async Task CallerCancellationDuringProfileLookupPropagatesWithoutSaving()
    {
        using var http = new LoginHandler { HoldProfile = true };
        var saves = 0;
        var account = CreateManager(http, () => saves++);
        using var signer = XblRequestSigner.CreateNew();
        using var cancellation = new CancellationTokenSource();

        var login = account.ImportAndLoginAsync(
            "imported-refresh",
            signer.ExportPkcs8Base64(),
            cancellation.Token);
        await http.ProfileEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => login);
        Assert.Equal(0, saves);
    }

    private static XboxAccountManager CreateManager(LoginHandler http, Action save)
        => new(AnsiConsole.Console, new SilentLogin(), http, save);

    private static void SetAccount(XboxAccountManager manager, XboxAccount account)
        => typeof(XboxAccountManager).GetProperty(nameof(XboxAccountManager.Account))!.SetValue(manager, account);

    private static XboxAccount ValidAccount(string xuid) => new()
    {
        RefreshToken = "saved-refresh",
        XboxLiveToken = "test-token",
        XboxLiveUhs = "test-uhs",
        XboxLiveExpiresAt = DateTime.UtcNow.AddHours(2),
        UpdateToken = "update-token",
        UpdateUhs = "update-uhs",
        UpdateExpiresAt = DateTime.UtcNow.AddHours(2),
        Xuid = xuid
    };

    private static XboxAccount ExpiredAccount(string xuid)
    {
        var account = ValidAccount(xuid);
        Expire(account);
        return account;
    }

    private static void Expire(XboxAccount account)
    {
        account.XboxLiveExpiresAt = DateTime.UtcNow.AddMinutes(-1);
        account.UpdateExpiresAt = DateTime.UtcNow.AddMinutes(-1);
    }

    private static string Profile(string gamertag)
        => $$"""{"profileUsers":[{"settings":[{"id":"Gamertag","value":"{{gamertag}}"}]}]}""";

    private sealed class SilentLogin : IXboxAuthProvider
    {
        public Task PresentDeviceCodeAsync(
            string userCode,
            string verificationUri,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public void CancelPendingRequest() { }
    }

    private sealed class LoginHandler : HttpMessageHandler
    {
        public string? Xuid { get; set; } = "profile-xuid";
        public string ProfileBody { get; set; } = "{\"profileUsers\":[]}";
        public HttpStatusCode ProfileStatus { get; set; } = HttpStatusCode.OK;
        public string? ProfileFailure { get; set; }
        public bool HoldProfile { get; set; }
        public int ProfileRequests { get; private set; }
        public string? ProfileUri { get; private set; }
        public string? ProfileAuthorization { get; private set; }
        public string? ProfileContract { get; private set; }
        public string? ProfileLanguage { get; private set; }
        public TaskCompletionSource ProfileEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            if (uri.Host == "profile.xboxlive.com")
            {
                ProfileRequests++;
                ProfileUri = uri.AbsoluteUri;
                ProfileAuthorization = request.Headers.GetValues("Authorization").Single();
                ProfileContract = request.Headers.GetValues("x-xbl-contract-version").Single();
                ProfileLanguage = request.Headers.AcceptLanguage.Single().Value;
                ProfileEntered.TrySetResult();
                if (HoldProfile)
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }
                if (ProfileFailure == "transport")
                {
                    throw new HttpRequestException("Injected profile transport failure.");
                }
                if (ProfileFailure == "timeout")
                {
                    throw new TaskCanceledException("Injected profile timeout.");
                }
                return Json(ProfileStatus, ProfileBody);
            }

            if (uri.Host == "login.live.com" && uri.AbsolutePath.Contains("connect", StringComparison.Ordinal))
            {
                return Json(HttpStatusCode.OK,
                    "{\"device_code\":\"device-code\",\"user_code\":\"user-code\",\"verification_uri\":\"https://microsoft.test/device\",\"interval\":0,\"expires_in\":60}");
            }
            if (uri.Host == "login.live.com")
            {
                return Json(HttpStatusCode.OK,
                    "{\"access_token\":\"test-access\",\"refresh_token\":\"test-refresh\"}");
            }
            if (uri.Host is "user.auth.xboxlive.com" or "device.auth.xboxlive.com")
            {
                return Json(HttpStatusCode.OK,
                    "{\"Token\":\"auth-token\",\"NotAfter\":\"2030-01-01T00:00:00Z\"}");
            }
            if (uri.Host == "xsts.auth.xboxlive.com")
            {
                var xid = Xuid is null ? string.Empty : $",\"xid\":\"{Xuid}\"";
                return Json(HttpStatusCode.OK,
                    $"{{\"Token\":\"test-token\",\"NotAfter\":\"2030-01-01T00:00:00Z\",\"DisplayClaims\":{{\"xui\":[{{\"uhs\":\"test-uhs\"{xid}}}]}}}}");
            }

            throw new InvalidOperationException($"Unexpected request host: {uri.Host}");
        }

        private static HttpResponseMessage Json(HttpStatusCode status, string content)
            => new(status) { Content = new StringContent(content) };
    }
}
