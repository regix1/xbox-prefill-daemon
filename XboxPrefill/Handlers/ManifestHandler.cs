namespace XboxPrefill.Handlers
{
    /// <summary>
    /// Resolves an app (by Store ProductId) to a downloadable <see cref="PackageManifest"/>:
    /// ProductId -> ContentId(s) (DisplayCatalog) -> GetBasePackage -> a <see cref="QueuedRequest"/> per file.
    /// Files ending in <c>.phf</c> / <c>.xsp</c> are skipped. The download url stored on each request is the
    /// lancache-relative path (RelativeUrl); the upstream CDN host travels on the manifest.
    /// </summary>
    public sealed class ManifestHandler
    {
        private static readonly string[] ExcludedExtensions = { ".phf", ".xsp" };

        private readonly IAnsiConsole _ansiConsole;
        private readonly XboxApi _xboxApi;

        public ManifestHandler(IAnsiConsole ansiConsole, XboxApi xboxApi)
        {
            _ansiConsole = ansiConsole;
            _xboxApi = xboxApi;
        }

        /// <summary>
        /// Resolves the app's ProductId to a package manifest containing the full download queue + CDN host.
        /// </summary>
        public async Task<PackageManifest> ResolvePackageAsync(AppInfo app)
        {
            var contentIds = await _xboxApi.GetContentIdsAsync(app.AppId);
            if (contentIds.Count == 0)
            {
                throw new ManifestException($"No package ContentId found for product {app.AppId} ({app.Title}). It may be a bundle edition with no direct package.");
            }

            var manifest = new PackageManifest();
            var queue = new List<QueuedRequest>();

            foreach (var contentId in contentIds)
            {
                var package = await _xboxApi.GetBasePackageAsync(contentId);
                if (!package.PackageFound || package.PackageFiles == null)
                {
                    continue;
                }

                if (string.IsNullOrEmpty(manifest.Version) && !string.IsNullOrEmpty(package.Version))
                {
                    manifest.Version = package.Version;
                }

                foreach (var file in package.PackageFiles)
                {
                    if (ShouldSkip(file))
                    {
                        continue;
                    }

                    // Join the FULL CDN root (scheme + host + ANY path) with the file's RelativeUrl, exactly as the
                    // spec requires: CdnRootPaths[0].TrimEnd('/') + "/" + RelativeUrl.TrimStart('/'). Any path
                    // component carried by CdnRootPaths[0] must be preserved (it is part of the upstream path).
                    var cdnRoot = file.CdnRootPaths![0].TrimEnd('/');
                    var fullUpstreamUrl = $"{cdnRoot}/{file.RelativeUrl.TrimStart('/')}";
                    var upstreamUri = new Uri(fullUpstreamUrl);

                    // Capture the FIRST file's host/root only as the seed for lancache IP resolution; the per-file
                    // Host header travels on each QueuedRequest below (different files can have different hosts).
                    if (string.IsNullOrEmpty(manifest.CdnRootUrl))
                    {
                        manifest.CdnRootUrl = $"{upstreamUri.Scheme}://{upstreamUri.Host}";
                        manifest.CdnHost = upstreamUri.Host;
                    }

                    // The lancache fetch: GET http://{lancacheIp}{PathAndQuery} with Host = this file's CDN host.
                    queue.Add(new QueuedRequest
                    {
                        DownloadUrl = upstreamUri.PathAndQuery,
                        UpstreamHost = upstreamUri.Host,
                        DownloadSizeBytes = file.FileSize
                    });
                }
            }

            if (queue.Count == 0)
            {
                throw new ManifestException($"Package for product {app.AppId} ({app.Title}) contained no downloadable files.");
            }

            manifest.QueuedRequests = queue;
            _ansiConsole.LogMarkupVerbose($"Resolved {LightYellow(queue.Count)} package files for {Magenta(app.Title)}");
            return manifest;
        }

        private static bool ShouldSkip(PackageFile file)
        {
            if (string.IsNullOrEmpty(file.FileName)
                || string.IsNullOrEmpty(file.RelativeUrl)
                || file.CdnRootPaths == null
                || file.CdnRootPaths.Length == 0
                || string.IsNullOrEmpty(file.CdnRootPaths[0]))
            {
                return true;
            }
            return ExcludedExtensions.Any(ext => file.FileName.EndsWith(ext, StringComparison.OrdinalIgnoreCase));
        }
    }
}
