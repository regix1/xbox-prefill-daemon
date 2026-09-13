#nullable enable annotations

using XboxPrefill.Api;
using XboxPrefill.Settings;
using Spectre.Console;

namespace XboxPrefill
{
    public static class Program
    {
        public static async Task<int> Main()
        {
            try
            {
                ParseHiddenFlags();

                AnsiConsole.WriteLine($"XboxPrefill daemon v{ThisAssembly.Info.InformationalVersion}");

                var tcpPortEnv = Environment.GetEnvironmentVariable("PREFILL_TCP_PORT");
                var useTcp = int.TryParse(tcpPortEnv, out var tcpPort) && tcpPort > 0;

                var responsesDir = Environment.GetEnvironmentVariable("PREFILL_RESPONSES_DIR") ?? "/responses";
                var socketPath = Environment.GetEnvironmentVariable("PREFILL_SOCKET_PATH") ??
                                Path.Combine(responsesDir, "daemon.sock");

                using var cts = new CancellationTokenSource();

                Console.CancelKeyPress += (_, e) =>
                {
                    e.Cancel = true;
                    AnsiConsole.WriteLine("\nShutdown signal received...");
#pragma warning disable AsyncFixer02 // Console signal callbacks cannot await asynchronous cancellation.
#pragma warning disable CA1849
#pragma warning disable VSTHRD103
                    cts.Cancel();
#pragma warning restore VSTHRD103
#pragma warning restore CA1849
#pragma warning restore AsyncFixer02
                };

                // Optional self-shutdown timer. When PREFILL_MAX_LIFETIME_SECONDS is a positive integer the
                // daemon cancels the host CTS on elapse, which unblocks the daemon's Task.Delay(Infinite) and
                // drives a clean shutdown (process exits 0 / the container stops). Unset or <= 0 = run forever.
                using var lifetimeTimer = StartMaxLifetimeTimer(cts);

                if (useTcp)
                {
                    await DaemonMode.RunTcpAsync(tcpPort, cts.Token);
                }
                else
                {
                    await DaemonMode.RunAsync(socketPath, cts.Token);
                }

                return 0;
            }
            catch (OperationCanceledException)
            {
                return 0;
            }
            catch (Exception e)
            {
                AnsiConsole.WriteLine($"Fatal error: {e.Message}");
                if (AppConfig.DebugLogs)
                {
                    AnsiConsole.WriteLine(e.StackTrace ?? string.Empty);
                }
                return 1;
            }
        }

        /// <summary>
        /// Reads <c>PREFILL_MAX_LIFETIME_SECONDS</c>; when it parses to a positive integer, starts a one-shot
        /// timer that cancels <paramref name="cts"/> on elapse to trigger a clean shutdown. Returns the timer
        /// (kept alive by the caller via <c>using</c>) or null when no lifetime cap is configured.
        /// </summary>
        private static System.Threading.Timer? StartMaxLifetimeTimer(CancellationTokenSource cts)
        {
            var raw = Environment.GetEnvironmentVariable("PREFILL_MAX_LIFETIME_SECONDS");
            if (!int.TryParse(raw, out var seconds) || seconds <= 0)
            {
                return null;
            }

            AnsiConsole.WriteLine($"Max lifetime configured: daemon will self-shutdown after {seconds} second(s).");

            var dueTime = TimeSpan.FromSeconds(seconds);
            return new System.Threading.Timer(_ =>
            {
                AnsiConsole.WriteLine($"\nMax lifetime of {seconds}s reached - initiating clean shutdown...");
                try
                {
#pragma warning disable AsyncFixer02 // Timer callbacks cannot await asynchronous cancellation.
#pragma warning disable CA1849
#pragma warning disable VSTHRD103
                    cts.Cancel();
#pragma warning restore VSTHRD103
#pragma warning restore CA1849
#pragma warning restore AsyncFixer02
                }
                catch (ObjectDisposedException) { /* already shutting down */ }
            }, null, dueTime, System.Threading.Timeout.InfiniteTimeSpan);
        }

        private static void ParseHiddenFlags()
        {
            var args = Environment.GetCommandLineArgs().Skip(1).ToList();

            if (args.Any(e => e.Contains("--debug")))
            {
                AnsiConsole.WriteLine("Using --debug flag. Displaying debug only logging...");
                AnsiConsole.WriteLine($"Additional debugging files will be output to {AppConfig.DebugOutputDir}");
                AppConfig.DebugLogs = true;
            }

            if (args.Any(e => e.Contains("--no-download")))
            {
                AnsiConsole.WriteLine("Using --no-download flag. Will skip downloading chunks...");
                AppConfig.SkipDownloads = true;
            }

            if (args.Any(e => e.Contains("--nocache")) || args.Any(e => e.Contains("--no-cache")))
            {
                AnsiConsole.WriteLine("Using --nocache flag. Will always re-download manifests...");
                AppConfig.NoLocalCache = true;
            }

            if (AppConfig.DebugLogs || AppConfig.SkipDownloads || AppConfig.NoLocalCache)
            {
                AnsiConsole.WriteLine();
                AnsiConsole.WriteLine(new string('─', 60));
            }
        }
    }
}

#nullable restore annotations
