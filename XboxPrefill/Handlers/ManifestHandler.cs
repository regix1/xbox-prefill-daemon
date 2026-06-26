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

        /// <summary>
        /// The lancache nginx slice size (<c>slice 1m;</c>). Every content request MUST carry a <c>Range</c>
        /// aligned to this boundary so nginx's <c>@upstream_redirect</c> forwards a matching <c>$http_range</c>
        /// to delivery.mp and the slice module receives the 206 it demands per slice. A whole-file GET (or an
        /// open-ended <c>bytes=0-</c>) yields a 200 / whole-file 206 and the slice subrequest aborts at 0 bytes.
        /// </summary>
        internal const long SliceSizeBytes = 1048576; // 1 MB

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
            int fileCount = 0;

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
                    // Expand the file into 1 MB-aligned slice requests so each Range header matches nginx's
                    // $slice_range exactly; a whole-file (or open-ended bytes=0-) request returns a non-slice
                    // response that the slice module aborts at 0 bytes. One QueuedRequest per slice, mirroring
                    // the per-range request model used by the Riot/Battle.net prefill daemons.
                    queue.AddRange(BuildChunkRequests(upstreamUri.PathAndQuery, upstreamUri.Host, file.FileSize));
                    fileCount++;
                }
            }

            if (queue.Count == 0)
            {
                throw new ManifestException($"Package for product {app.AppId} ({app.Title}) contained no downloadable files.");
            }

            // Collect the stable per-file path fragments (path only, query string stripped) for the
            // CDN-info API. The lancache manager uses these to map cached requests back to this product.
            manifest.FilePathFragments = CollectFilePathFragments(queue);

            manifest.QueuedRequests = queue;
            _ansiConsole.LogMarkupVerbose($"Resolved {LightYellow(fileCount)} package files ({LightYellow(queue.Count)} slices) for {Magenta(app.Title)}");
            return manifest;
        }

        /// <summary>
        /// Expands a single upstream file into 1 MB-aligned slice requests. The slices are contiguous,
        /// non-overlapping, start at 0, and the LAST slice ends exactly at <c>fileSize - 1</c> (so a file whose
        /// size is not a 1 MB multiple is fully and exactly covered). Each slice carries its inclusive
        /// <see cref="QueuedRequest.LowerByteRange"/>/<see cref="QueuedRequest.UpperByteRange"/> and that slice's
        /// byte count. A zero-byte file yields no slices (there is nothing to warm). Mirrors the per-range request
        /// model used by the Riot/Battle.net daemons so the daemon's <c>Range</c> header matches nginx's slice.
        /// </summary>
        internal static IEnumerable<QueuedRequest> BuildChunkRequests(string downloadUrl, string upstreamHost, ulong fileSize)
        {
            var total = (long)fileSize;
            for (long start = 0; start < total; start += SliceSizeBytes)
            {
                long end = Math.Min(start + SliceSizeBytes, total) - 1;
                yield return new QueuedRequest
                {
                    DownloadUrl = downloadUrl,
                    UpstreamHost = upstreamHost,
                    LowerByteRange = start,
                    UpperByteRange = end,
                    DownloadSizeBytes = (ulong)(end - start + 1)
                };
            }
        }

        /// <summary>
        /// Reduces the slice queue to the stable per-file path fragments exposed to the CDN-info API: the query
        /// string is stripped from each slice's <see cref="QueuedRequest.DownloadUrl"/> and the result is
        /// de-duplicated case-insensitively. Chunk expansion emits one <see cref="QueuedRequest"/> per 1 MB slice,
        /// and every slice of a given file carries the SAME path+query, so this yields exactly ONE
        /// <c>/filestreamingservice/files/&lt;GUID&gt;</c> fragment per distinct file - never one per slice. Without
        /// this de-duplication a multi-GB file would emit tens of thousands of identical fragments and bloat the
        /// <c>get-cdn-info</c> response.
        /// </summary>
        internal static List<string> CollectFilePathFragments(IEnumerable<QueuedRequest> queue)
        {
            return queue
                .Select(static r => r.DownloadUrl.Contains('?') ? r.DownloadUrl[..r.DownloadUrl.IndexOf('?')] : r.DownloadUrl)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
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
