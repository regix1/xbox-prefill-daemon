using XboxPrefill.Handlers;
using XboxPrefill.Models;
using XboxPrefill.Api;
using LancachePrefill.Common;

namespace XboxPrefill.Test;

public sealed class CacheCommitTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CancellationAfterPreparationRefusesMarkerAndSuccess(bool chooseTerminal)
    {
        var directory = Directory.CreateTempSubdirectory();
        var path = Path.Combine(directory.FullName, "success.json");
        using var budget = new RequestBudget(1);
        var run = CreateRun(budget);
        var app = new AppInfo { AppId = "A", BuildVersion = "1" };
        var replacementCalls = 0;
        var handler = new AppInfoHandler(path, (source, target) =>
        {
            replacementCalls++;
            File.Move(source, target, true);
        }, () =>
        {
            run.Progress.MarkCancelling();
            if (chooseTerminal) run.Progress.TryChooseTerminal("cancelled");
        });
        try
        {
            Assert.False(handler.MarkDownloadAsSuccessful(app, run,
                new AppDownloadInfo { AppId = "A", TotalBytes = 8 }, CancellationToken.None));
            Assert.Equal(0, replacementCalls);
            Assert.False(File.Exists(path));
            Assert.False(handler.AppIsUpToDate(app));
            Assert.Equal(0, run.Progress.Snapshot.CompletedApps);
            Assert.Null(run.Progress.GetPage().Items.Single().Result);
            Assert.Empty(directory.GetFiles());
        }
        finally { directory.Delete(); }
    }

    [Fact]
    public async Task MarkerCommitPrecedesContendingCancellationAndRetainsItemSuccess()
    {
        var directory = Directory.CreateTempSubdirectory();
        var path = Path.Combine(directory.FullName, "success.json");
        using var budget = new RequestBudget(1);
        var run = CreateRun(budget);
        var app = new AppInfo { AppId = "A", BuildVersion = "1" };
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelling = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var handler = new AppInfoHandler(path, (source, target) =>
        {
            entered.TrySetResult();
            Assert.True(release.Wait(TimeSpan.FromSeconds(10)));
            File.Move(source, target, true);
        });
        Task<bool>? commit = null;
        Task<bool>? cancel = null;
        try
        {
            commit = Task.Run(() => handler.MarkDownloadAsSuccessful(app, run,
                new AppDownloadInfo { AppId = "A", TotalBytes = 8 }, CancellationToken.None));
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            cancel = Task.Run(() =>
            {
                cancelling.TrySetResult();
                return run.Progress.TryChooseTerminal("cancelled");
            });
            await cancelling.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(cancel.IsCompleted);
            release.Set();
            Assert.True(await commit.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.True(await cancel.WaitAsync(TimeSpan.FromSeconds(10)));
            await run.CompleteAsync();
            Assert.True(handler.AppIsUpToDate(app));
            Assert.Equal("success", run.Progress.GetPage().Items.Single().Result);
            Assert.Equal(1, run.Progress.Snapshot.CompletedApps);
            Assert.Equal(8, run.Progress.Snapshot.BytesTransferred);
            Assert.Equal("cancelled", run.Progress.Snapshot.State);
        }
        finally
        {
            release.Set();
            if (commit != null) await commit;
            if (cancel != null) await cancel;
            File.Delete(path);
            directory.Delete();
        }
    }

    [Fact]
    public async Task ReplacementFailureReachesRunFailureWithoutPublishingSuccess()
    {
        var directory = Directory.CreateTempSubdirectory();
        var path = Path.Combine(directory.FullName, "success.json");
        var baseline = new AppInfo { AppId = "B", BuildVersion = "2" };
        var saved = new AppInfoHandler(path, (source, target) => File.Move(source, target, true));
        saved.MarkDownloadAsSuccessful(baseline);
        var original = File.ReadAllBytes(path);
        var failure = new IOException("Injected replacement failure");
        var handler = new AppInfoHandler(path, (_, _) => throw failure);
        var protocol = new PrefillProtocol(1);
        await using var commands = new SocketCommandInterface(0, protocol, (run, token) =>
        {
            run.OnDownloadProgress(new DownloadProgressInfo { AppId = "A", BytesDownloaded = 8, TotalBytes = 8 });
            handler.MarkDownloadAsSuccessful(new AppInfo { AppId = "A", BuildVersion = "1" }, run,
                new AppDownloadInfo { AppId = "A", TotalBytes = 8 }, token);
            return Task.CompletedTask;
        });
        try
        {
            Assert.True((await commands.HandleCommandAsync(ConcurrentPrefillTests.Start("A", protocol), CancellationToken.None)).Success);
            var owner = (OwnedOperationCoordinator)typeof(SocketCommandInterface).GetField("_prefillOperation", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(commands)!;
            var result = await owner.WaitAsync("A").WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Same(failure, result.Exception);
            var page = owner.GetOperation("A", 0, 100)!;
            Assert.Equal("failed", page.Operation.State);
            Assert.Equal("cache-write-failed", page.Operation.Reason);
            Assert.Equal(0, page.Operation.CompletedApps);
            Assert.NotEqual("success", page.Items.Single().Result);
            Assert.Equal(original, File.ReadAllBytes(path));
            Assert.False(saved.AppIsUpToDate(new AppInfo { AppId = "A", BuildVersion = "1" }));
            Assert.Single(directory.GetFiles());
        }
        finally
        {
            await commands.StopAsync();
            File.Delete(path);
            directory.Delete();
        }
    }

    private static PrefillRun CreateRun(RequestBudget budget)
    {
        var run = new PrefillRun("run", new PrefillProtocol(1),
            new RunOptions { AppIds = new[] { "A" }, MaxConcurrency = 1 }, budget, new ItemClaims(), NullProgress.Instance);
        run.OnDownloadProgress(new DownloadProgressInfo { AppId = "A", BytesDownloaded = 8, TotalBytes = 8 });
        return run;
    }

    [Fact]
    public async Task DisjointCommitsMergeAndFailedReplacementPreservesTruth()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "success.json");
        try
        {
            var first = new AppInfoHandler(path, (source, target) => File.Move(source, target, true));
            var second = new AppInfoHandler(path, (source, target) => File.Move(source, target, true));
            var a = new AppInfo { AppId = "A", BuildVersion = "1" };
            var b = new AppInfo { AppId = "B", BuildVersion = "2" };
            await Task.WhenAll(Task.Run(() => first.MarkDownloadAsSuccessful(a)), Task.Run(() => second.MarkDownloadAsSuccessful(b)));
            Assert.True(first.AppIsUpToDate(a));
            Assert.True(first.AppIsUpToDate(b));
            var bytes = File.ReadAllBytes(path);
            var failed = new AppInfoHandler(path, (_, _) => throw new IOException("Injected replacement failure"));
            var c = new AppInfo { AppId = "C", BuildVersion = "3" };
            Assert.Throws<IOException>(() => failed.MarkDownloadAsSuccessful(c));
            Assert.Equal(bytes, File.ReadAllBytes(path));
            Assert.False(first.AppIsUpToDate(c));
        }
        finally
        {
            File.Delete(path);
            Directory.Delete(directory);
        }
    }
}
