using System.Collections.Concurrent;
using System.Net;
using LancachePrefill.Common;
using XboxPrefill.Api;
using XboxPrefill.Handlers;
using XboxPrefill.Models.ApiResponses;

namespace XboxPrefill.Test;

public sealed class ConcurrentPrefillTests
{
    [Fact]
    public async Task ThreeBodiesOverlapAndCancellationRetainsSiblingWork()
    {
        var protocol = new PrefillProtocol(3, "3");
        using var http = new BodyHandler();
        using var commands = new SocketCommandInterface(0, protocol, async (run, token) =>
        {
            var id = run.Options.AppIds![0];
            using var claim = run.Claims.TryClaim(run.OperationId, new[] { id });
            var app = new AppDownloadInfo { AppId = id, Name = id, TotalBytes = 8 };
            if (claim == null)
            {
                run.OnAppCompleted(app, AppDownloadResult.Skipped);
                return;
            }
            run.OnAppStarted(app);
            var console = new ApiConsoleAdapter(new SilentLogin(), run);
            using var download = new DownloadHandler(console, run, http, "127.0.0.1");
            try
            {
                var queue = ManifestHandler.BuildChunkRequests("/" + id, "content.test", 8).ToList();
                Assert.True(await download.DownloadQueuedChunksAsync(queue,
                    new PackageManifest { CdnHost = "content.test", CdnRootUrl = "http://content.test", Version = "1" }, id, id, token));
                run.OnAppCompleted(app, AppDownloadResult.Success);
            }
            finally
            {
                run.OnDownloadProgress(new DownloadProgressInfo { AppId = id, AppName = id, TotalBytes = 8, BytesDownloaded = download.BytesTransferred });
            }
        });
        try
        {
            foreach (var id in new[] { "A", "B", "C" })
            {
                var response = await commands.HandleCommandAsync(Start(id, protocol), CancellationToken.None);
                Assert.True(response.Success, response.Error);
            }
            await Task.WhenAll(http.Get("A").Entered.Task, http.Get("B").Entered.Task, http.Get("C").Entered.Task)
                .WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(3, http.Active);
            var full = await commands.HandleCommandAsync(Start("D", protocol), CancellationToken.None);
            Assert.False(full.Success);
            Assert.Equal("run-limit", full.Error);
            Assert.True((await commands.HandleCommandAsync(Start("A", protocol), CancellationToken.None)).Success);
            Assert.False((await commands.HandleCommandAsync(new CommandRequest { Type = "cancel-prefill" }, CancellationToken.None)).Success);
            var wrong = Cancel("A", protocol);
            wrong.Parameters!["daemonInstanceId"] = Guid.NewGuid().ToString();
            Assert.False((await commands.HandleCommandAsync(wrong, CancellationToken.None)).Success);
            Assert.True((await commands.HandleCommandAsync(Cancel("A", protocol), CancellationToken.None)).Success);
            await http.Get("A").Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(http.Get("B").Cancelled.Task.IsCompleted);
            Assert.False(http.Get("C").Cancelled.Task.IsCompleted);
            http.Get("B").Release.TrySetResult();
            http.Get("C").Release.TrySetResult();
            var owner = (OwnedOperationCoordinator)typeof(SocketCommandInterface).GetField("_prefillOperation", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(commands)!;
            await Task.WhenAll(owner.WaitAsync("A"), owner.WaitAsync("B"), owner.WaitAsync("C")).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal("completed", owner.GetOperation("B")!.State);
            Assert.Equal("completed", owner.GetOperation("C")!.State);
            await commands.StopAsync();
            var page = await commands.HandleCommandAsync(new CommandRequest
            {
                Type = "get-operation",
                Parameters = new() { ["operationId"] = "A", ["daemonInstanceId"] = protocol.DaemonInstanceId }
            }, CancellationToken.None);
            var operation = Assert.IsType<OperationPage>(page.Data);
            Assert.Equal("cancelled", operation.Operation.State);
            Assert.Equal(4, operation.Operation.BytesTransferred);
            Assert.Equal(0, http.Active);
        }
        finally
        {
            foreach (var id in new[] { "A", "B", "C" }) http.Get(id).Release.TrySetResult();
        }
    }

    internal static CommandRequest Start(string id, PrefillProtocol protocol) => new()
    {
        Id = id,
        Type = "prefill",
        Parameters = new()
        {
            ["protocolVersion"] = "2",
            ["daemonInstanceId"] = protocol.DaemonInstanceId,
            ["appIds"] = "[\"" + id + "\"]",
            ["maxConcurrency"] = "1"
        }
    };

    private static CommandRequest Cancel(string id, PrefillProtocol protocol) => new()
    {
        Type = "cancel-prefill",
        Parameters = new() { ["operationId"] = id, ["daemonInstanceId"] = protocol.DaemonInstanceId }
    };

    private sealed class SilentLogin : IXboxAuthProvider
    {
        public Task PresentDeviceCodeAsync(string userCode, string verificationUri, CancellationToken cancellationToken = default) => throw new InvalidOperationException();
        public void CancelPendingRequest() { }
    }

    private sealed class BodyHandler : HttpMessageHandler
    {
        private readonly ConcurrentDictionary<string, Body> _bodies = new();
        private int _active;
        public int Active => Volatile.Read(ref _active);
        public Body Get(string id) => _bodies.GetOrAdd(id, _ => new Body(() => Interlocked.Increment(ref _active), () => Interlocked.Decrement(ref _active)));
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(Get(request.RequestUri!.AbsolutePath.Trim('/'))) });
        protected override void Dispose(bool disposing) { }
    }

    private sealed class Body : Stream
    {
        private readonly Action _enter;
        private readonly Action _exit;
        private int _reads;
        private int _disposed;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Body(Action enter, Action exit) { _enter = enter; _exit = exit; }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var read = Interlocked.Increment(ref _reads);
            if (read == 1) { _enter(); buffer.Span[..4].Clear(); return 4; }
            if (read > 2) return 0;
            Entered.TrySetResult();
            try { await Release.Task.WaitAsync(cancellationToken); }
            catch (OperationCanceledException) { Cancelled.TrySetResult(); throw; }
            buffer.Span[..4].Clear();
            return 4;
        }
        protected override void Dispose(bool disposing)
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0 && _reads > 0) _exit();
            base.Dispose(disposing);
        }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => 8;
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
