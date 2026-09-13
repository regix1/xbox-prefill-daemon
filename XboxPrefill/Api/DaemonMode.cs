using Spectre.Console;
#nullable enable

namespace XboxPrefill.Api;

/// <summary>
/// Runs XboxPrefill in daemon mode using Unix Domain Socket or TCP for IPC.
/// </summary>
public static class DaemonMode
{
    public static async Task RunAsync(
        string socketPath = "/responses/daemon.sock",
        CancellationToken cancellationToken = default)
    {
        AnsiConsole.WriteLine($"Starting XboxPrefill daemon on Unix socket {socketPath}");

        await using var socketInterface = new SocketCommandInterface(socketPath);

        await socketInterface.StartAsync(cancellationToken);

        try
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            AnsiConsole.WriteLine("Daemon shutdown requested...");
        }

        await socketInterface.StopAsync();
        AnsiConsole.WriteLine("Daemon stopped.");
    }

    public static async Task RunTcpAsync(
        int port,
        CancellationToken cancellationToken = default)
    {
        AnsiConsole.WriteLine($"Starting XboxPrefill daemon on TCP port {port}");

        await using var socketInterface = new SocketCommandInterface(port);

        await socketInterface.StartAsync(cancellationToken);

        try
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            AnsiConsole.WriteLine("Daemon shutdown requested...");
        }

        await socketInterface.StopAsync();
        AnsiConsole.WriteLine("Daemon stopped.");
    }
}
