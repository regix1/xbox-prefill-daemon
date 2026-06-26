using XboxPrefill.Diagnostics;

namespace XboxPrefill.Test
{
    /// <summary>
    /// Locks the request-path projection used by the opt-in <c>[MAP]</c> diagnostics. The whole point of the
    /// diagnostics is to let a human compare, from <c>docker logs</c>, the <c>/filestreamingservice/files/&lt;GUID&gt;</c>
    /// fragment that <c>get-cdn-info</c> EMITS against the GUID the daemon actually REQUESTS. That only works if both
    /// sides are formatted identically, so <see cref="MappingDebugLogger.ToFragment"/> must strip the CDN signature
    /// query string exactly the way <see cref="XboxPrefill.Handlers.ManifestHandler.CollectFilePathFragments"/> does.
    /// </summary>
    public sealed class MappingDebugLoggerTests
    {
        [Fact]
        public void ToFragment_StripsQueryString_MatchesEmittedFragmentShape()
        {
            const string path = "/filestreamingservice/files/11111111-2222-3333-4444-555555555555";
            const string signedUrl = path + "?P1=1719273600&P2=900&P3=2&P4=AbCdEfGhIjKlMnOpQrStUvWxYz0123456789";

            Assert.Equal(path, MappingDebugLogger.ToFragment(signedUrl));
        }

        [Fact]
        public void ToFragment_PathWithoutQuery_ReturnedUnchanged()
        {
            const string path = "/filestreamingservice/files/aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee";

            Assert.Equal(path, MappingDebugLogger.ToFragment(path));
        }
    }
}
