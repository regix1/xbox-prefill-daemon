using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using XboxPrefill.Handlers;
using XboxPrefill.Models;

namespace XboxPrefill.Test
{
    /// <summary>
    /// Locks the per-1 MB-aligned slice ranging that fixes the Xbox 0-byte stall. The lancache nginx slice
    /// module (<c>slice 1m;</c>) aborts any subrequest whose upstream response is not a 206 matching the exact
    /// slice boundary, so the daemon MUST request each file as a sequence of <c>bytes=N-(N+1048575)</c> slices
    /// (last slice ending at <c>fileSize-1</c>). These tests assert the produced ranges - including a
    /// non-1MB-multiple size that exercises a partial last slice - and that every request carries a Range header.
    /// </summary>
    public sealed class DownloadRequestTests
    {
        private const long SliceSize = 1048576; // 1 MB - must equal ManifestHandler.SliceSizeBytes
        private const string SampleUrl = "/filestreamingservice/files/abc-def";

        /// <summary>
        /// A realistic Xbox signed URL: PathAndQuery already contains P1/P2/P3/P4 CDN signature params.
        /// Used to verify that forceRecache retries do not corrupt the signature by appending a second '?'.
        /// </summary>
        private const string SampleSignedUrl = "/filestreamingservice/files/abc-def?P1=1719273600&P2=900&P3=2&P4=AbCdEfGhIjKlMnOpQrStUvWxYz0123456789ABCDEFGHIJKLMNOPQRSTU";

        private const string SampleHost = "assets1.xboxlive.com";
        private const string LancacheAddress = "10.0.0.1";

        [Fact]
        public void BuildChunkRequests_NonMultipleSize_ProducesAlignedChunksWithPartialLast()
        {
            // 2.5 MB - the by-hand trace from the plan: 0-1048575, 1048576-2097151, 2097152-2621439.
            const ulong fileSize = 2621440UL; // 2.5 * 1048576

            var chunks = ManifestHandler.BuildChunkRequests(SampleUrl, SampleHost, fileSize).ToList();

            Assert.Equal(3, chunks.Count);

            Assert.Equal(0L, chunks[0].LowerByteRange);
            Assert.Equal(1048575L, chunks[0].UpperByteRange);
            Assert.Equal(1048576UL, chunks[0].DownloadSizeBytes);

            Assert.Equal(1048576L, chunks[1].LowerByteRange);
            Assert.Equal(2097151L, chunks[1].UpperByteRange);
            Assert.Equal(1048576UL, chunks[1].DownloadSizeBytes);

            // Partial last slice: 2097152 .. 2621439 (524288 bytes), ending exactly at fileSize-1.
            Assert.Equal(2097152L, chunks[2].LowerByteRange);
            Assert.Equal(2621439L, chunks[2].UpperByteRange);
            Assert.Equal(524288UL, chunks[2].DownloadSizeBytes);

            AssertFullyCovers(chunks, fileSize);
        }

        [Fact]
        public void BuildChunkRequests_ExactMultipleSize_ProducesFullChunks()
        {
            const ulong fileSize = 2097152UL; // exactly 2 MB

            var chunks = ManifestHandler.BuildChunkRequests(SampleUrl, SampleHost, fileSize).ToList();

            Assert.Equal(2, chunks.Count);
            Assert.Equal(0L, chunks[0].LowerByteRange);
            Assert.Equal(1048575L, chunks[0].UpperByteRange);
            Assert.Equal(1048576L, chunks[1].LowerByteRange);
            Assert.Equal(2097151L, chunks[1].UpperByteRange); // last slice ends at fileSize-1, no overrun
            AssertFullyCovers(chunks, fileSize);
        }

        [Fact]
        public void BuildChunkRequests_SubSliceSize_ProducesSingleChunk()
        {
            const ulong fileSize = 500000UL; // smaller than one slice

            var chunks = ManifestHandler.BuildChunkRequests(SampleUrl, SampleHost, fileSize).ToList();

            Assert.Single(chunks);
            Assert.Equal(0L, chunks[0].LowerByteRange);
            Assert.Equal(499999L, chunks[0].UpperByteRange);
            Assert.Equal(500000UL, chunks[0].DownloadSizeBytes);
            AssertFullyCovers(chunks, fileSize);
        }

        [Fact]
        public void BuildChunkRequests_ZeroSize_ProducesNoChunks()
        {
            var chunks = ManifestHandler.BuildChunkRequests(SampleUrl, SampleHost, 0UL).ToList();
            Assert.Empty(chunks);
        }

        [Fact]
        public void BuildContentRequest_SetsRangeHeaderPerChunk()
        {
            const ulong fileSize = 2621440UL; // 2.5 MB -> 3 slices
            var chunks = ManifestHandler.BuildChunkRequests(SampleUrl, SampleHost, fileSize).ToList();

            foreach (var chunk in chunks)
            {
                using var request = DownloadHandler.BuildContentRequest(LancacheAddress, chunk, chunk.UpstreamHost, forceRecache: false);

                Assert.NotNull(request.Headers.Range); // NO request may go out without a Range
                Assert.Equal($"bytes={chunk.LowerByteRange}-{chunk.UpperByteRange}", request.Headers.Range!.ToString());
                Assert.Equal(SampleHost, request.Headers.Host);
                Assert.Equal(HttpMethod.Get, request.Method);
                Assert.DoesNotContain("nocache", request.RequestUri!.ToString());
            }
        }

        /// <summary>
        /// forceRecache must NOT append ?nocache=1 (or any query parameter): lancache's slice cache key
        /// is md5($cacheidentifier$uri$slice_range) where $uri is the path without the query string, so
        /// a query-based cache-bust is a no-op and only adds noise. Range header must still be set.
        /// </summary>
        [Fact]
        public void BuildContentRequest_ForceRecache_DoesNotAppendNocache()
        {
            var chunk = ManifestHandler.BuildChunkRequests(SampleUrl, SampleHost, 1048576UL).Single();

            using var request = DownloadHandler.BuildContentRequest(LancacheAddress, chunk, chunk.UpstreamHost, forceRecache: true);

            Assert.DoesNotContain("nocache=1", request.RequestUri!.ToString());
            Assert.NotNull(request.Headers.Range);
            Assert.Equal("bytes=0-1048575", request.Headers.Range!.ToString());
        }

        /// <summary>
        /// When DownloadUrl is a signed Xbox CDN URL (already contains ?P1=..&amp;P4=signature), a
        /// forceRecache retry must NOT append a second '?' — doing so glues "nocache=1" onto the P4
        /// signature value and produces a double-'?' URL that yields CDN 403s on every retry.
        /// The resulting URI must have exactly one '?', and the original signature must be intact.
        /// </summary>
        [Fact]
        public void BuildContentRequest_ForceRecache_SignedUrl_NoQueryCorruption()
        {
            var chunk = ManifestHandler.BuildChunkRequests(SampleSignedUrl, SampleHost, 1048576UL).Single();

            using var request = DownloadHandler.BuildContentRequest(LancacheAddress, chunk, chunk.UpstreamHost, forceRecache: true);

            var uri = request.RequestUri!.ToString();
            Assert.Equal(1, uri.Count(character => character == '?'));
            Assert.Contains("P4=AbCdEfGhIjKlMnOpQrStUvWxYz0123456789ABCDEFGHIJKLMNOPQRSTU", uri);
            Assert.DoesNotContain("nocache=1", uri);
            Assert.NotNull(request.Headers.Range);
            Assert.Equal("bytes=0-1048575", request.Headers.Range!.ToString());
        }

        /// <summary>
        /// Asserts the slices are contiguous, non-overlapping, start at 0, end exactly at fileSize-1, and their
        /// byte counts sum to fileSize (so progress accounting still totals correctly).
        /// </summary>
        private static void AssertFullyCovers(IReadOnlyList<QueuedRequest> chunks, ulong fileSize)
        {
            Assert.NotEmpty(chunks);
            Assert.Equal(0L, chunks[0].LowerByteRange);
            Assert.Equal((long)fileSize - 1, chunks[^1].UpperByteRange);

            ulong sum = 0;
            long expectedNextStart = 0;
            foreach (var chunk in chunks)
            {
                Assert.Equal(expectedNextStart, chunk.LowerByteRange);            // contiguous, no gap/overlap
                Assert.True(chunk.UpperByteRange >= chunk.LowerByteRange);        // non-empty slice
                Assert.True(chunk.UpperByteRange - chunk.LowerByteRange + 1 <= SliceSize); // never larger than 1 MB
                Assert.Equal((ulong)(chunk.UpperByteRange - chunk.LowerByteRange + 1), chunk.DownloadSizeBytes);
                sum += chunk.DownloadSizeBytes;
                expectedNextStart = chunk.UpperByteRange + 1;
            }

            Assert.Equal(fileSize, sum);
        }
    }
}
