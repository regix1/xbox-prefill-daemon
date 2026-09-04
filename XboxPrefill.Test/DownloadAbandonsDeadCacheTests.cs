using System.Net;
using Spectre.Console;
using XboxPrefill.Api;
using XboxPrefill.Handlers;
using XboxPrefill.Models;
using XboxPrefill.Models.ApiResponses;
using XboxPrefill.Settings;

namespace XboxPrefill.Test
{
    /// <summary>
    /// Locks the rule that stops the download loop walking a whole slice queue against a cache that is giving
    /// back nothing. The queue, not the per-request timeout, used to be the multiplier: the walk cost
    /// <c>ceil(queueLength / MaxConcurrentRequests) x ResponseHeadersTimeout</c>, so a large game against a dead
    /// cache ran for hours before reporting anything. Once two full waves have failed with zero successes the
    /// rest of the queue is abandoned and the failure names the cache and the CDN behind it.
    /// </summary>
    public sealed class DownloadAbandonsDeadCacheTests
    {
        private const string LancacheAddress = "10.0.0.1";
        private const string CdnHost = "assets1.xboxlive.com";

        /// <summary>
        /// Comfortably larger than the two-wave threshold, so a walk that does NOT abandon is clearly
        /// distinguishable from one that does by the number of requests the cache actually saw.
        /// </summary>
        private const int QueueLength = 600;

        [Fact]
        public async Task DownloadQueuedChunksAsync_CacheReturnsNothing_AbandonsQueueAndNamesTheCache()
        {
            using var messageHandler = new StubCacheHandler(requestsToFail: int.MaxValue);
            using var downloadHandler = new DownloadHandler(AnsiConsole.Console, NullProgress.Instance, messageHandler, LancacheAddress);

            var exception = await Assert.ThrowsAsync<TimeoutException>(
                () => downloadHandler.DownloadQueuedChunksAsync(BuildQueue(QueueLength), BuildManifest()));

            Assert.Contains(LancacheAddress, exception.Message, StringComparison.Ordinal);
            Assert.Contains(CdnHost, exception.Message, StringComparison.Ordinal);
            Assert.Contains("not one byte arrived", exception.Message, StringComparison.Ordinal);

            // This message replaces the older stalled-host one for any game large enough to reach two waves, so it
            // has to carry that message's cache-domain advice or the most actionable sentence in the path is lost.
            Assert.Contains("cache-domain group", exception.Message, StringComparison.Ordinal);
            Assert.Contains("windowsupdates", exception.Message, StringComparison.Ordinal);
            Assert.Contains("xboxlive", exception.Message, StringComparison.Ordinal);

            // The point of the rule: the walk stopped early instead of trying every file in the queue.
            Assert.True(
                messageHandler.RequestCount < QueueLength,
                $"Expected the queue to be abandoned, but all {messageHandler.RequestCount} of {QueueLength} requests were attempted.");
        }

        /// <summary>
        /// The over-fire guard. A queue whose first wave fails and which then recovers is a working download and
        /// must still finish. This test passes both with and without the abandon rule, which is expected: code
        /// that never abandons cannot fail an assertion that it does not abandon. It is here to prove the rule
        /// does not break a download that is merely off to a bad start.
        /// </summary>
        [Fact]
        public async Task DownloadQueuedChunksAsync_FirstWaveFailsThenRecovers_StillCompletes()
        {
            using var messageHandler = new StubCacheHandler(requestsToFail: AppConfig.MaxConcurrentRequests);
            using var downloadHandler = new DownloadHandler(AnsiConsole.Console, NullProgress.Instance, messageHandler, LancacheAddress);

            var succeeded = await downloadHandler.DownloadQueuedChunksAsync(BuildQueue(QueueLength), BuildManifest());

            Assert.True(succeeded);
            Assert.True(
                messageHandler.RequestCount >= QueueLength,
                $"Expected every file to be attempted, but only {messageHandler.RequestCount} of {QueueLength} requests were made.");
        }

        private static PackageManifest BuildManifest() => new PackageManifest
        {
            CdnHost = CdnHost,
            CdnRootUrl = $"http://{CdnHost}",
            Version = "1.0.0.0"
        };

        private static List<QueuedRequest> BuildQueue(int count)
        {
            var queue = new List<QueuedRequest>(count);
            for (var i = 0; i < count; i++)
            {
                queue.Add(new QueuedRequest
                {
                    DownloadUrl = $"/filestreamingservice/files/file-{i}",
                    UpstreamHost = CdnHost,
                    LowerByteRange = 0,
                    UpperByteRange = 1023,
                    DownloadSizeBytes = 1024
                });
            }
            return queue;
        }

        /// <summary>
        /// Stands in for the lancache. Fails the first <c>requestsToFail</c> requests and serves the rest, so one
        /// instance covers both a cache that is entirely down and one that recovers after a bad first wave.
        /// Failures are immediate rather than slow so the tests do not sit through the real header timeout.
        /// </summary>
        private sealed class StubCacheHandler : HttpMessageHandler
        {
            private readonly int _requestsToFail;
            private int _requestCount;

            public StubCacheHandler(int requestsToFail)
            {
                _requestsToFail = requestsToFail;
            }

            public int RequestCount => Volatile.Read(ref _requestCount);

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var attempt = Interlocked.Increment(ref _requestCount);
                if (attempt <= _requestsToFail)
                {
                    throw new HttpRequestException("Connection refused");
                }

                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.PartialContent)
                {
                    Content = new ByteArrayContent(new byte[1024])
                });
            }
        }
    }
}
