using LancachePrefill.Common;
using XboxPrefill.Api;

namespace XboxPrefill.Test;

public sealed class PrefillRunTests
{
    [Fact]
    public async Task ExclusiveLegacyRegistrationRejectsConcurrentAdmission()
    {
        var protocol = new PrefillProtocol(3, "3");
        using var commands = new SocketCommandInterface(0, protocol, (_, _) => Task.CompletedTask);
        var owner = (OwnedOperationCoordinator)typeof(SocketCommandInterface).GetField("_prefillOperation", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(commands)!;
        await owner.StartAsync(token => Task.Delay(Timeout.Infinite, token));
        var rejected = await commands.HandleCommandAsync(ConcurrentPrefillTests.Start("A", protocol), CancellationToken.None);
        Assert.False(rejected.Success);
        await owner.CancelAndWaitAsync();
    }

    [Fact]
    public async Task AuthenticationLossFailsEveryActiveRunAndBlocksAdmission()
    {
        var protocol = new PrefillProtocol(3, "3");
        var entered = new System.Collections.Concurrent.ConcurrentDictionary<string, TaskCompletionSource>();
        foreach (var id in new[] { "A", "B", "C" }) entered[id] = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var fail = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var commands = new SocketCommandInterface(0, protocol, async (run, token) =>
        {
            entered[run.OperationId].TrySetResult();
            if (run.OperationId == "A")
            {
                await fail.Task.WaitAsync(token);
                throw new XboxPrefill.Models.Exceptions.XboxLoginException("Account rejected");
            }
            await Task.Delay(Timeout.Infinite, token);
        });
        foreach (var id in new[] { "A", "B", "C" }) Assert.True((await commands.HandleCommandAsync(ConcurrentPrefillTests.Start(id, protocol), CancellationToken.None)).Success);
        await Task.WhenAll(entered.Values.Select(value => value.Task)).WaitAsync(TimeSpan.FromSeconds(10));
        fail.TrySetResult();
        var owner = (OwnedOperationCoordinator)typeof(SocketCommandInterface).GetField("_prefillOperation", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(commands)!;
        await Task.WhenAll(owner.WaitAsync("A"), owner.WaitAsync("B"), owner.WaitAsync("C")).WaitAsync(TimeSpan.FromSeconds(10));
        foreach (var id in new[] { "A", "B", "C" })
        {
            Assert.Equal("failed", owner.GetOperation(id)!.State);
            Assert.Equal("auth-lost", owner.GetOperation(id)!.Reason);
        }
        Assert.False((await commands.HandleCommandAsync(ConcurrentPrefillTests.Start("D", protocol), CancellationToken.None)).Success);
    }

    [Fact]
    public void InlineSelectionIsCanonicalAndImmutable()
    {
        var protocol = new PrefillProtocol(30);
        var command = ConcurrentPrefillTests.Start("A", protocol);
        command.Parameters!["appIds"] = "[\"a\",\" A \",\"b\"]";
        var captured = PrefillRun.Capture(command, protocol);
        command.Parameters["appIds"] = "[\"C\"]";
        command.Parameters["force"] = "true";
        Assert.Equal(new[] { "A", "B" }, captured.AppIds);
        Assert.False(captured.Force);
    }

    [Theory]
    [InlineData("[]", null)]
    [InlineData("[\"A\"]", "all")]
    [InlineData("[\" \"]", null)]
    public void InvalidInlineSelectionsReject(string ids, string? preset)
    {
        var protocol = new PrefillProtocol(30);
        var command = ConcurrentPrefillTests.Start("A", protocol);
        command.Parameters!["appIds"] = ids;
        if (preset != null) command.Parameters[preset] = "true";
        Assert.ThrowsAny<ArgumentException>(() => PrefillRun.Capture(command, protocol));
    }

    [Fact]
    public async Task ProgressPreservesActualBytesAndSkippedOutcome()
    {
        var protocol = new PrefillProtocol(3);
        using var budget = new RequestBudget(3);
        var run = new PrefillRun("run", protocol, new RunOptions { AppIds = new[] { "A", "B" }, MaxConcurrency = 1 }, budget, new ItemClaims(), NullProgress.Instance);
        run.OnDownloadProgress(new DownloadProgressInfo { AppId = "A", BytesDownloaded = 4, TotalBytes = 8 });
        run.OnAppCompleted(new AppDownloadInfo { AppId = "A", TotalBytes = 8 }, AppDownloadResult.Failed);
        run.OnAppCompleted(new AppDownloadInfo { AppId = "B" }, AppDownloadResult.Skipped);
        await run.CompleteAsync();
        Assert.Equal("failed", run.Progress.Snapshot.State);
        Assert.Equal(4, run.Progress.Snapshot.BytesTransferred);
        Assert.Equal(1, run.Progress.Snapshot.SkippedApps);
        Assert.Equal(0, run.Progress.Snapshot.CachedApps);
    }
}
