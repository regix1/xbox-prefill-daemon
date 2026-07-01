using System.Collections.Generic;
using XboxPrefill.Handlers;

namespace XboxPrefill.Test
{
    /// <summary>
    /// Locks the permalink-parsing behind the "Top" preset's most-played-games lookup. The daemon has no
    /// documented JSON API for this ranking, so it parses Microsoft's public storefront page HTML directly
    /// (see <see cref="XboxTrendingTitlesProvider"/>) - these tests use the real permalink shape observed on
    /// https://www.microsoft.com/en-us/store/most-popular/games/xbox (fortnite/bt5p2x999vh2 etc.) and confirm
    /// the graceful-degradation contract: unrecognized markup returns an empty list rather than throwing, so
    /// a page-layout change can never crash the daemon.
    /// </summary>
    public sealed class XboxTrendingTitlesProviderTests
    {
        [Fact]
        public void ExtractProductIds_RealisticPageMarkup_ReturnsIdsInRankOrder()
        {
            const string html = """
                <ul class="tileList">
                  <li><img src="cover1.jpg"><h3><a href="https://www.microsoft.com/en-us/p/fortnite/bt5p2x999vh2">Fortnite</a></h3></li>
                  <li><img src="cover2.jpg"><h3><a href="https://www.microsoft.com/en-us/p/roblox-xbox/bq1tn1t79v9k">Roblox - Xbox</a></h3></li>
                  <li><img src="cover3.jpg"><h3><a href="https://www.microsoft.com/en-us/p/minecraft/9mvxmvt8zkwc">Minecraft</a></h3></li>
                  <li><img src="cover4.jpg"><h3><a href="https://www.microsoft.com/en-us/p/call-of-duty/9n201kqxs5bm">Call of Duty</a></h3></li>
                </ul>
                """;

            var productIds = XboxTrendingTitlesProvider.ExtractProductIds(html, maxItems: 25);

            Assert.Equal(
                new List<string> { "BT5P2X999VH2", "BQ1TN1T79V9K", "9MVXMVT8ZKWC", "9N201KQXS5BM" },
                productIds);
        }

        [Fact]
        public void ExtractProductIds_SameProductLinkedTwice_DeduplicatesAtFirstRank()
        {
            // Real markup links both the cover image and the title text to the same product permalink.
            const string html = """
                <li>
                  <a href="/en-us/p/fortnite/bt5p2x999vh2"><img src="cover1.jpg"></a>
                  <a href="/en-us/p/fortnite/bt5p2x999vh2">Fortnite</a>
                </li>
                <li><a href="/en-us/p/minecraft/9mvxmvt8zkwc">Minecraft</a></li>
                """;

            var productIds = XboxTrendingTitlesProvider.ExtractProductIds(html, maxItems: 25);

            Assert.Equal(new List<string> { "BT5P2X999VH2", "9MVXMVT8ZKWC" }, productIds);
        }

        [Fact]
        public void ExtractProductIds_MoreMatchesThanMaxItems_TruncatesToMaxItems()
        {
            const string html = """
                <a href="/en-us/p/a/bt5p2x999vh2">A</a>
                <a href="/en-us/p/b/bq1tn1t79v9k">B</a>
                <a href="/en-us/p/c/9mvxmvt8zkwc">C</a>
                """;

            var productIds = XboxTrendingTitlesProvider.ExtractProductIds(html, maxItems: 2);

            Assert.Equal(new List<string> { "BT5P2X999VH2", "BQ1TN1T79V9K" }, productIds);
        }

        [Theory]
        [InlineData("<html><body>Microsoft has redesigned this page and it no longer has any /p/ links.</body></html>")]
        [InlineData("")]
        [InlineData(null)]
        public void ExtractProductIds_UnrecognizedOrEmptyMarkup_ReturnsEmptyListInsteadOfThrowing(string html)
        {
            var productIds = XboxTrendingTitlesProvider.ExtractProductIds(html, maxItems: 25);

            Assert.Empty(productIds);
        }
    }
}
