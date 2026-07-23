using System.Net;
using System.Reflection;
using Spectre.Console;
using XboxPrefill.Api;
using XboxPrefill.Handlers;
using XboxPrefill.Models.ApiResponses;

namespace XboxPrefill.Test;

public sealed class XboxApiCancellationTests
{
    [Fact]
    public async Task GetContentIdsAsync_CancelsActiveSend()
    {
        using var sharedHandler = new CancellationProbeHandler();
        using var anonymousHandler = new CancellationProbeHandler(blockSend: true);
        var accountManager = CreateAccountManager();
        using var factory = new HttpClientFactory(
            AnsiConsole.Console,
            accountManager,
            sharedHandler,
            anonymousHandler);
        var api = new XboxApi(AnsiConsole.Console, factory);
        using var cancellation = new CancellationTokenSource();

        var request = api.GetContentIdsAsync("product-id", cancellation.Token);
        var sendToken = await anonymousHandler.SendStarted.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(sendToken.CanBeCanceled);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
    }

    [Fact]
    public async Task GetContentIdsAsync_CancelsActiveResponseRead()
    {
        using var sharedHandler = new CancellationProbeHandler();
        using var anonymousHandler = new CancellationProbeHandler();
        var accountManager = CreateAccountManager();
        using var factory = new HttpClientFactory(
            AnsiConsole.Console,
            accountManager,
            sharedHandler,
            anonymousHandler);
        var api = new XboxApi(AnsiConsole.Console, factory);
        using var cancellation = new CancellationTokenSource();

        var request = api.GetContentIdsAsync("product-id", cancellation.Token);
        var sendToken = await anonymousHandler.SendStarted.WaitAsync(TimeSpan.FromSeconds(5));
        var readToken = await anonymousHandler.ReadStarted.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(sendToken.CanBeCanceled);
        Assert.True(readToken.CanBeCanceled);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
    }

    [Fact]
    public async Task GetBasePackageAsync_CancelsActiveSend()
    {
        using var sharedHandler = new CancellationProbeHandler(blockSend: true);
        using var anonymousHandler = new CancellationProbeHandler();
        var accountManager = CreateAccountManager(withValidPackageToken: true);
        using var factory = new HttpClientFactory(
            AnsiConsole.Console,
            accountManager,
            sharedHandler,
            anonymousHandler);
        var api = new XboxApi(AnsiConsole.Console, factory);
        using var cancellation = new CancellationTokenSource();

        var request = api.GetBasePackageAsync("content-id", cancellation.Token);
        var sendToken = await sharedHandler.SendStarted.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(sendToken.CanBeCanceled);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
    }

    [Fact]
    public async Task GetBasePackageAsync_CancelsActiveResponseRead()
    {
        using var sharedHandler = new CancellationProbeHandler();
        using var anonymousHandler = new CancellationProbeHandler();
        var accountManager = CreateAccountManager(withValidPackageToken: true);
        using var factory = new HttpClientFactory(
            AnsiConsole.Console,
            accountManager,
            sharedHandler,
            anonymousHandler);
        var api = new XboxApi(AnsiConsole.Console, factory);
        using var cancellation = new CancellationTokenSource();

        var request = api.GetBasePackageAsync("content-id", cancellation.Token);
        var sendToken = await sharedHandler.SendStarted.WaitAsync(TimeSpan.FromSeconds(5));
        var readToken = await sharedHandler.ReadStarted.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(sendToken.CanBeCanceled);
        Assert.True(readToken.CanBeCanceled);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
    }

    private static XboxAccountManager CreateAccountManager(bool withValidPackageToken = false)
    {
        var manager = (XboxAccountManager)Activator.CreateInstance(
            typeof(XboxAccountManager),
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            args: new object[] { AnsiConsole.Console, new NullXboxAuthProvider() },
            culture: null)!;

        if (!withValidPackageToken)
        {
            return manager;
        }

        using var signer = XblRequestSigner.CreateNew();
        var account = new XboxAccount
        {
            RefreshToken = "refresh-token",
            RefreshTokenIssuedUtc = DateTime.UtcNow,
            DisplayName = "test-account",
            Xuid = "test-xuid",
            DeviceKeyPkcs8 = signer.ExportPkcs8Base64(),
            XboxLiveToken = "title-token",
            XboxLiveUhs = "title-uhs",
            XboxLiveExpiresAt = DateTime.UtcNow.AddHours(1),
            UpdateToken = "package-token",
            UpdateUhs = "package-uhs",
            UpdateExpiresAt = DateTime.UtcNow.AddHours(1)
        };

        typeof(XboxAccountManager)
            .GetProperty(nameof(XboxAccountManager.Account))!
            .SetValue(manager, account);
        typeof(XboxAccountManager)
            .GetField("_signer", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(manager, XblRequestSigner.FromPkcs8Base64(account.DeviceKeyPkcs8));

        return manager;
    }

    private sealed class NullXboxAuthProvider : IXboxAuthProvider
    {
        public Task PresentDeviceCodeAsync(
            string userCode,
            string verificationUri,
            CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public void CancelPendingRequest()
        {
        }
    }

    private sealed class CancellationProbeHandler : HttpMessageHandler
    {
        private readonly bool _blockSend;
        private readonly BlockingReadStream _responseStream = new();
        private readonly TaskCompletionSource<CancellationToken> _sendStarted = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<CancellationToken> SendStarted => _sendStarted.Task;
        public Task<CancellationToken> ReadStarted => _responseStream.ReadStarted;

        public CancellationProbeHandler(bool blockSend = false)
        {
            _blockSend = blockSend;
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            _sendStarted.TrySetResult(cancellationToken);

            if (_blockSend)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(_responseStream)
            };
        }
    }

    private sealed class BlockingReadStream : Stream
    {
        private readonly TaskCompletionSource<CancellationToken> _readStarted = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<CancellationToken> ReadStarted => _readStarted.Task;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count)
            => throw new NotSupportedException();

        public override async Task<int> ReadAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken)
        {
            _readStarted.TrySetResult(cancellationToken);
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            _readStarted.TrySetResult(cancellationToken);
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }

        public override long Seek(long offset, SeekOrigin origin)
            => throw new NotSupportedException();

        public override void SetLength(long value)
            => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
            => throw new NotSupportedException();
    }
}
