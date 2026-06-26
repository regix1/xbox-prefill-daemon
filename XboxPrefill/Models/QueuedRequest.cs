namespace XboxPrefill.Models
{
    /// <summary>
    /// A single 1 MB-aligned slice of a package file to fetch through the lancache. <see cref="DownloadUrl"/>
    /// is the upstream CDN path+query (with a leading <c>/</c>), preserving any path component carried by the
    /// package's <c>CdnRootPaths[0]</c>. <see cref="UpstreamHost"/> is that file's CDN host, sent as the
    /// <c>Host</c> header so the lancache fetches from (and keys on) the correct upstream origin.
    /// Different package files can live on different CDN hosts, so the host travels per request.
    /// <para>
    /// Each file is expanded into one request per lancache slice (1 MB) so the daemon's <c>Range</c> header
    /// matches nginx's <c>$slice_range</c> exactly; <see cref="LowerByteRange"/>/<see cref="UpperByteRange"/>
    /// carry that slice's inclusive byte bounds. A whole-file (or open-ended <c>bytes=0-</c>) request makes the
    /// CDN return a non-slice response and nginx's slice module aborts the transfer at 0 bytes.
    /// </para>
    /// </summary>
    public sealed class QueuedRequest
    {
        /// <summary>The upstream CDN path + query (leading <c>/</c>); the lancache fetch path.</summary>
        public string DownloadUrl { get; set; }

        /// <summary>This file's upstream CDN host, used as the <c>Host</c> header for the lancache fetch.</summary>
        public string UpstreamHost { get; set; }

        /// <summary>Inclusive lower byte offset of this 1 MB-aligned slice; the <c>Range</c> header's lower bound.</summary>
        public long LowerByteRange { get; set; }

        /// <summary>
        /// Inclusive upper byte offset of this slice; the <c>Range</c> header's upper bound. For the last slice
        /// of a file this is <c>fileSize - 1</c> (so a non-1MB-multiple file is fully, exactly covered).
        /// </summary>
        public long UpperByteRange { get; set; }

        /// <summary>The number of bytes this slice transfers (<see cref="UpperByteRange"/> - <see cref="LowerByteRange"/> + 1).</summary>
        public ulong DownloadSizeBytes { get; set; }
    }
}
