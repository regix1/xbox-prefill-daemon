namespace XboxPrefill.Diagnostics
{
    /// <summary>
    /// Opt-in <c>[MAP]</c> name-mapping diagnostics. Every line answers one decisive question from
    /// <c>docker logs</c>: does the <c>/filestreamingservice/files/&lt;GUID&gt;</c> fragment that
    /// <c>get-cdn-info</c> EMITS for naming match the GUID the daemon actually REQUESTS through lancache
    /// (and that nginx therefore logs)? If they match, naming works once bytes flow; if not, that is the
    /// real naming defect.
    /// <para>
    /// All output is gated on <see cref="AppConfig.DebugMapping"/> (the <c>XBOX_DEBUG_MAPPING</c> env var,
    /// off by default) and prefixed with <c>[MAP]</c>, so it can be isolated with
    /// <c>docker logs &lt;container&gt; 2&gt;&amp;1 | grep '[MAP]'</c>. Lines are routed through
    /// <see cref="IPrefillProgress.OnLog"/>, which the daemon writes to stdout, so they reach
    /// <c>docker logs</c>. This type only LOGS; it never changes download behavior.
    /// </para>
    /// </summary>
    internal static class MappingDebugLogger
    {
        /// <summary>Greppable prefix on every mapping-diagnostic line.</summary>
        private const string Prefix = "[MAP]";

        /// <summary>Whether opt-in mapping diagnostics are enabled (the <c>XBOX_DEBUG_MAPPING</c> env var).</summary>
        public static bool Enabled => AppConfig.DebugMapping;

        /// <summary>
        /// Logs the <c>get-cdn-info</c> fragment emit for a single app: name, ProductId (appId), resolved CDN host,
        /// the COUNT of emitted <c>/filestreamingservice/files/&lt;GUID&gt;</c> fragments, and each fragment string.
        /// An app that emits ZERO fragments is logged loudly as a warning: it can never be name-mapped because no
        /// patterns reach the manager (the "no fragments emitted" root cause).
        /// </summary>
        public static void LogCdnInfoEmit(IPrefillProgress progress, string appName, string appId, string cdnHost, IReadOnlyList<string> fragments)
        {
            if (!Enabled)
            {
                return;
            }

            if (fragments.Count == 0)
            {
                progress.OnLog(LogLevel.Warning,
                    $"{Prefix} get-cdn-info EMITTED 0 fragments for app='{appName}' appId={appId} cdnHost={cdnHost} -- this app can NEVER be name-mapped (no /filestreamingservice/files/<GUID> patterns reach the manager)");
                return;
            }

            progress.OnLog(LogLevel.Info,
                $"{Prefix} get-cdn-info app='{appName}' appId={appId} cdnHost={cdnHost} fragments={fragments.Count}");
            for (int i = 0; i < fragments.Count; i++)
            {
                progress.OnLog(LogLevel.Info, $"{Prefix}   emit[{i}] {fragments[i]}");
            }
        }

        /// <summary>
        /// Logs the FIRST slice of a distinct file actually requested through lancache (so a multi-GB file logs once,
        /// not once per 1 MB slice): the requested <c>/filestreamingservice/files/&lt;GUID&gt;</c> path (query stripped,
        /// formatted identically to the <see cref="LogCdnInfoEmit"/> emit lines so the two can be eyeballed side by
        /// side), the <c>Host</c> header, this slice's <c>Range</c>, and the CDN RESPONSE status + <c>Content-Range</c>.
        /// A <c>206</c> with a Content-Range means the slice fix is working; a <c>200</c> (no Content-Range) means the
        /// slice subrequest will still abort at 0 bytes.
        /// </summary>
        public static void LogFileRequest(IPrefillProgress progress, string requestPath, string host, string range, int statusCode, string? contentRange)
        {
            if (!Enabled)
            {
                return;
            }

            progress.OnLog(LogLevel.Info,
                $"{Prefix} request {requestPath} host={host} range={range} -> {statusCode} content-range={contentRange ?? "(none)"}");
        }

        /// <summary>
        /// Logs a distinct file's transfer completion: bytes received versus expected file size across N slices.
        /// </summary>
        public static void LogFileCompleted(IPrefillProgress progress, string requestPath, long bytesReceived, long expectedBytes, int sliceCount)
        {
            if (!Enabled)
            {
                return;
            }

            progress.OnLog(LogLevel.Info,
                $"{Prefix} completed {requestPath} received={bytesReceived}/{expectedBytes} bytes across {sliceCount} slice(s)");
        }

        /// <summary>
        /// Reduces a request path+query to the stable <c>/filestreamingservice/files/&lt;GUID&gt;</c> portion (query
        /// string stripped) so a requested path can be compared byte-for-byte against an emitted fragment. Mirrors the
        /// strip in <see cref="XboxPrefill.Handlers.ManifestHandler.CollectFilePathFragments"/> so both sides match.
        /// </summary>
        public static string ToFragment(string downloadUrl)
            => downloadUrl.Contains('?') ? downloadUrl[..downloadUrl.IndexOf('?')] : downloadUrl;
    }
}
