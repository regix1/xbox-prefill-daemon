namespace XboxPrefill.Models
{
    /// <summary>
    /// A single file to fetch through the lancache. <see cref="DownloadUrl"/> is the upstream CDN
    /// path+query (with a leading <c>/</c>), preserving any path component carried by the package's
    /// <c>CdnRootPaths[0]</c>. <see cref="UpstreamHost"/> is that file's CDN host, sent as the
    /// <c>Host</c> header so the lancache fetches from (and keys on) the correct upstream origin.
    /// Different package files can live on different CDN hosts, so the host travels per request.
    /// </summary>
    public sealed class QueuedRequest
    {
        /// <summary>The upstream CDN path + query (leading <c>/</c>); the lancache fetch path.</summary>
        public string DownloadUrl { get; set; }

        /// <summary>This file's upstream CDN host, used as the <c>Host</c> header for the lancache fetch.</summary>
        public string UpstreamHost { get; set; }

        public ulong DownloadSizeBytes { get; set; }
    }
}
