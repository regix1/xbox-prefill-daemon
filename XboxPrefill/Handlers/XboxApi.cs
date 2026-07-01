namespace XboxPrefill.Handlers
{
    /// <summary>
    /// Talks to the Xbox Live APIs: titlehub (the account's title library), DisplayCatalog
    /// (ProductId -> ContentId, anonymous), and the package service (ContentId -> PackageFiles, signed).
    /// Methods return the API data with minimal transformation. All flows are PROVEN live.
    /// </summary>
    public sealed class XboxApi
    {
        private readonly IAnsiConsole _ansiConsole;
        private readonly HttpClientFactory _httpClientFactory;

        public XboxApi(IAnsiConsole ansiConsole, HttpClientFactory httpClientFactory)
        {
            _ansiConsole = ansiConsole;
            _httpClientFactory = httpClientFactory;
        }

        /// <summary>
        /// Enumerates the account's prefillable titles via titlehub. Only MS-Store/Xbox titles
        /// (those with a non-null pfn AND productId) are returned; non-Store titles are excluded.
        /// </summary>
        public async Task<List<TitleHubTitle>> GetOwnedTitlesAsync()
        {
            _ansiConsole.LogMarkupLine("Retrieving owned titles from titlehub");
            var timer = Stopwatch.StartNew();

            // Token refresh is authoritative in GetHttpClientAsync — no manual check needed here.
            var account = _httpClientFactory.AccountManager;
            var xuid = account.Xuid;
            if (string.IsNullOrEmpty(xuid))
            {
                throw new XboxLoginException("No Xbox user id available. Login may have failed.");
            }

            // "titleHistory" adds a per-title lastTimePlayed/visible/canHide block (xbox-webapi-python's
            // TitlehubProvider models this as recently-played metadata); needed to back the "Recent" preset.
            var url = $"{AppConfig.TitleHubBaseUrl}/users/xuid({xuid})/titles/titlehistory/decoration/detail,image,productId,gamepass,titleHistory";

            // Refresh the token FIRST, then read the (now-fresh) authorization header.
            var httpClient = await _httpClientFactory.GetHttpClientAsync();
            var freshAccount = _httpClientFactory.AccountManager;

            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(url));
            request.Headers.Add("Authorization", freshAccount.TitleHubAuthorizationHeader);
            request.Headers.Add("x-xbl-contract-version", "2");
            request.Headers.Add("Accept-Language", "en-US");
            using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();

            await using var stream = await response.Content.ReadAsStreamAsync();
            var titleHub = await JsonSerializer.DeserializeAsync(stream, SerializationContext.Default.TitleHubResponse);

            var prefillable = (titleHub?.Titles ?? new List<TitleHubTitle>())
                .Where(t => !string.IsNullOrEmpty(t.Pfn) && !string.IsNullOrEmpty(t.ProductId))
                .GroupBy(t => t.ProductId, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            _ansiConsole.LogMarkupLine($"Retrieved {Magenta(prefillable.Count)} prefillable titles", timer);
            return prefillable;
        }

        /// <summary>
        /// Resolves a Store ProductId to its package ContentId(s) via the anonymous DisplayCatalog endpoint.
        /// A product may expose several SKUs/packages; all ContentIds are collected.
        /// </summary>
        public async Task<List<string>> GetContentIdsAsync(string productId)
        {
            var url = $"{AppConfig.DisplayCatalogBaseUrl}/v7.0/products?bigIds={productId}&market=US&languages=en-US,neutral&fieldsTemplate=details";
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(url));

            // DisplayCatalog is anonymous — use the shared anonymous client, no token refresh required.
            using var response = await _httpClientFactory.AnonymousClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();

            await using var stream = await response.Content.ReadAsStreamAsync();
            var catalog = await JsonSerializer.DeserializeAsync(stream, SerializationContext.Default.DisplayCatalogResponse);

            var contentIds = new List<string>();
            foreach (var product in catalog?.Products ?? new List<DisplayCatalogProduct>())
            {
                foreach (var skuAvailability in product.DisplaySkuAvailabilities ?? new List<DisplaySkuAvailability>())
                {
                    foreach (var package in skuAvailability.Sku?.Properties?.Packages ?? new List<DisplayCatalogPackage>())
                    {
                        if (!string.IsNullOrEmpty(package.ContentId) && !contentIds.Contains(package.ContentId, StringComparer.OrdinalIgnoreCase))
                        {
                            contentIds.Add(package.ContentId);
                        }
                    }
                }
            }

            return contentIds;
        }

        /// <summary>
        /// Fetches the base package (PackageFiles + version) for a ContentId from the package service.
        /// Requires the device-bearing update token AND a per-request Signature (else the service 403s).
        /// </summary>
        public async Task<GetBasePackageResponse> GetBasePackageAsync(string contentId)
        {
            // Token refresh is authoritative in GetHttpClientAsync — no manual check needed here.
            var httpClient = await _httpClientFactory.GetHttpClientAsync();

            var account = _httpClientFactory.AccountManager;
            var url = $"{AppConfig.PackageServiceBaseUrl}{contentId}";
            var uri = new Uri(url);
            var authHeader = account.UpdateAuthorizationHeader;
            var signature = account.Signer.Sign("GET", uri.PathAndQuery, authHeader, Array.Empty<byte>());

            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.Add("Authorization", authHeader);
            request.Headers.Add("Signature", signature);
            request.Headers.Add("x-xbl-contract-version", "1");

            using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();

            await using var stream = await response.Content.ReadAsStreamAsync();
            var package = await JsonSerializer.DeserializeAsync(stream, SerializationContext.Default.GetBasePackageResponse);
            return package ?? new GetBasePackageResponse { PackageFound = false };
        }
    }
}
