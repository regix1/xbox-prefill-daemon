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

            await Parallel.ForEachAsync(requestsToDownload, new ParallelOptions { MaxDegreeOfParallelism = AppConfig.MaxConcurrentRequests, CancellationToken = cancellationToken }, async (chunk, ct) =>
            {
                var upstreamHost = string.IsNullOrEmpty(chunk.UpstreamHost) ? upstreamCdn.Host : chunk.UpstreamHost;

                // Bound ONLY the wait for response headers (the time for lancache to start responding).
                // The body transfer below uses the caller's token, so a slow-but-progressing large file
                // is never cut off - only a request that never starts responding (the 0-byte stall) trips.
                using var headersTimeoutCts = new CancellationTokenSource(ResponseHeadersTimeout);
                using var headersCts = CancellationTokenSource.CreateLinkedTokenSource(ct, headersTimeoutCts.Token);

                try
                {
                    var url = Path.Join($"http://{_lancacheAddress}", chunk.DownloadUrl);
                    if (forceRecache)
                    {
                        url += "?nocache=1";
                    }

                    using var requestMessage = new HttpRequestMessage(HttpMethod.Get, url);
                    // The Host header carries THIS file's upstream CDN host (files can live on different hosts).
                    // Fall back to the manifest-wide host only if a request didn't capture its own.
                    requestMessage.Headers.Host = upstreamHost;

                    using var response = await _client.SendAsync(requestMessage, HttpCompletionOption.ResponseHeadersRead, headersCts.Token);
                    response.EnsureSuccessStatusCode();
                    using Stream responseStream = await response.Content.ReadAsStreamAsync(ct);

                    // Don't save the data anywhere, so we don't have to waste time writing it to disk.
                    var buffer = new byte[4096];
                    while (await responseStream.ReadAsync(buffer, ct) != 0)
                    {
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    // Propagate caller cancellation - let Parallel.ForEachAsync stop
                    throw;
                }
                catch (OperationCanceledException) when (headersTimeoutCts.IsCancellationRequested)
                {
                    // lancache never started responding for this Host - the cache-domain group covering it
                    // is almost certainly not enabled. Record the host so the final failure can name it.
                    _stalledUpstreamHost = upstreamHost;
                    failedRequests.Add(chunk);
                    FileLogger.LogExceptionNoStackTrace($"Request {chunk.DownloadUrl}",
                        new TimeoutException($"lancache returned no response for Host '{upstreamHost}' within {ResponseHeadersTimeout.TotalSeconds:0}s"));
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

        public void Dispose()
        {
            _client?.Dispose();
        }
    }
}
