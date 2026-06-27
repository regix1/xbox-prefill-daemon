using XboxPrefill.Api;
using XboxPrefill.Settings;

namespace XboxPrefill
{
    public static class Program
    {
        public static async Task<int> Main()
        {
            try
            {
                ParseHiddenFlags();

                Console.WriteLine($"""
                    ╔═══════════════════════════════════════════════════════════╗
                    ║              XboxPrefill Daemon                           ║
                    ║                  v{ThisAssembly.Info.InformationalVersion,-20}             ║
                    ╚═══════════════════════════════════════════════════════════╝

                    """);

                var tcpPortEnv = Environment.GetEnvironmentVariable("PREFILL_TCP_PORT");
                var useTcp = int.TryParse(tcpPortEnv, out var tcpPort) && tcpPort > 0;

                if (!useTcp)
                {
                    Console.WriteLine("Using Unix Domain Socket for reliable, low-latency IPC.");
                    Console.WriteLine();
                }

                var responsesDir = Environment.GetEnvironmentVariable("PREFILL_RESPONSES_DIR") ?? "/responses";
                var socketPath = Environment.GetEnvironmentVariable("PREFILL_SOCKET_PATH") ??
                                Path.Combine(responsesDir, "daemon.sock");

                using var cts = new CancellationTokenSource();

                Console.CancelKeyPress += (_, e) =>
                {
                    e.Cancel = true;
                    Console.WriteLine("\nShutdown signal received...");
                    cts.Cancel();
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
                Console.WriteLine($"Fatal error: {e.Message}");
                if (AppConfig.DebugLogs)
                {
                    Console.WriteLine(e.StackTrace);
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

            Console.WriteLine($"Max lifetime configured: daemon will self-shutdown after {seconds} second(s).");

            var dueTime = TimeSpan.FromSeconds(seconds);
            return new System.Threading.Timer(_ =>
            {
                Console.WriteLine($"\nMax lifetime of {seconds}s reached - initiating clean shutdown...");
                try { cts.Cancel(); }
                catch (ObjectDisposedException) { /* already shutting down */ }
            }, null, dueTime, System.Threading.Timeout.InfiniteTimeSpan);
        }

        private static void ParseHiddenFlags()
        {
            var args = Environment.GetCommandLineArgs().Skip(1).ToList();

            if (args.Any(e => e.Contains("--debug")))
            {
                Console.WriteLine($"Using --debug flag. Displaying debug only logging...");
                Console.WriteLine($"Additional debugging files will be output to {AppConfig.DebugOutputDir}");
                AppConfig.DebugLogs = true;
            }

            if (args.Any(e => e.Contains("--no-download")))
            {
                Console.WriteLine($"Using --no-download flag. Will skip downloading chunks...");
                AppConfig.SkipDownloads = true;
            }

            if (args.Any(e => e.Contains("--nocache")) || args.Any(e => e.Contains("--no-cache")))
            {
                Console.WriteLine($"Using --nocache flag. Will always re-download manifests...");
                AppConfig.NoLocalCache = true;
            }

            if (AppConfig.DebugLogs || AppConfig.SkipDownloads || AppConfig.NoLocalCache)
            {
                Console.WriteLine();
                Console.WriteLine(new string('─', 60));
            }
        }
    }
}
