using System.Collections.Generic;
using System.Linq;
using XboxPrefill.Handlers;
using XboxPrefill.Models;

namespace XboxPrefill.Test
{
    /// <summary>
    /// Locks the per-FILE de-duplication of the CDN-info path fragments. The byte-flow fix expands every package
    /// file into one <see cref="QueuedRequest"/> per 1 MB slice (all sharing the same path+query), so the fragment
    /// projection MUST collapse those slices back to exactly ONE <c>/filestreamingservice/files/&lt;GUID&gt;</c>
    /// fragment per distinct file. Without that collapse a multi-GB file would emit tens of thousands of identical
    /// fragments, bloating the <c>get-cdn-info</c> socket response and risking the manager-side timeout (which would
    /// persist 0 patterns and leave every Xbox download named generic Windows Update). These tests guard against a
    /// chunk-expansion regression re-introducing per-slice fragments.
    /// </summary>
    public sealed class FilePathFragmentTests
    {
        private const string SampleHost = "assets1.xboxlive.com";

        [Fact]
        public void CollectFilePathFragments_ChunkExpandedFiles_YieldsOneFragmentPerFile()
        {
            const string pathA = "/filestreamingservice/files/11111111-2222-3333-4444-555555555555";
            const string pathB = "/filestreamingservice/files/aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee";

            // File A: 5 MB -> 5 slices; File B: 3 MB -> 3 slices. Every slice of a file carries the same DownloadUrl.
            var queue = new List<QueuedRequest>();
            queue.AddRange(ManifestHandler.BuildChunkRequests(pathA, SampleHost, 5UL * 1048576UL));
            queue.AddRange(ManifestHandler.BuildChunkRequests(pathB, SampleHost, 3UL * 1048576UL));

            Assert.Equal(8, queue.Count); // sanity: the files really did expand into many slices

            var fragments = ManifestHandler.CollectFilePathFragments(queue);

            Assert.Equal(2, fragments.Count); // ONE fragment per file, not one per slice
            Assert.Contains(pathA, fragments);
            Assert.Contains(pathB, fragments);
        }

        [Fact]
        public void CollectFilePathFragments_StripsQueryStringFromSignedUrl()
        {
            const string path = "/filestreamingservice/files/11111111-2222-3333-4444-555555555555";
            const string signedUrl = path + "?P1=1719273600&P2=900&P3=2&P4=AbCdEfGhIjKlMnOpQrStUvWxYz0123456789";

            // A large file's slices all share the same signed URL (with the query string).
            var queue = ManifestHandler.BuildChunkRequests(signedUrl, SampleHost, 4UL * 1048576UL).ToList();

            Assert.Equal(4, queue.Count);

            var fragments = ManifestHandler.CollectFilePathFragments(queue);

            // The query (CDN signature) must be stripped; the stored fragment is the bare path the daemon requests
            // through lancache, so the manager's case-insensitive substring match hits when bytes flow.
            Assert.Single(fragments);
            Assert.Equal(path, fragments[0]);
        }

        [Fact]
        public void CollectFilePathFragments_DeduplicatesCaseInsensitively()
        {
            const string lower = "/filestreamingservice/files/aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee";
            const string upper = "/filestreamingservice/files/AAAAAAAA-BBBB-CCCC-DDDD-EEEEEEEEEEEE";

            var queue = new List<QueuedRequest>();
            queue.AddRange(ManifestHandler.BuildChunkRequests(lower, SampleHost, 2UL * 1048576UL));
            queue.AddRange(ManifestHandler.BuildChunkRequests(upper, SampleHost, 2UL * 1048576UL));

            var fragments = ManifestHandler.CollectFilePathFragments(queue);

            Assert.Single(fragments); // same GUID path in different casing collapses to one
        }

        [Fact]
        public void CollectFilePathFragments_EmptyQueue_YieldsNoFragments()
        {
            var fragments = ManifestHandler.CollectFilePathFragments(new List<QueuedRequest>());
            Assert.Empty(fragments);
        }
    }
}
