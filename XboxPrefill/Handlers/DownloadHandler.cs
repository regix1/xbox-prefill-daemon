using System.Threading;

namespace XboxPrefill.Handlers
{
    public sealed class DownloadHandler : IDisposable
    {
        private const int MaxDownloadRetries = 2;

        /// <summary>
        /// Maximum time to wait for lancache to START responding (i.e. return response headers) to a
        /// content request. This bounds ONLY the time-to-first-response-headers, not the body transfer,
        /// so a slow-but-progressing large file is never cut off. When lancache has no upstream for the
        /// request's Host (e.g. the cache-domain group covering that host is not enabled), nginx never
        /// sends response headers and the request would otherwise hang forever transferring 0 bytes;
        /// this timeout makes that case fail fast instead.
        /// </summary>
        private static readonly TimeSpan ResponseHeadersTimeout = TimeSpan.FromSeconds(100);

        /// <summary>
        /// Maximum time the body read may go WITHOUT receiving any new bytes before the slice is treated as a
        /// stalled (retriable) failure. The window is reset on every successful read, so a slow-but-progressing
        /// large file is never cut off - only a mid-stream stall trips it (e.g. nginx sent 200 headers then the
        /// slice subrequest died and the connection hangs). This is separate from <see cref="ResponseHeadersTimeout"/>,
        /// which bounds only the wait for response headers, not the body transfer.
        /// </summary>
        private static readonly TimeSpan BodyIdleTimeout = TimeSpan.FromSeconds(100);

        private readonly IAnsiConsole _ansiConsole;
        private readonly HttpClient _client;
        private readonly IPrefillProgress _progress;

        /// <summary>
        /// Set when a content request times out waiting for lancache to start responding. This is the
        /// signature of a lancache that cannot serve the request's upstream Host (missing cache-domain
        /// group), so we surface a specific, actionable error instead of a silent stall + retry loop.
        /// </summary>
        private volatile string? _stalledUpstreamHost;

        /// <summary>
        /// The URL/IP Address where the Lancache has been detected.
        /// </summary>
        private string _lancacheAddress;

        public DownloadHandler(IAnsiConsole ansiConsole, IPrefillProgress? progress = null)
        {
            _ansiConsole = ansiConsole;
            _progress = progress ?? NullProgress.Instance;

            _client = new HttpClient();
            _client.DefaultRequestHeaders.Add("User-Agent", AppConfig.DefaultUserAgent);
            // Disable HttpClient's overall timeout so it never cuts off a slow-but-progressing large file.
            // Timeouts are scoped per request to the response-headers wait only (see ResponseHeadersTimeout).
            _client.Timeout = Timeout.InfiniteTimeSpan;
        }

        //TODO document allManifestUrls
        //TODO why does the manifest url need to be passed in?
        /// <summary>
        /// Attempts to download all queued requests.  If all downloads are successful, will return true.
        /// In the case of any failed downloads, the failed downloads will be retried up to 3 times.  If the downloads fail 3 times, then
        /// false will be returned
        /// </summary>
        /// <returns>True if all downloads succeeded.  False if downloads failed 3 times.</returns>
        public async Task<bool> DownloadQueuedChunksAsync(List<QueuedRequest> queuedRequests, PackageManifest manifestUrl, string? appId = null, string? appName = null, CancellationToken cancellationToken = default)
        {
            if (AppConfig.SkipDownloads)
            {
                return true;
            }
            if (_lancacheAddress == null)
            {
                var cdnUrl = manifestUrl.ManifestDownloadUri.Host;
                _lancacheAddress = await LancacheIpResolver.ResolveLancacheIpAsync(_ansiConsole, cdnUrl);
            }

            int retryCount = 0;
            var failedRequests = new ConcurrentBag<QueuedRequest>();
            await _ansiConsole.CreateSpectreProgress(TransferSpeedUnit.Bits).StartAsync(async ctx =>
            {
                //TODO should probably implement cycling through available CDNs when one fails
                // Run the initial download
                failedRequests = await AttemptDownloadAsync(ctx, "Downloading..", queuedRequests, new Uri(manifestUrl.ManifestDownloadUrl), appId: appId, appName: appName, cancellationToken: cancellationToken);

                // Handle any failed requests
                while (failedRequests.Any() && retryCount < MaxDownloadRetries)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    retryCount++;
                    await Task.Delay(2000 * retryCount, cancellationToken);
                    var upstreamCdn = new Uri(manifestUrl.ManifestDownloadUrl);
                    failedRequests = await AttemptDownloadAsync(ctx, $"Retrying  {retryCount}..", failedRequests.ToList(), upstreamCdn, forceRecache: true, appId: appId, appName: appName, cancellationToken: cancellationToken);
                }
            });

            // Handling final failed requests
            if (!failedRequests.Any())
            {
                return true;
            }

            // If requests stalled waiting for lancache to even start responding, the cause is almost
            // always a lancache that has no upstream configured for the content host - i.e. the relevant
            // cache-domain group is not enabled. Surface a specific, actionable error (instead of a bare
            // "download failed") so the manager/UI shows the user exactly what to fix.
            var stalledHost = _stalledUpstreamHost;
            if (!string.IsNullOrEmpty(stalledHost))
            {
                var message =
                    $"Download stalled: lancache returned no data for Host '{stalledHost}' " +
                    $"(no response within {ResponseHeadersTimeout.TotalSeconds:0} seconds). " +
                    "This usually means the cache-domain group for that host is not enabled in lancache. " +
                    "Xbox game content requires the 'windowsupdates' (and 'xboxlive') cache-domain groups - " +
                    "enable them in your lancache (uklans/cache-domains) and reload.";
                _ansiConsole.LogMarkupError(message);
                _ansiConsole.WriteLine();
                _progress.OnError(message);
                return false;
            }

            _ansiConsole.LogMarkupError($"Download failed with {LightYellow(failedRequests.Count)} failed requests");
            _ansiConsole.WriteLine();
            return false;
        }

        /// <summary>
        /// Attempts to download the specified requests.  Returns a list of any requests that have failed for any reason.
        /// </summary>
        /// <param name="forceRecache">When specified, will cause the cache to delete the existing cached data for a request, and redownload it again.</param>
        /// <returns>A list of failed requests</returns>
        private async Task<ConcurrentBag<QueuedRequest>> AttemptDownloadAsync(ProgressContext ctx, string taskTitle, List<QueuedRequest> requestsToDownload,
                                                                                Uri upstreamCdn, bool forceRecache = false, string? appId = null, string? appName = null, CancellationToken cancellationToken = default)
        {
            double requestTotalSize = requestsToDownload.Sum(e => (long)e.DownloadSizeBytes);
            var progressTask = ctx.AddTask(taskTitle, new ProgressTaskSettings { MaxValue = requestTotalSize });

            var failedRequests = new ConcurrentBag<QueuedRequest>();
            long bytesDownloaded = 0;
            var startTime = DateTime.UtcNow;
            long lastProgressReportTicks = 0;
            var progressThrottle = TimeSpan.FromMilliseconds(250);

            var progressAppId = appId ?? upstreamCdn.Host;
            var progressAppName = appName ?? upstreamCdn.Host;

            // Opt-in [MAP] diagnostics state (only built when XBOX_DEBUG_MAPPING is on, so the hot path is untouched
            // otherwise). Pre-group the slice list by distinct file path so each file's request/response is logged
            // ONCE (on its first slice) and a single completion line can report bytes-received vs file size across all
            // slices, instead of emitting tens of thousands of per-slice lines.
            var debugMapping = MappingDebugLogger.Enabled;
            Dictionary<string, long>? debugExpectedBytesByFile = null;
            Dictionary<string, int>? debugSliceCountByFile = null;
            ConcurrentDictionary<string, FileDebugProgress>? debugProgressByFile = null;
            if (debugMapping)
            {
                debugExpectedBytesByFile = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
                debugSliceCountByFile = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                foreach (var request in requestsToDownload)
                {
                    var filePath = MappingDebugLogger.ToFragment(request.DownloadUrl);
                    debugExpectedBytesByFile[filePath] = debugExpectedBytesByFile.GetValueOrDefault(filePath) + (long)request.DownloadSizeBytes;
                    debugSliceCountByFile[filePath] = debugSliceCountByFile.GetValueOrDefault(filePath) + 1;
                }
                debugProgressByFile = new ConcurrentDictionary<string, FileDebugProgress>(StringComparer.OrdinalIgnoreCase);
            }

            await Parallel.ForEachAsync(requestsToDownload, new ParallelOptions { MaxDegreeOfParallelism = AppConfig.MaxConcurrentRequests, CancellationToken = cancellationToken }, async (chunk, ct) =>
            {
                var upstreamHost = string.IsNullOrEmpty(chunk.UpstreamHost) ? upstreamCdn.Host : chunk.UpstreamHost;

                // Bound ONLY the wait for response headers (the time for lancache to start responding).
                using var headersTimeoutCts = new CancellationTokenSource(ResponseHeadersTimeout);
                using var headersCts = CancellationTokenSource.CreateLinkedTokenSource(ct, headersTimeoutCts.Token);

                // Idle body-read timeout: armed only once the body is being read (CancelAfter below) and reset on
                // each successful read, so a slow-but-progressing large file is never cut off - only a mid-stream
                // stall trips it. Declared out here so the catch filters can tell an idle stall apart from a
                // caller cancel or a headers timeout. headersReceived disambiguates the two timers, since the
                // 100 s headers timer keeps running and could fire spuriously during a long body read.
                using var bodyIdleCts = new CancellationTokenSource();
                using var bodyCts = CancellationTokenSource.CreateLinkedTokenSource(ct, bodyIdleCts.Token);
                bool headersReceived = false;

                try
                {
                    // Resolve this file's [MAP] accounting handle once (gated; nothing is allocated when disabled).
                    FileDebugProgress? fileProgress = null;
                    string? requestPath = null;
                    if (debugMapping)
                    {
                        requestPath = MappingDebugLogger.ToFragment(chunk.DownloadUrl);
                        fileProgress = debugProgressByFile!.GetOrAdd(requestPath, static _ => new FileDebugProgress());
                    }

                    using var requestMessage = BuildContentRequest(_lancacheAddress, chunk, upstreamHost, forceRecache);

                    using var response = await _client.SendAsync(requestMessage, HttpCompletionOption.ResponseHeadersRead, headersCts.Token);
                    headersReceived = true; // past the headers wait; from here only the idle body timeout applies

                    // [MAP] request/response: log once per distinct file (the first slice to arrive), capturing the
                    // requested GUID + the CDN status BEFORE EnsureSuccessStatusCode so a 200/403/404 is still recorded.
                    if (debugMapping && fileProgress!.TryMarkRequestLogged())
                    {
                        MappingDebugLogger.LogFileRequest(_progress, requestPath!, upstreamHost,
                            $"bytes={chunk.LowerByteRange}-{chunk.UpperByteRange}",
                            (int)response.StatusCode,
                            response.Content.Headers.ContentRange?.ToString());
                    }

                    response.EnsureSuccessStatusCode();
                    using Stream responseStream = await response.Content.ReadAsStreamAsync(ct);

                    // Don't save the data anywhere, so we don't have to waste time writing it to disk.
                    var buffer = new byte[4096];
                    long sliceBytesRead = 0;
                    bodyIdleCts.CancelAfter(BodyIdleTimeout); // arm the idle window before the first read
                    int bytesRead;
                    while ((bytesRead = await responseStream.ReadAsync(buffer, bodyCts.Token)) != 0)
                    {
                        bodyIdleCts.CancelAfter(BodyIdleTimeout); // reset the idle window on each successful read
                        if (debugMapping)
                        {
                            sliceBytesRead += bytesRead;
                        }
                    }

                    // [MAP] completion: when this file's LAST slice finishes, log bytes received vs file size so a human
                    // can confirm bytes actually flowed for the requested GUID (not a 0-byte stall).
                    if (debugMapping && fileProgress!.TryCompleteSlice(sliceBytesRead, debugSliceCountByFile![requestPath!], out var fileBytesReceived, out var slicesCompleted))
                    {
                        MappingDebugLogger.LogFileCompleted(_progress, requestPath!, fileBytesReceived, debugExpectedBytesByFile![requestPath!], slicesCompleted);
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    // Propagate caller cancellation - let Parallel.ForEachAsync stop
                    throw;
                }
                catch (OperationCanceledException) when (!headersReceived && headersTimeoutCts.IsCancellationRequested)
                {
                    // lancache never started responding for this Host - the cache-domain group covering it
                    // is almost certainly not enabled. Record the host so the final failure can name it.
                    _stalledUpstreamHost = upstreamHost;
                    failedRequests.Add(chunk);
                    FileLogger.LogExceptionNoStackTrace($"Request {chunk.DownloadUrl}",
                        new TimeoutException($"lancache returned no response for Host '{upstreamHost}' within {ResponseHeadersTimeout.TotalSeconds:0}s"));
                }
                catch (OperationCanceledException) when (bodyIdleCts.IsCancellationRequested)
                {
                    // Headers arrived but the body stalled mid-stream (no new bytes within the idle window) - the
                    // signature of a slice subrequest that died upstream. Record the slice as a retriable failure
                    // so the existing retry loop re-fetches it instead of hanging forever.
                    failedRequests.Add(chunk);
                    FileLogger.LogExceptionNoStackTrace($"Request {chunk.DownloadUrl}",
                        new TimeoutException($"lancache stalled mid-transfer for Host '{upstreamHost}' (no data within {BodyIdleTimeout.TotalSeconds:0}s)"));
                }
                catch (Exception e)
                {
                    failedRequests.Add(chunk);
                    FileLogger.LogExceptionNoStackTrace($"Request {chunk.DownloadUrl}", e);
                }
                progressTask.Increment(chunk.DownloadSizeBytes);

                // Report progress via IPrefillProgress (throttled)
                var downloaded = Interlocked.Add(ref bytesDownloaded, (long)chunk.DownloadSizeBytes);
                var now = DateTime.UtcNow;
                var nowTicks = now.Ticks;
                long prevTicks = Volatile.Read(ref lastProgressReportTicks);
                if (prevTicks == 0 || (nowTicks - prevTicks) >= progressThrottle.Ticks)
                {
                    if (Interlocked.CompareExchange(ref lastProgressReportTicks, nowTicks, prevTicks) == prevTicks)
                    {
                        var elapsed = now - startTime;
                        var bytesPerSecond = elapsed.TotalSeconds > 0 ? downloaded / elapsed.TotalSeconds : 0;

                        _progress.OnDownloadProgress(new DownloadProgressInfo
                        {
                            AppId = progressAppId,
                            AppName = progressAppName,
                            TotalBytes = (long)requestTotalSize,
                            BytesDownloaded = downloaded,
                            BytesPerSecond = bytesPerSecond,
                            Elapsed = elapsed
                        });
                    }
                }
            });

            // Making sure the progress bar is always set to its max value, in-case some unexpected error leaves the progress bar showing as unfinished
            progressTask.Increment(progressTask.MaxValue);

            // Send a final progress report to ensure the client sees 100% completion
            var finalElapsed = DateTime.UtcNow - startTime;
            var finalBytesPerSecond = finalElapsed.TotalSeconds > 0 ? bytesDownloaded / finalElapsed.TotalSeconds : 0;
            _progress.OnDownloadProgress(new DownloadProgressInfo
            {
                AppId = progressAppId,
                AppName = progressAppName,
                TotalBytes = (long)requestTotalSize,
                BytesDownloaded = (long)requestTotalSize,
                BytesPerSecond = finalBytesPerSecond,
                Elapsed = finalElapsed
            });

            return failedRequests;
        }

        /// <summary>
        /// Builds the lancache content request for a single 1 MB-aligned slice: <c>GET http://{lancache}{path}</c>
        /// with the upstream <c>Host</c> header and an exact-boundary <c>Range</c> header
        /// (<c>bytes={Lower}-{Upper}</c>). The Range is the crux of the fix - it makes nginx's
        /// <c>@upstream_redirect</c> forward a matching <c>$http_range</c> to delivery.mp so the slice module
        /// gets the 206 it demands. A request with no Range (or an open-ended <c>bytes=0-</c>) makes the CDN
        /// return 200 / a whole-file 206 and the slice subrequest aborts at 0 bytes.
        /// </summary>
        /// <remarks>
        /// <paramref name="forceRecache"/> is intentionally not used to append a query parameter.
        /// lancache's slice cache key is <c>md5($cacheidentifier$uri$slice_range)</c> where nginx
        /// <c>$uri</c> is the request path WITHOUT the query string, so any query-based cache-bust
        /// is a complete no-op against the slice key. Xbox content <c>DownloadUrl</c> values are
        /// already signed URIs (carrying P1/P2/P3/P4 params); appending a second <c>?</c> corrupts
        /// the signature and causes 403s on every retry. Retries simply re-request the identical
        /// URL; lancache will re-fetch from upstream for any stale or missing slice automatically.
        /// </remarks>
        internal static HttpRequestMessage BuildContentRequest(string lancacheAddress, QueuedRequest chunk, string upstreamHost, bool forceRecache)
        {
            var url = Path.Join($"http://{lancacheAddress}", chunk.DownloadUrl);

            var requestMessage = new HttpRequestMessage(HttpMethod.Get, url);
            // The Host header carries THIS file's upstream CDN host (files can live on different hosts).
            // Fall back to the manifest-wide host only if a request didn't capture its own.
            requestMessage.Headers.Host = upstreamHost;
            // Exact 1 MB-aligned slice range so nginx's $slice_range is matched and delivery.mp returns 206.
            requestMessage.Headers.Range = new RangeHeaderValue(chunk.LowerByteRange, chunk.UpperByteRange);
            return requestMessage;
        }

        /// <summary>
        /// Thread-safe per-file accounting for the opt-in <c>[MAP]</c> diagnostics. A package file is fetched as many
        /// parallel 1 MB slices, so this collapses those slices back to one request-log decision and one completion
        /// line: <see cref="TryMarkRequestLogged"/> returns true for exactly the first slice to reach the request log,
        /// and <see cref="TryCompleteSlice"/> returns true only for the slice that completes the file (the running
        /// byte total is then fully settled because every slice adds its bytes before incrementing the count).
        /// </summary>
        private sealed class FileDebugProgress
        {
            private long _bytesReceived;
            private int _slicesCompleted;
            private int _requestLogged;

            /// <summary>Returns true exactly once, for the first slice of the file to reach the request log.</summary>
            public bool TryMarkRequestLogged() => Interlocked.Exchange(ref _requestLogged, 1) == 0;

            /// <summary>
            /// Records a completed slice's byte count. Returns true only when this is the file's final slice
            /// (<paramref name="slicesCompleted"/> == <paramref name="expectedSlices"/>), with the fully-settled
            /// <paramref name="totalBytes"/> received across all slices.
            /// </summary>
            public bool TryCompleteSlice(long sliceBytes, int expectedSlices, out long totalBytes, out int slicesCompleted)
            {
                Interlocked.Add(ref _bytesReceived, sliceBytes);
                slicesCompleted = Interlocked.Increment(ref _slicesCompleted);
                if (slicesCompleted == expectedSlices)
                {
                    totalBytes = Interlocked.Read(ref _bytesReceived);
                    return true;
                }
                totalBytes = 0;
                return false;
            }
        }

        public void Dispose()
        {
            _client?.Dispose();
        }
    }
}
