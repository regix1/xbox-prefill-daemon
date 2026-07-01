namespace XboxPrefill.Models.ApiResponses
{
    /// <summary>
    /// Response from the titlehub <c>titlehistory</c> endpoint. Lists the account's titles. Only titles with a
    /// non-null <see cref="TitleHubTitle.Pfn"/> and <see cref="TitleHubTitle.ProductId"/> are MS-Store/Xbox
    /// titles that can be prefilled.
    /// </summary>
    public sealed class TitleHubResponse
    {
        [JsonPropertyName("titles")]
        public List<TitleHubTitle> Titles { get; set; }
    }

    public sealed class TitleHubTitle
    {
        [JsonPropertyName("titleId")]
        public string TitleId { get; set; }

        /// <summary>Package family name. Null for non-Store titles (e.g. Steam/Epic games played on PC).</summary>
        [JsonPropertyName("pfn")]
        public string Pfn { get; set; }

        [JsonPropertyName("name")]
        public string Name { get; set; }

        [JsonPropertyName("type")]
        public string Type { get; set; }

        /// <summary>Store product id (big id). The cache key used to resolve a downloadable package.</summary>
        [JsonPropertyName("productId")]
        public string ProductId { get; set; }

        [JsonPropertyName("gamePass")]
        public TitleHubGamePass GamePass { get; set; }

        /// <summary>Play-recency metadata, present only when the <c>titleHistory</c> decoration is requested.</summary>
        [JsonPropertyName("titleHistory")]
        public TitleHubTitleHistory TitleHistory { get; set; }
    }

    public sealed class TitleHubGamePass
    {
        [JsonPropertyName("isGamePass")]
        public bool IsGamePass { get; set; }
    }

    /// <summary>
    /// Per-title play recency, returned by titlehub when the <c>titleHistory</c> decoration is requested
    /// (see <see cref="Handlers.XboxApi.GetOwnedTitlesAsync"/>).
    /// </summary>
    public sealed class TitleHubTitleHistory
    {
        [JsonPropertyName("lastTimePlayed")]
        public DateTimeOffset? LastTimePlayed { get; set; }

        [JsonPropertyName("visible")]
        public bool Visible { get; set; }

        [JsonPropertyName("canHide")]
        public bool CanHide { get; set; }
    }
}
