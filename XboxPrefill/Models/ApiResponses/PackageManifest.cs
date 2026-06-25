namespace XboxPrefill.Models.ApiResponses
{
    /// <summary>
    /// The result of resolving an app to a downloadable Xbox package. Carries the CDN host (for lancache
    /// resolution + the upstream <c>Host</c> header) and the version string. This is the Xbox equivalent of
    /// a manifest URL and is the source for the lancache fill loop's upstream CDN.
    /// </summary>
    public sealed class PackageManifest
    {
        /// <summary>The CDN host, e.g. <c>assets1.xboxlive.com</c>, taken from the package's CdnRootPaths.</summary>
        public string CdnHost { get; set; } = string.Empty;

        /// <summary>The full CDN root url (scheme + host), used as the upstream CDN for the fill loop.</summary>
        public string CdnRootUrl { get; set; } = string.Empty;

        /// <summary>The package version reported by GetBasePackage. Used as the up-to-date marker.</summary>
        public string Version { get; set; } = string.Empty;

        /// <summary>The download queue: one entry per package file, with a lancache-relative path + size.</summary>
        public List<QueuedRequest> QueuedRequests { get; set; } = new();

        /// <summary>The upstream CDN host, exposed for the lancache fill loop (used by the lancache fill loop as the upstream CDN).</summary>
        public Uri ManifestDownloadUri
        {
            get
            {
                if (string.IsNullOrEmpty(CdnRootUrl))
                {
                    throw new ManifestException("Package manifest has no CDN root URL. The package service returned an empty CdnRootPaths — cannot proceed with lancache fill.");
                }
                return new Uri(CdnRootUrl);
            }
        }

        /// <summary>The upstream CDN url, exposed for the lancache fill loop.</summary>
        public string ManifestDownloadUrl => CdnRootUrl;

        /// <summary>The base path on the CDN. Informational; used for the CDN-info API.</summary>
        public string ChunkBaseUrl => "/";

        /// <summary>
        /// The stable per-file path fragments (path only, query string stripped) extracted from
        /// the package manifest. Each entry is a <c>/filestreamingservice/files/&lt;36-char-GUID&gt;</c>
        /// path that the lancache manager uses to map cache hits back to this product.
        /// Populated by <see cref="XboxPrefill.Handlers.ManifestHandler.ResolvePackageAsync"/>.
        /// </summary>
        public List<string> FilePathFragments { get; set; } = new();
    }
}
