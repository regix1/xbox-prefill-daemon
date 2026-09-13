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
#pragma warning disable CA2227 // System.Text.Json replaces Xbox API response collections.
        public List<DisplayCatalogProduct> Products { get; set; }
#pragma warning restore CA2227
    }

    public sealed class DisplayCatalogProduct
    {
        [JsonPropertyName("ProductId")]
        public string ProductId { get; set; }

        [JsonPropertyName("LastModifiedDate")]
        public DateTime LastModifiedDate { get; set; }

        [JsonPropertyName("DisplaySkuAvailabilities")]
#pragma warning disable CA2227 // System.Text.Json replaces Xbox API response collections.
        public List<DisplaySkuAvailability> DisplaySkuAvailabilities { get; set; }
#pragma warning restore CA2227
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
#pragma warning disable CA2227 // System.Text.Json replaces Xbox API response collections.
        public List<DisplayCatalogPackage> Packages { get; set; }
#pragma warning restore CA2227
    }

    public sealed class DisplayCatalogPackage
    {
        [JsonPropertyName("ContentId")]
        public string ContentId { get; set; }

        [JsonPropertyName("PackageFormat")]
        public string PackageFormat { get; set; }
    }
}
