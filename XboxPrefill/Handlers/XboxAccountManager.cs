namespace XboxPrefill.Handlers
{
    /// <summary>
    /// Handles the Xbox Live login chain (all steps PROVEN live against a real account):
    /// device-code (MSA) -> user token (user.auth) -> device token (device.auth, signed) ->
    /// XSTS per relying party (titlehub + update). Persists the session encrypted to disk and refreshes
    /// the access token via the MSA refresh token when the XSTS tokens expire.
    /// </summary>
    [SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable", Justification = "Signer outlives the manager and is reused for the process lifetime; disposed implicitly at exit.")]
    public sealed class XboxAccountManager
    {
        private readonly IAnsiConsole _ansiConsole;
        private readonly IXboxAuthProvider _authProvider;
        private readonly HttpClient _client;

        private XblRequestSigner _signer;
        private readonly SemaphoreSlim _loginLock = new SemaphoreSlim(1, 1);

        public XboxAccount Account { get; private set; }

        private XboxAccountManager(IAnsiConsole ansiConsole, IXboxAuthProvider authProvider)
        {
            _ansiConsole = ansiConsole;
            _authProvider = authProvider;
            _client = new HttpClient
            {
                Timeout = AppConfig.DefaultRequestTimeout
            };
            _client.DefaultRequestHeaders.Add("User-Agent", AppConfig.DefaultUserAgent);
        }

        public string? DisplayName => Account?.DisplayName;
        public string? Xuid => Account?.Xuid;

        /// <summary>
        /// True when a long-lived MSA refresh token is present. This token (not the short-lived XSTS
        /// tokens) is the real login bound — it slides ~90 days, so as long as it is stored the daemon
        /// can re-mint XSTS tokens without an interactive login.
        /// </summary>
        public bool HasRefreshToken => !string.IsNullOrEmpty(Account?.RefreshToken);

        /// <summary>
        /// Expiry of the currently minted XSTS tokens (the earlier of the titlehub / update token expiries).
        /// This is the short-lived (~16h) bound; null when no tokens have been minted yet.
        /// </summary>
        public DateTime? XstsExpiryUtc
        {
            get
            {
                if (Account == null || string.IsNullOrEmpty(Account.XboxLiveToken) || string.IsNullOrEmpty(Account.UpdateToken))
                {
                    return null;
                }

                var earliest = Account.XboxLiveExpiresAt < Account.UpdateExpiresAt
                    ? Account.XboxLiveExpiresAt
                    : Account.UpdateExpiresAt;
                return DateTime.SpecifyKind(earliest, DateTimeKind.Utc);
            }
        }

        /// <summary>
        /// Expiry of the MSA refresh token (the real re-login bound): the time it was issued / last rotated, plus
        /// <see cref="AppConfig.RefreshTokenValidityDays"/> (default 90, overridable via XBOX_REFRESH_TOKEN_VALIDITY_DAYS).
        /// Null when no refresh token has been stamped yet.
        /// </summary>
        public DateTime? AuthExpiryUtc =>
            Account?.RefreshTokenIssuedUtc is { } issued
                ? DateTime.SpecifyKind(issued, DateTimeKind.Utc).AddDays(AppConfig.RefreshTokenValidityDays)
                : (DateTime?)null;

        /// <summary>The titlehub XBL3.0 authorization header (<c>XBL3.0 x={uhs};{token}</c>).</summary>
        public string TitleHubAuthorizationHeader => $"XBL3.0 x={Account!.XboxLiveUhs};{Account.XboxLiveToken}";

        /// <summary>The update/package XBL3.0 authorization header (<c>XBL3.0 x={uhs};{token}</c>).</summary>
        public string UpdateAuthorizationHeader => $"XBL3.0 x={Account!.UpdateUhs};{Account.UpdateToken}";

        public XblRequestSigner Signer => _signer;

        public bool TokensAreExpired()
        {
            if (Account == null || string.IsNullOrEmpty(Account.XboxLiveToken) || string.IsNullOrEmpty(Account.UpdateToken))
            {
                return true;
            }

            // 10 minute buffer so a token can't expire mid-use
            var now = DateTimeOffset.UtcNow.UtcDateTime;
            return now > Account.XboxLiveExpiresAt.AddMinutes(-10) || now > Account.UpdateExpiresAt.AddMinutes(-10);
        }

        /// <summary>
        /// Ensures the account is logged in and the XSTS tokens are fresh. Reuses the stored session when valid,
        /// refreshes via the MSA refresh token when possible, and falls back to a fresh device-code login.
        /// </summary>
        /// <param name="interactive">
        /// When true (the explicit <c>login</c> command), a failed/absent refresh falls back to an interactive
        /// device-code login. When false (headless callers such as the lazy token-refresh hook), a failed/absent
        /// refresh throws instead of dangling on a device-code prompt nobody can answer.
        /// </param>
        public async Task LoginAsync(bool interactive = true, CancellationToken cancellationToken = default)
        {
            // Fast-path: no lock needed if tokens are still valid.
            if (!TokensAreExpired())
            {
                _ansiConsole.LogMarkupLine("Reusing existing Xbox auth session...");
                return;
            }

            await _loginLock.WaitAsync(cancellationToken);
            try
            {
                // Re-check after acquiring the lock — a concurrent caller may have already refreshed.
                if (!TokensAreExpired())
                {
                    _ansiConsole.LogMarkupLine("Reusing existing Xbox auth session...");
                    return;
                }

                EnsureSigner();

                string accessToken = null;
                if (Account?.RefreshToken != null)
                {
                    _ansiConsole.LogMarkupLine("Refreshing Xbox access token...");
                    if (await TryRefreshAccessTokenAsync(Account.RefreshToken, cancellationToken))
                    {
                        accessToken = _pendingAccessToken;
                        _pendingAccessToken = null;
                    }
                }

                // Fresh device-code login if there was no refresh token or the refresh failed. Headless callers
                // (interactive == false) must not dangle on a device-code prompt nobody can answer — fail loudly
                // so the orchestrator surfaces a clear "re-login required" instead of hanging.
                if (accessToken == null && !interactive)
                {
                    throw new InvalidOperationException(
                        "Xbox re-login required: the saved Microsoft refresh token is expired or invalid. Log in again.");
                }

                accessToken ??= await DeviceCodeLoginAsync(cancellationToken);

                await MintXstsTokensAsync(accessToken, cancellationToken);
                Save();
            }
            finally
            {
                _loginLock.Release();
            }
        }

        /// <summary>
        /// Imports a supplied MSA refresh token and (optional) device identity key (PKCS#8, base64) into the
        /// session, persists them to the encrypted store, and mints fresh XSTS tokens — all without ever
        /// presenting the interactive device-code challenge. Used by the <c>provide-auto-login</c> path so an
        /// orchestrator can log the daemon in non-interactively using credentials it already holds.
        /// </summary>
        public async Task ImportAndLoginAsync(string refreshToken, string deviceKeyPkcs8, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(refreshToken))
            {
                throw new XboxLoginException("A non-empty MSA refresh token is required for non-interactive login.");
            }

            await _loginLock.WaitAsync(cancellationToken);
            try
            {
                Account ??= new XboxAccount();
                Account.RefreshToken = refreshToken;
                // The imported token's true issue time is unknown to us, so stamp now. This is conservative:
                // the reported AuthExpiryUtc (now + 90d) may slightly overstate the real expiry, never understate
                // it less than the remaining life — re-import resets it. A rolling refresh below re-stamps it.
                Account.RefreshTokenIssuedUtc = DateTime.UtcNow;

                // Prefer the caller-supplied device key so the device identity matches what the orchestrator
                // expects; otherwise EnsureSigner generates and persists a fresh one.
                if (!string.IsNullOrEmpty(deviceKeyPkcs8))
                {
                    Account.DeviceKeyPkcs8 = deviceKeyPkcs8;
                    _signer = XblRequestSigner.FromPkcs8Base64(deviceKeyPkcs8);
                }

                EnsureSigner();

                _ansiConsole.LogMarkupLine("Refreshing Xbox access token from imported credentials...");
                if (!await TryRefreshAccessTokenAsync(refreshToken, cancellationToken))
                {
                    // Non-interactive path: never fall back to device-code. Fail loudly instead.
                    throw new XboxLoginException("The imported MSA refresh token was rejected. Re-import a valid refresh token.");
                }

                var accessToken = _pendingAccessToken;
                _pendingAccessToken = null;

                await MintXstsTokensAsync(accessToken, cancellationToken);
                Save();
            }
            finally
            {
                _loginLock.Release();
            }
        }

        private void EnsureSigner()
        {
            if (_signer != null)
            {
                return;
            }

            if (Account?.DeviceKeyPkcs8 != null)
            {
                _signer = XblRequestSigner.FromPkcs8Base64(Account.DeviceKeyPkcs8);
            }
            else
            {
                _signer = XblRequestSigner.CreateNew();
                Account ??= new XboxAccount();
                Account.DeviceKeyPkcs8 = _signer.ExportPkcs8Base64();
            }
        }

        #region MSA device-code flow

        private async Task<string> DeviceCodeLoginAsync(CancellationToken cancellationToken)
        {
            var deviceCode = await RequestDeviceCodeAsync(cancellationToken);
            await _authProvider.PresentDeviceCodeAsync(deviceCode.UserCode, deviceCode.VerificationUri, cancellationToken);

            var msaToken = await PollForTokenAsync(deviceCode, cancellationToken);
            Account ??= new XboxAccount();
            Account.RefreshToken = msaToken.RefreshToken;
            // Fresh refresh token just issued — stamp now so AuthExpiryUtc reports issued + 90d.
            Account.RefreshTokenIssuedUtc = DateTime.UtcNow;
            return msaToken.AccessToken;
        }

        private async Task<DeviceCodeResponse> RequestDeviceCodeAsync(CancellationToken cancellationToken)
        {
            var form = new Dictionary<string, string>
            {
                { "client_id", AppConfig.ClientId },
                { "scope", AppConfig.AuthScope },
                { "response_type", "device_code" }
            };

            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(AppConfig.DeviceCodeUrl))
            {
                Content = new FormUrlEncodedContent(form)
            };
            using var response = await _client.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var deviceCode = await JsonSerializer.DeserializeAsync(stream, SerializationContext.Default.DeviceCodeResponse, cancellationToken);
            if (deviceCode?.DeviceCode == null)
            {
                throw new XboxLoginException("Failed to obtain a device code from Microsoft.");
            }
            return deviceCode;
        }

        private async Task<MsaTokenResponse> PollForTokenAsync(DeviceCodeResponse deviceCode, CancellationToken cancellationToken)
        {
            var interval = TimeSpan.FromSeconds(Math.Max(deviceCode.Interval, 1));
            var deadline = DateTimeOffset.UtcNow.AddSeconds(deviceCode.ExpiresIn);

            while (DateTimeOffset.UtcNow < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await Task.Delay(interval, cancellationToken);

                var form = new Dictionary<string, string>
                {
                    { "client_id", AppConfig.ClientId },
                    { "grant_type", AppConfig.DeviceCodeGrantType },
                    { "device_code", deviceCode.DeviceCode }
                };

                var token = await PostTokenFormAsync(form, cancellationToken);
                if (token.AccessToken != null)
                {
                    return token;
                }

                // authorization_pending / slow_down => keep polling. Anything else is fatal.
                if (token.Error == "slow_down")
                {
                    interval = interval.Add(TimeSpan.FromSeconds(5));
                }
                else if (token.Error != null && token.Error != "authorization_pending")
                {
                    throw new XboxLoginException($"Xbox device-code login failed: {token.Error}");
                }
            }

            throw new XboxLoginException("Xbox device-code login timed out. Please try again.");
        }

        private async Task<bool> TryRefreshAccessTokenAsync(string refreshToken, CancellationToken cancellationToken)
        {
            var form = new Dictionary<string, string>
            {
                { "client_id", AppConfig.ClientId },
                { "grant_type", "refresh_token" },
                { "refresh_token", refreshToken }
            };

            try
            {
                var token = await PostTokenFormAsync(form, cancellationToken);
                if (token.AccessToken == null)
                {
                    return false;
                }

                if (token.RefreshToken != null)
                {
                    Account!.RefreshToken = token.RefreshToken;
                    // Rolling refresh token rotated — re-stamp so the 90d sliding window restarts from now.
                    Account.RefreshTokenIssuedUtc = DateTime.UtcNow;
                }

                // Cache the access token on the instance so MintXstsTokensAsync can read it.
                _pendingAccessToken = token.AccessToken;
                return true;
            }
            catch (HttpRequestException)
            {
                return false;
            }
        }

        private string _pendingAccessToken;

        private async Task<MsaTokenResponse> PostTokenFormAsync(Dictionary<string, string> form, CancellationToken cancellationToken)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(AppConfig.TokenUrl))
            {
                Content = new FormUrlEncodedContent(form)
            };
            using var response = await _client.SendAsync(request, cancellationToken);

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var token = await JsonSerializer.DeserializeAsync(stream, SerializationContext.Default.MsaTokenResponse, cancellationToken);
            return token ?? new MsaTokenResponse();
        }

        #endregion

        #region XBL token chain

        private async Task MintXstsTokensAsync(string accessToken, CancellationToken cancellationToken)
        {
            var userToken = await AuthenticateUserAsync(accessToken, cancellationToken);
            var deviceToken = await AuthenticateDeviceAsync(cancellationToken);

            // Titlehub audience: user token only (device token optional).
            var titleHubXsts = await AuthorizeXstsAsync(userToken, deviceToken: null, AppConfig.TitleHubRelyingParty, signed: false, cancellationToken);
            // Update audience: device-bearing + signed (mandatory, else GetBasePackage 403s).
            var updateXsts = await AuthorizeXstsAsync(userToken, deviceToken, AppConfig.UpdateRelyingParty, signed: true, cancellationToken);

            Account ??= new XboxAccount();
            var titleClaims = titleHubXsts.DisplayClaims?.Xui?.FirstOrDefault();
            var updateClaims = updateXsts.DisplayClaims?.Xui?.FirstOrDefault();

            Account.XboxLiveToken = titleHubXsts.Token;
            Account.XboxLiveUhs = titleClaims?.Uhs;
            Account.XboxLiveExpiresAt = titleHubXsts.NotAfter;
            Account.UpdateToken = updateXsts.Token;
            Account.UpdateUhs = updateClaims?.Uhs;
            Account.UpdateExpiresAt = updateXsts.NotAfter;
            Account.Xuid = titleClaims?.Xid ?? Account.Xuid;
        }

        private async Task<string> AuthenticateUserAsync(string accessToken, CancellationToken cancellationToken)
        {
            var body = new XblUserAuthRequest
            {
                Properties = new XblUserAuthProperties { RpsTicket = accessToken }
            };
            var bodyBytes = JsonSerializer.SerializeToUtf8Bytes(body, SerializationContext.Default.XblUserAuthRequest);

            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(AppConfig.UserAuthUrl));
            request.Headers.Add("x-xbl-contract-version", "1");
            request.Content = new ByteArrayContent(bodyBytes);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

            using var response = await _client.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var result = await JsonSerializer.DeserializeAsync(stream, SerializationContext.Default.XblAuthResponse, cancellationToken);
            if (result?.Token == null)
            {
                throw new XboxLoginException("Failed to obtain the Xbox user token.");
            }
            return result.Token;
        }

        private async Task<string> AuthenticateDeviceAsync(CancellationToken cancellationToken)
        {
            var body = new XblDeviceAuthRequest
            {
                Properties = new XblDeviceAuthProperties
                {
                    Id = $"{{{Guid.NewGuid()}}}",
                    ProofKey = _signer.GetProofKey()
                }
            };
            var bodyBytes = JsonSerializer.SerializeToUtf8Bytes(body, SerializationContext.Default.XblDeviceAuthRequest);

            var uri = new Uri(AppConfig.DeviceAuthUrl);
            var signature = _signer.Sign("POST", uri.PathAndQuery, string.Empty, bodyBytes);

            using var request = new HttpRequestMessage(HttpMethod.Post, uri);
            request.Headers.Add("x-xbl-contract-version", "1");
            request.Headers.Add("Signature", signature);
            request.Content = new ByteArrayContent(bodyBytes);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

            using var response = await _client.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var result = await JsonSerializer.DeserializeAsync(stream, SerializationContext.Default.XblAuthResponse, cancellationToken);
            if (result?.Token == null)
            {
                throw new XboxLoginException("Failed to obtain the Xbox device token.");
            }
            return result.Token;
        }

        private async Task<XstsTokenResponse> AuthorizeXstsAsync(string userToken, string deviceToken, string relyingParty, bool signed, CancellationToken cancellationToken)
        {
            var body = new XstsAuthRequest
            {
                RelyingParty = relyingParty,
                Properties = new XstsAuthProperties
                {
                    UserTokens = new[] { userToken },
                    DeviceToken = deviceToken
                }
            };
            var bodyBytes = JsonSerializer.SerializeToUtf8Bytes(body, SerializationContext.Default.XstsAuthRequest);

            var uri = new Uri(AppConfig.XstsAuthUrl);
            using var request = new HttpRequestMessage(HttpMethod.Post, uri);
            request.Headers.Add("x-xbl-contract-version", "1");
            if (signed)
            {
                request.Headers.Add("Signature", _signer.Sign("POST", uri.PathAndQuery, string.Empty, bodyBytes));
            }
            request.Content = new ByteArrayContent(bodyBytes);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

            using var response = await _client.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var result = await JsonSerializer.DeserializeAsync(stream, SerializationContext.Default.XstsTokenResponse, cancellationToken);
            if (result?.Token == null)
            {
                throw new XboxLoginException($"Failed to obtain the XSTS token for {relyingParty}.");
            }
            return result;
        }

        #endregion

        #region Persistence

        public static XboxAccountManager LoadFromFile(IAnsiConsole ansiConsole, IXboxAuthProvider authProvider)
        {
            var manager = new XboxAccountManager(ansiConsole, authProvider);

            if (!File.Exists(AppConfig.AccountSettingsStorePath))
            {
                return manager;
            }

            var rawContent = File.ReadAllText(AppConfig.AccountSettingsStorePath).Trim();
            if (TokenStorageEncryption.IsEncrypted(rawContent))
            {
                var json = TokenStorageEncryption.Decrypt(rawContent);
                manager.Account = JsonSerializer.Deserialize(json, SerializationContext.Default.XboxAccount);
            }
            else
            {
                ansiConsole.LogMarkupLine("Migrating account credentials to encrypted storage...");
                manager.Account = JsonSerializer.Deserialize(rawContent, SerializationContext.Default.XboxAccount);
                manager.Save();
            }

            return manager;
        }

        private void Save()
        {
            if (Account == null)
            {
                return;
            }

            var json = JsonSerializer.Serialize(Account, SerializationContext.Default.XboxAccount);
            var encrypted = TokenStorageEncryption.Encrypt(json);
            File.WriteAllText(AppConfig.AccountSettingsStorePath, encrypted);
            TokenStorageEncryption.SetRestrictivePermissions(AppConfig.AccountSettingsStorePath);
        }

        #endregion
    }
}
