using System.Net;
using Spectre.Console;
using XboxPrefill.Api;
using XboxPrefill.Handlers;
using XboxPrefill.Models.ApiResponses;

namespace XboxPrefill.Test;

public sealed class AccountRefreshTests
{
    [Fact]
    public async Task ConcurrentRefreshUsesOneAccountAndSigningKey()
    {
        using var http = new LoginHandler();
        var saves = 0;
        var account = new XboxAccountManager(AnsiConsole.Console, new SilentLogin(), http, () => Interlocked.Increment(ref saves));
        using var signer = XblRequestSigner.CreateNew();
        typeof(XboxAccountManager).GetProperty(nameof(XboxAccountManager.Account))!.SetValue(account,
            new XboxAccount { RefreshToken = "test-refresh", DeviceKeyPkcs8 = signer.ExportPkcs8Base64() });
        var calls = Enumerable.Range(0, 3).Select(_ => account.LoginAsync(false)).ToArray();
        await http.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        http.Release.TrySetResult();
        await Task.WhenAll(calls).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(1, http.Refreshes);
        Assert.Equal(1, saves);
        Assert.False(account.TokensAreExpired());
        Assert.Equal(signer.ExportPkcs8Base64(), account.Account.DeviceKeyPkcs8);
        var requests = Enumerable.Range(0, 16).Select(_ => new HttpRequestMessage(HttpMethod.Get, "https://packages.test/item")).ToArray();
        try
        {
            await Task.WhenAll(requests.Select(request => account.AuthorizeAsync(request, true, CancellationToken.None)));
            Assert.All(requests, request =>
            {
                Assert.Equal("XBL3.0 x=test-uhs;test-token", request.Headers.GetValues("Authorization").Single());
                Assert.Single(request.Headers.GetValues("Signature"));
            });
        }
        finally { foreach (var request in requests) request.Dispose(); }
    }

    private sealed class SilentLogin : IXboxAuthProvider
    {
        public Task PresentDeviceCodeAsync(string userCode, string verificationUri, CancellationToken cancellationToken = default) => throw new InvalidOperationException();
        public void CancelPendingRequest() { }
    }

    private sealed class LoginHandler : HttpMessageHandler
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Refreshes;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string json;
            if (request.RequestUri!.Host == "login.live.com")
            {
                Interlocked.Increment(ref Refreshes);
                Entered.TrySetResult();
                await Release.Task.WaitAsync(cancellationToken);
                json = "{\"access_token\":\"test-access\",\"refresh_token\":\"test-refresh\"}";
            }
            else
            {
                json = "{\"Token\":\"test-token\",\"NotAfter\":\"2030-01-01T00:00:00Z\",\"DisplayClaims\":{\"xui\":[{\"uhs\":\"test-uhs\",\"xid\":\"test-xuid\"}]}}";
            }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) };
        }
    }
}
