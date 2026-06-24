namespace XboxPrefill.Models.ApiResponses
{
    /// <summary>
    /// Response from the anonymous DisplayCatalog products endpoint. The ContentId(s) needed by GetBasePackage
    /// live under <c>Products[].DisplaySkuAvailabilities[].Sku.Properties.Packages[].ContentId</c>. A product may
    /// expose several SKUs/packages; a bundle edition may carry none (resolve its base game then).
    /// </summary>
    public sealed class DisplayCatalogResponse
    {
        [JsonPropertyName("Products")]
        public List<DisplayCatalogProduct> Products { get; set; }
    }

    public sealed class DisplayCatalogProduct
    {
        [JsonPropertyName("ProductId")]
        public string ProductId { get; set; }

        [JsonPropertyName("LastModifiedDate")]
        public DateTime LastModifiedDate { get; set; }

        [JsonPropertyName("DisplaySkuAvailabilities")]
        public List<DisplaySkuAvailability> DisplaySkuAvailabilities { get; set; }
    }

    public sealed class DisplaySkuAvailability
    {
        [JsonPropertyName("Sku")]
        public DisplayCatalogSku Sku { get; set; }
    }

    public sealed class DisplayCatalogSku
    {
        [JsonPropertyName("Properties")]
        public DisplayCatalogSkuProperties Properties { get; set; }
    }

    public sealed class DisplayCatalogSkuProperties
    {
        [JsonPropertyName("Packages")]
        public List<DisplayCatalogPackage> Packages { get; set; }
    }

    public sealed class DisplayCatalogPackage
    {
        [JsonPropertyName("ContentId")]
        public string ContentId { get; set; }

        [JsonPropertyName("PackageFormat")]
        public string PackageFormat { get; set; }
    }
}
