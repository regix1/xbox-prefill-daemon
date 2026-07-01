namespace XboxPrefill.Handlers
{
    /// <summary>
    /// Best-effort "most played" Xbox ranking, sourced from Microsoft's own public storefront page
    /// (<see cref="AppConfig.MostPlayedGamesPageUrl"/>) rather than any documented API - no JSON endpoint
    /// for this ranking is publicly known. The page is server-rendered (confirmed live: a plain anonymous
    /// GET returns the full ranked title list with no JavaScript execution required), so each title's Store
    /// ProductId is recovered from its <c>/p/&lt;slug&gt;/&lt;productId&gt;</c> permalink, in the page's own
    /// rank order.
    /// <para>
    /// Microsoft can change this page's markup at any time without notice, since it is unofficial and
    /// unversioned. Every failure mode (network error, non-success status, zero permalinks recognized) is
    /// swallowed here and reported as an EMPTY result rather than thrown, so a "Top" prefill run degrades to
    /// the documented fallback in <see cref="XboxManager"/> instead of crashing the whole operation.
    /// </para>
    /// </summary>
    public sealed class XboxTrendingTitlesProvider
    {
        private readonly IAnsiConsole _ansiConsole;
        private readonly HttpClient _anonymousClient;

        // Recovers the ProductId segment from a Store product permalink, e.g.
        // "https://www.microsoft.com/en-us/p/fortnite/bt5p2x999vh2" -> "bt5p2x999vh2" (verified against the
        // live page). Scheme/host and the locale segment are optional so this matches both absolute and
        // root-relative hrefs. A match timeout guards against catastrophic backtracking, since this runs
        // against a third-party page this daemon does not control.
        private static readonly Regex ProductPermalinkRegex = new Regex(
            @"(?:/[a-z]{2}-[a-z]{2})?/p/[^""'/?#]+/([a-z0-9]{11,14})(?=[""'/?#]|$)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase,
            TimeSpan.FromSeconds(5));

        public XboxTrendingTitlesProvider(IAnsiConsole ansiConsole, HttpClient anonymousClient)
        {
            _ansiConsole = ansiConsole;
            _anonymousClient = anonymousClient;
        }

        /// <summary>
        /// Fetches Microsoft's public "Most played games" storefront page and returns up to
        /// <paramref name="maxItems"/> Store ProductIds, most-played first. Returns an empty list (never
        /// throws) on any network failure or if the page no longer matches the expected permalink shape -
        /// callers must treat an empty result as "unavailable" and apply their own documented fallback.
        /// </summary>
        public async Task<List<string>> GetTrendingProductIdsAsync(int maxItems, CancellationToken cancellationToken = default)
        {
            string html;
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(AppConfig.MostPlayedGamesPageUrl));
                using var response = await _anonymousClient.SendAsync(request, cancellationToken);
                response.EnsureSuccessStatusCode();
                html = await response.Content.ReadAsStringAsync(cancellationToken);
            }
            // A user cancellation (token signalled) must propagate so the socket "CANCEL" works, but an
            // HttpClient timeout also surfaces as OperationCanceledException with the token NOT signalled -
            // that is a network failure and must degrade to the documented empty-result fallback, not crash
            // the whole "Top" prefill run.
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                _ansiConsole.LogMarkupLine($"[yellow]Failed to fetch Microsoft's most-played games page: {ex.Message}[/]");
                return new List<string>();
            }

            List<string> productIds;
            try
            {
                productIds = ExtractProductIds(html, maxItems);
            }
            catch (RegexMatchTimeoutException)
            {
                // The 5s match timeout guards a third-party page this daemon does not control. If it ever
                // trips, treat it as "unavailable" (empty) so the caller applies its fallback, matching the
                // never-throws contract, rather than letting the exception crash the prefill run.
                _ansiConsole.LogMarkupLine("[yellow]Parsing Microsoft's most-played games page timed out; its layout may have changed.[/]");
                return new List<string>();
            }

            if (productIds.Count == 0)
            {
                _ansiConsole.LogMarkupLine("[yellow]Microsoft's most-played games page returned no recognizable product listings - its layout may have changed.[/]");
            }

            return productIds;
        }

        /// <summary>
        /// Pure parsing step, split out from <see cref="GetTrendingProductIdsAsync"/> so the permalink
        /// extraction can be unit tested without a live HTTP call. Preserves document order (Microsoft's
        /// display rank) and de-duplicates a title that appears more than once (e.g. an image link and a
        /// title link pointing at the same permalink).
        /// </summary>
        public static List<string> ExtractProductIds(string html, int maxItems)
        {
            var productIds = new List<string>();
            if (string.IsNullOrEmpty(html) || maxItems <= 0)
            {
                return productIds;
            }

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (Match match in ProductPermalinkRegex.Matches(html))
            {
                var productId = match.Groups[1].Value.ToUpperInvariant();
                if (seen.Add(productId))
                {
                    productIds.Add(productId);
                    if (productIds.Count >= maxItems)
                    {
                        break;
                    }
                }
            }

            return productIds;
        }
    }
}
