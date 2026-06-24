using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Spectre.Console;
using XboxPrefill.Api;
using XboxPrefill.Handlers;
using XboxPrefill.Models.ApiResponses;

namespace XboxPrefill.Smoke;

/// <summary>
/// Console IXboxAuthProvider that prints the device code to stdout in machine-parseable form
/// so a human can approve the login in a browser, and the orchestrator can scrape the code.
/// </summary>
internal sealed class ConsoleXboxAuthProvider : IXboxAuthProvider
{
    public Task PresentDeviceCodeAsync(string userCode, string verificationUri, CancellationToken cancellationToken = default)
    {
        Console.WriteLine();
        Console.WriteLine($"SMOKE-DEVICE-CODE: {userCode}");
        Console.WriteLine($"SMOKE-VERIFY-URL: {verificationUri}");
        Console.Out.Flush();
        return Task.CompletedTask;
    }

    public void CancelPendingRequest()
    {
        // No-op for the console provider — nothing to cancel.
    }
}

internal static class Program
{
    private static async Task<int> Main()
    {
        var console = AnsiConsole.Console;

        try
        {
            // ----------------------------------------------------------------
            // 1. Wire up the same way XboxManager does it
            // ----------------------------------------------------------------
            var authProvider = new ConsoleXboxAuthProvider();
            var accountManager = XboxAccountManager.LoadFromFile(console, authProvider);
            using var httpClientFactory = new HttpClientFactory(console, accountManager);
            var xboxApi = new XboxApi(console, httpClientFactory);

            // ----------------------------------------------------------------
            // 2. Login
            // ----------------------------------------------------------------
            await accountManager.LoginAsync();
            Console.WriteLine($"SMOKE-LOGIN-OK xuid={accountManager.Xuid}");

            // ----------------------------------------------------------------
            // 3. Enumerate owned titles
            // ----------------------------------------------------------------
            var titles = await xboxApi.GetOwnedTitlesAsync();
            Console.WriteLine($"SMOKE-TITLES count={titles.Count}");

            var preview = titles.Take(20).ToList();
            foreach (var t in preview)
            {
                Console.WriteLine($"SMOKE-TITLE: {t.Name} | productId={t.ProductId} | pfn={t.Pfn}");
            }

            // ----------------------------------------------------------------
            // 4. Resolve a download URL
            // ----------------------------------------------------------------
            const string MinecraftProductId = "9NBLGGH2JHXJ";
            var target = titles.FirstOrDefault(t => string.Equals(t.ProductId, MinecraftProductId, StringComparison.OrdinalIgnoreCase))
                         ?? titles.FirstOrDefault(t => !string.IsNullOrEmpty(t.ProductId));

            if (target == null)
            {
                Console.WriteLine("SMOKE-SKIP: no title with a productId found — cannot resolve download URL");
                return 0;
            }

            // Resolve productId -> contentId(s) via DisplayCatalog
            var contentIds = await xboxApi.GetContentIdsAsync(target.ProductId);
            if (contentIds.Count == 0)
            {
                Console.WriteLine($"SMOKE-SKIP: no contentIds returned for productId={target.ProductId}");
                return 0;
            }

            var contentId = contentIds[0];
            Console.WriteLine($"SMOKE-CONTENTID: {contentId}");

            // Fetch the base package for the first contentId
            var pkg = await xboxApi.GetBasePackageAsync(contentId);
            if (!pkg.PackageFound || pkg.PackageFiles == null || pkg.PackageFiles.Count == 0)
            {
                Console.WriteLine($"SMOKE-SKIP: package not found or no files for contentId={contentId}");
                return 0;
            }

            // Pick first non-.phf / non-.xsp file that has CDN roots
            PackageFile? fileEntry = pkg.PackageFiles.FirstOrDefault(
                f => f.CdnRootPaths is { Length: > 0 }
                     && !string.IsNullOrEmpty(f.RelativeUrl)
                     && !string.IsNullOrEmpty(f.FileName)
                     && !f.FileName.EndsWith(".phf", StringComparison.OrdinalIgnoreCase)
                     && !f.FileName.EndsWith(".xsp", StringComparison.OrdinalIgnoreCase));

            fileEntry ??= pkg.PackageFiles.FirstOrDefault(f => f.CdnRootPaths is { Length: > 0 });

            if (fileEntry == null)
            {
                Console.WriteLine("SMOKE-SKIP: no suitable PackageFile with CdnRootPaths found");
                return 0;
            }

            var downloadUrl = fileEntry.CdnRootPaths[0].TrimEnd('/') + "/" + fileEntry.RelativeUrl.TrimStart('/');
            Console.WriteLine($"SMOKE-DOWNLOAD-URL: {downloadUrl}");
            Console.WriteLine($"SMOKE-SIZE: {fileEntry.FileSize}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"SMOKE-ERROR: {ex.GetType().Name}: {ex.Message}");
            return 1;
        }

        return 0;
    }
}
