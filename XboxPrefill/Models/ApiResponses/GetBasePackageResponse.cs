namespace XboxPrefill.Models.ApiResponses
{
    /// <summary>
    /// Response from <c>packagespc.xboxlive.com/GetBasePackage/{ContentId}</c>. Lists every file that makes up
    /// the package, each with its CDN root paths + relative url. Ported from MsixvcPackageDownloader.
    /// </summary>
    public sealed class GetBasePackageResponse
    {
        [JsonPropertyName("PackageFound")]
        public bool PackageFound { get; set; }

        [JsonPropertyName("ContentId")]
        public string ContentId { get; set; }

        [JsonPropertyName("VersionId")]
        public string VersionId { get; set; }

        [JsonPropertyName("Version")]
        public string Version { get; set; }

        [JsonPropertyName("PackageFiles")]
        public List<PackageFile> PackageFiles { get; set; }
    }

    public sealed class PackageFile
    {
        [JsonPropertyName("FileName")]
        public string FileName { get; set; }

        [JsonPropertyName("FileSize")]
        public ulong FileSize { get; set; }

        [JsonPropertyName("RelativeUrl")]
        public string RelativeUrl { get; set; }

        [SuppressMessage("Performance", "CA1819:Properties should not return arrays", Justification = "Direct mapping of GetBasePackage JSON response.")]
        [JsonPropertyName("CdnRootPaths")]
        public string[] CdnRootPaths { get; set; }
    }
}
