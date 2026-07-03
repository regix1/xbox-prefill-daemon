using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using XboxPrefill.Api;
using XboxPrefill.Models.ApiResponses;
using XboxPrefill.Settings;
using Xunit;

namespace XboxPrefill.Test
{
    /// <summary>
    /// SocketCommandInterface itself has no test seam (it owns a live SocketServer and only reaches
    /// _isLoggedIn=true via a real MSA/XSTS login), so this covers the underlying invariant its
    /// complete-forget contract depends on: <see cref="XboxPrefillApi.Shutdown"/> must drop the
    /// in-memory MSA/XSTS token state unconditionally - not gated on IsInitialized - so a logout
    /// racing a mid-login task can't leave a live token behind (diagnostic: XboxPrefillApi.cs
    /// Shutdown, XboxManager.ClearAccount, XboxAccountManager.ClearAccount). Pre-seeds an unexpired
    /// account on disk so InitializeAsync takes the "reuse existing session" path and never makes a
    /// real network call.
    /// </summary>
    [Collection("XboxAccountFile")]
    public sealed class LogoutClearsAccountTests : IDisposable
    {
        private static readonly string AccountPath = AppConfig.AccountSettingsStorePath;
        private readonly bool _accountFileExisted;
        private readonly string? _originalAccountContent;

        public LogoutClearsAccountTests()
        {
            _accountFileExisted = File.Exists(AccountPath);
            _originalAccountContent = _accountFileExisted ? File.ReadAllText(AccountPath) : null;
        }

        public void Dispose()
        {
            if (_accountFileExisted && _originalAccountContent != null)
            {
                File.WriteAllText(AccountPath, _originalAccountContent);
            }
            else
            {
                File.Delete(AccountPath);
            }
        }

        private sealed class NeverCalledAuthProvider : IXboxAuthProvider
        {
            public Task PresentDeviceCodeAsync(string userCode, string verificationUri, CancellationToken cancellationToken = default)
                => throw new InvalidOperationException("Should not be called: account on disk has unexpired tokens.");

            public void CancelPendingRequest() { }
        }

        [Fact]
        public async Task Shutdown_AfterSuccessfulInitialize_ClearsAccount_AndTokenStateGoesNull()
        {
            var account = new XboxAccount
            {
                RefreshToken = "test-refresh-token",
                RefreshTokenIssuedUtc = DateTime.UtcNow,
                DisplayName = "test-gamertag",
                Xuid = "test-xuid",
                DeviceKeyPkcs8 = "test-device-key",
                XboxLiveToken = "test-xbl-token",
                XboxLiveUhs = "test-xbl-uhs",
                XboxLiveExpiresAt = DateTime.UtcNow.AddHours(4),
                UpdateToken = "test-update-token",
                UpdateUhs = "test-update-uhs",
                UpdateExpiresAt = DateTime.UtcNow.AddHours(4)
            };
            // Plaintext (unencrypted) - XboxAccountManager.LoadFromFile treats this as the legacy
            // migration path: loads it directly, no auth provider call needed either way.
            File.WriteAllText(AccountPath, JsonSerializer.Serialize(account, SerializationContext.Default.XboxAccount));

            var api = new XboxPrefillApi(new NeverCalledAuthProvider());
            await api.InitializeAsync();

            Assert.True(api.IsInitialized);
            Assert.Equal("test-gamertag", api.DisplayName);
            Assert.True(api.HasRefreshToken);
            Assert.NotNull(api.XstsExpiryUtc);

            api.Shutdown();

            Assert.False(api.IsInitialized);
            Assert.Null(api.DisplayName);
            Assert.False(api.HasRefreshToken);
            Assert.Null(api.XstsExpiryUtc);
        }
    }
}
