namespace XboxPrefill.Handlers
{
    /// <summary>
    /// Manages a single long-lived <see cref="HttpClient"/> (backed by <see cref="SocketsHttpHandler"/>) for
    /// the Xbox APIs and ensures the account's XSTS tokens are fresh before each call. The actual
    /// <c>Authorization: XBL3.0 ...</c> + <c>Signature</c> headers are applied per-request by
    /// <see cref="XboxApi"/> (audience- and signature-specific); they are never set on the shared client's
    /// default headers to avoid cross-request header bleed.
    /// </summary>
    public sealed class HttpClientFactory : IDisposable
    {
        private readonly IAnsiConsole _ansiConsole;
        private readonly XboxAccountManager _accountManager;

        // Single long-lived client — avoids socket exhaustion during parallel downloads.
        private readonly HttpClient _sharedClient;

        // Separate anonymous client for DisplayCatalog (no auth, no token refresh needed).
        public readonly HttpClient AnonymousClient;

        public HttpClientFactory(IAnsiConsole ansiConsole, XboxAccountManager accountManager)
        {
            _ansiConsole = ansiConsole;
            _accountManager = accountManager;

            var handler = new SocketsHttpHandler
            {
                PooledConnectionLifetime = TimeSpan.FromMinutes(5),
                PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
            };
            _sharedClient = new HttpClient(handler)
            {
                Timeout = AppConfig.DefaultRequestTimeout
            };
            _sharedClient.DefaultRequestHeaders.Add("User-Agent", AppConfig.DefaultUserAgent);

            var anonHandler = new SocketsHttpHandler
            {
                PooledConnectionLifetime = TimeSpan.FromMinutes(5),
                PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
            };
            AnonymousClient = new HttpClient(anonHandler)
            {
                Timeout = AppConfig.DefaultRequestTimeout
            };
            AnonymousClient.DefaultRequestHeaders.Add("User-Agent", AppConfig.DefaultUserAgent);
        }

        public XboxAccountManager AccountManager => _accountManager;

        /// <summary>
        /// Ensures tokens are fresh, then returns the shared base client.
        /// Per-request <c>Authorization</c> and <c>Signature</c> headers must be set on the
        /// <see cref="HttpRequestMessage"/>, NOT on the returned client's default headers.
        /// </summary>
        public async Task<HttpClient> GetHttpClientAsync(CancellationToken cancellationToken = default)
        {
            if (_accountManager.TokensAreExpired())
            {
                await _accountManager.LoginAsync(cancellationToken);
            }

            return _sharedClient;
        }

        public void Dispose()
        {
            _sharedClient.Dispose();
            AnonymousClient.Dispose();
        }
    }
}
