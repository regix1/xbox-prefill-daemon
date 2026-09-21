using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using LancachePrefill.Common;
using Spectre.Console;
using XboxPrefill.Api;
using XboxPrefill.Handlers;
using XboxPrefill.Models;
using XboxPrefill.Models.ApiResponses;

namespace XboxPrefill.Test;

[Collection("ProcessEnvironment")]
public sealed class CacheStatusTests
{
    private static readonly DateTimeOffset StartTime = new(2026, 9, 21, 4, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task LegacyAndV2WireStayCompatible()
    {
        var clock = new CacheStatusClock(StartTime);
        using var fixture = new CacheFixture(clock, cacheOwnedApp: true);
        var request = new List<CachedAppInput> { new() { AppId = "OWNED" } };

        var legacy = await fixture.Api.CheckCacheStatusAsync(request);
        var v2 = await fixture.Api.CheckCacheStatusAsync(
            request,
            expiresAtUtc: StartTime.AddMinutes(1),
            version: 2);

        Assert.Equal("Checked 1 apps", legacy.Message);
        Assert.Null(v2.Message);

        var legacyJson = JsonSerializer.Serialize(
            legacy,
            DaemonSerializationContext.Default.CacheStatusResult).Replace("\r\n", "\n", StringComparison.Ordinal);
        var v2Json = JsonSerializer.Serialize(
            v2,
            DaemonSerializationContext.Default.CacheStatusResult).Replace("\r\n", "\n", StringComparison.Ordinal);

        Assert.Equal(
            """
            {
              "apps": [
                {
                  "appId": "OWNED",
                  "name": "Owned App",
                  "isUpToDate": true
                }
              ],
              "message": "Checked 1 apps"
            }
            """.Replace("\r\n", "\n", StringComparison.Ordinal),
            legacyJson);
        Assert.Equal(
            """
            {
              "apps": [
                {
                  "appId": "OWNED",
                  "name": "Owned App",
                  "isUpToDate": true,
                  "outcome": "Current"
                }
              ],
              "version": 2
            }
            """.Replace("\r\n", "\n", StringComparison.Ordinal),
            v2Json);
    }

    [Fact]
    public async Task ExpiredV2ReturnsEveryRowWithoutHttp()
    {
        var clock = new CacheStatusClock(StartTime);
        using var fixture = new CacheFixture(clock);
        var requested = new List<CachedAppInput>
        {
            new() { AppId = "OWNED", Revision = "revision-1" },
            new() { AppId = "MANUAL", Revision = "revision-1" }
        };

        var result = await fixture.Api.CheckCacheStatusAsync(
            requested,
            expiresAtUtc: StartTime.AddSeconds(2),
            version: 2);

        Assert.Equal(2, result.Version);
        Assert.Null(result.Message);
        Assert.Equal(new[] { "OWNED", "MANUAL" }, result.Apps.Select(app => app.AppId));
        Assert.All(result.Apps, app =>
        {
            Assert.False(app.IsUpToDate);
            Assert.Equal(CacheOutcome.Unknown, app.Outcome);
            Assert.Equal(CacheReason.DeadlineReached, app.Reason);
        });
        Assert.Equal(0, fixture.Http.Requests);
    }

    [Fact]
    public async Task MalformedV2FailsAtSocketBoundary()
    {
        var clock = new CacheStatusClock(StartTime);
        var protocol = new PrefillProtocol(1, "1");
        await using var commands = new SocketCommandInterface(
            0,
            protocol,
            (_, _) => Task.CompletedTask,
            clock);
        var missingExpiry = CreateCommand(
            "[{\"appId\":\"OWNED\",\"revision\":\"revision-1\"}]",
            version: "2");

        var missingExpiryResponse = await commands.HandleCommandAsync(missingExpiry, CancellationToken.None);

        Assert.False(missingExpiryResponse.Success);
        Assert.Contains("expiresAtUtc", missingExpiryResponse.Error, StringComparison.Ordinal);

        var duplicateIds = CreateCommand(
            "[{\"appId\":\"OWNED\"},{\"appId\":\"owned\"}]",
            version: "2",
            expiresAtUtc: StartTime.AddMinutes(1).ToString("O"));
        var duplicateResponse = await commands.HandleCommandAsync(duplicateIds, CancellationToken.None);

        Assert.False(duplicateResponse.Success);
        Assert.Contains("unique", duplicateResponse.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StatusAdvertisesCacheStatusV2Locally()
    {
        var protocol = new PrefillProtocol(1, "1");
        await using var commands = new SocketCommandInterface(
            0,
            protocol,
            (_, _) => Task.CompletedTask,
            new CacheStatusClock(StartTime));

        var response = await commands.HandleCommandAsync(
            new CommandRequest { Id = "status", Type = "status" },
            CancellationToken.None);

        var status = Assert.IsType<StatusData>(response.Data);
        Assert.Contains("cacheStatusV2", status.Features);
    }

    [Fact]
    public async Task DeadlineKeepsCompletedRowAndCancelsHeldHttp()
    {
        var clock = new CacheStatusClock(StartTime);
        using var fixture = new CacheFixture(clock);
        var inspection = fixture.Api.CheckCacheStatusAsync(
            new List<CachedAppInput>
            {
                new() { AppId = "OWNED", Revision = "revision-1" },
                new() { AppId = "HELD", Revision = "revision-1" }
            },
            expiresAtUtc: StartTime.AddSeconds(10),
            version: 2);

        await fixture.Http.HeldStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        clock.Advance(TimeSpan.FromSeconds(8));
        var result = await inspection.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Collection(
            result.Apps,
            completed =>
            {
                Assert.Equal("OWNED", completed.AppId);
                Assert.Equal(CacheOutcome.Current, completed.Outcome);
                Assert.Null(completed.Reason);
            },
            timedOut =>
            {
                Assert.Equal("HELD", timedOut.AppId);
                Assert.Equal(CacheOutcome.Unknown, timedOut.Outcome);
                Assert.Equal(CacheReason.DeadlineReached, timedOut.Reason);
            });
        Assert.Null(result.Message);
        await fixture.Http.HeldCancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, fixture.Http.Active);
    }

    [Fact]
    public async Task LegacyExpiryReturnsOnlyCompletedDeterminateRows()
    {
        var clock = new CacheStatusClock(StartTime);
        using var fixture = new CacheFixture(clock);
        var inspection = fixture.Api.CheckCacheStatusAsync(
            new List<CachedAppInput>
            {
                new() { AppId = "OWNED", Revision = "revision-1" },
                new() { AppId = "HELD", Revision = "revision-1" }
            },
            expiresAtUtc: StartTime.AddSeconds(10));

        await fixture.Http.HeldStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        clock.Advance(TimeSpan.FromSeconds(8));
        var result = await inspection.WaitAsync(TimeSpan.FromSeconds(5));

        var app = Assert.Single(result.Apps);
        Assert.Equal("OWNED", app.AppId);
        Assert.True(app.IsUpToDate);
        Assert.Null(app.Outcome);
        Assert.Null(app.Reason);
        Assert.Null(result.Version);
        Assert.Equal("Checked 1 apps", result.Message);
        await fixture.Http.HeldCancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, fixture.Http.Active);
    }

    [Fact]
    public async Task CallerCancellationWinsDeadlineRace()
    {
        var clock = new CacheStatusClock(StartTime);
        using var fixture = new CacheFixture(clock);
        using var caller = new CancellationTokenSource();
        var inspection = fixture.Api.CheckCacheStatusAsync(
            new List<CachedAppInput> { new() { AppId = "HELD", Revision = "revision-1" } },
            caller.Token,
            StartTime.AddSeconds(10),
            2);

        await fixture.Http.HeldStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        caller.Cancel();
        clock.Advance(TimeSpan.FromSeconds(8));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => inspection);
        await fixture.Http.HeldCancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, fixture.Http.Active);
    }

    [Fact]
    public async Task ItemFailureDoesNotBlockLaterManualId()
    {
        var clock = new CacheStatusClock(StartTime);
        using var fixture = new CacheFixture(clock);

        var result = await fixture.Api.CheckCacheStatusAsync(
            new List<CachedAppInput>
            {
                new() { AppId = "FAIL", Revision = "revision-1" },
                new() { AppId = "MANUAL", Revision = "revision-1" }
            },
            expiresAtUtc: StartTime.AddMinutes(1),
            version: 2);

        Assert.Collection(
            result.Apps,
            failed =>
            {
                Assert.Equal("FAIL", failed.AppId);
                Assert.Equal(CacheOutcome.Unknown, failed.Outcome);
                Assert.Equal(CacheReason.InspectionFailed, failed.Reason);
            },
            manual =>
            {
                Assert.Equal("MANUAL", manual.AppId);
                Assert.Equal("MANUAL", manual.Name);
                Assert.True(manual.IsUpToDate);
                Assert.Equal(CacheOutcome.Current, manual.Outcome);
            });
        Assert.Null(result.Message);
    }

    [Fact]
    public async Task MissingManifestAndCacheEvidenceReturnTypedUnknownRows()
    {
        var clock = new CacheStatusClock(StartTime);
        using var fixture = new CacheFixture(clock);

        var result = await fixture.Api.CheckCacheStatusAsync(
            new List<CachedAppInput>
            {
                new() { AppId = "MISSING" },
                new() { AppId = "OWNED" }
            },
            expiresAtUtc: StartTime.AddMinutes(1),
            version: 2);

        Assert.Collection(
            result.Apps,
            missingManifest =>
            {
                Assert.Equal(CacheReason.ManifestUnavailable, missingManifest.Reason);
                Assert.Equal(CacheOutcome.Unknown, missingManifest.Outcome);
            },
            noCacheEvidence =>
            {
                Assert.Equal(CacheReason.NoCacheEvidence, noCacheEvidence.Reason);
                Assert.Equal(CacheOutcome.Unknown, noCacheEvidence.Outcome);
            });
        Assert.Null(result.Message);
    }

    [Fact]
    public void MultiSliceExpansionObservesCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        using var slices = ManifestHandler.BuildChunkRequests(
                "/filestreamingservice/files/test",
                "assets.test",
                5UL * ManifestHandler.SliceSizeBytes,
                cancellation.Token)
            .GetEnumerator();

        Assert.True(slices.MoveNext());
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => slices.MoveNext());
    }

    [Fact]
    public async Task FramedSocketWritesDeadlineRowsBeforeClientDisconnect()
    {
        const string socketSecretVariable = "PREFILL_SOCKET_SECRET";
        var originalSecret = Environment.GetEnvironmentVariable(socketSecretVariable);
        Environment.SetEnvironmentVariable(socketSecretVariable, null);
        var clock = new CacheStatusClock(StartTime);
        using var fixture = new CacheFixture(clock);
        var protocol = new PrefillProtocol(1, "1");
        var port = GetUnusedTcpPort();
        await using var commands = new SocketCommandInterface(
            port,
            protocol,
            (_, _) => Task.CompletedTask,
            clock);
        SetField(commands, "_api", fixture.Api);

        try
        {
            await commands.StartAsync();
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, port);
            var stream = client.GetStream();
            await WriteCommandAsync(
                stream,
                CreateCommand(
                    "[{\"appId\":\"OWNED\",\"revision\":\"revision-1\"}]",
                    version: "2",
                    expiresAtUtc: StartTime.AddSeconds(2).ToString("O")));

            using var response = await ReadFrameAsync(stream).WaitAsync(TimeSpan.FromSeconds(5));
            var root = response.RootElement;
            Assert.True(root.GetProperty("success").GetBoolean());
            var row = root.GetProperty("data").GetProperty("apps")[0];
            Assert.Equal("Unknown", row.GetProperty("outcome").GetString());
            Assert.Equal("DeadlineReached", row.GetProperty("reason").GetString());
            Assert.True(client.Connected);
        }
        finally
        {
            await commands.StopAsync();
            SetField(commands, "_api", null);
            Environment.SetEnvironmentVariable(socketSecretVariable, originalSecret);
        }
    }

    private static CommandRequest CreateCommand(
        string cachedApps,
        string? version = null,
        string? expiresAtUtc = null)
    {
        var parameters = new Dictionary<string, string> { ["cachedApps"] = cachedApps };
        if (version != null)
        {
            parameters["cacheStatusVersion"] = version;
        }

        if (expiresAtUtc != null)
        {
            parameters["expiresAtUtc"] = expiresAtUtc;
        }

        return new CommandRequest
        {
            Id = Guid.NewGuid().ToString("N"),
            Type = "check-cache-status",
            Parameters = parameters
        };
    }

    private static int GetUnusedTcpPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }

    private static async Task WriteCommandAsync(NetworkStream stream, CommandRequest request)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(
            request,
            DaemonSerializationContext.Default.CommandRequest);
        await stream.WriteAsync(BitConverter.GetBytes(bytes.Length));
        await stream.WriteAsync(bytes);
        await stream.FlushAsync();
    }

    private static async Task<JsonDocument> ReadFrameAsync(NetworkStream stream)
    {
        var length = new byte[4];
        await stream.ReadExactlyAsync(length);
        var bytes = new byte[BitConverter.ToInt32(length, 0)];
        await stream.ReadExactlyAsync(bytes);
        return JsonDocument.Parse(bytes);
    }

    private static void SetField(object target, string name, object? value)
    {
        typeof(XboxPrefillApi).Assembly
            .GetType(target.GetType().FullName!)!
            .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(target, value);
    }

    private sealed class CacheFixture : IDisposable
    {
        private readonly DirectoryInfo _directory;
        private readonly HttpClientFactory _clients;

        public CacheFixture(CacheStatusClock clock, bool cacheOwnedApp = false)
        {
            _directory = Directory.CreateTempSubdirectory("xbox-cache-status-");
            var successPath = Path.Combine(_directory.FullName, "success.json");
            var cacheRecords = new AppInfoHandler(
                successPath,
                (source, target) => File.Move(source, target, true));
            if (cacheOwnedApp)
            {
                cacheRecords.MarkDownloadAsSuccessful(new AppInfo
                {
                    AppId = "OWNED",
                    BuildVersion = "revision-1"
                });
            }

            Http = new CacheHttpState();
            var account = CreateAccountManager();
            _clients = new HttpClientFactory(
                AnsiConsole.Console,
                account,
                new CacheHttpHandler(Http),
                new CacheHttpHandler(Http));
            var xboxApi = new XboxApi(AnsiConsole.Console, _clients);
            var manifest = new ManifestHandler(AnsiConsole.Console, xboxApi);
            var manager = (XboxManager)RuntimeHelpers.GetUninitializedObject(typeof(XboxManager));
            SetField(manager, "_xboxApi", xboxApi);
            SetField(manager, "_manifestHandler", manifest);
            SetField(manager, "_appInfoHandler", cacheRecords);

            Api = new XboxPrefillApi(new SilentLogin(), clock: clock);
            SetField(Api, "_xboxManager", manager);
            SetField(Api, "_isInitialized", true);
        }

        public XboxPrefillApi Api { get; }
        public CacheHttpState Http { get; }

        public void Dispose()
        {
            _clients.Dispose();
            _directory.Delete(true);
        }

        private static XboxAccountManager CreateAccountManager()
        {
            var manager = (XboxAccountManager)Activator.CreateInstance(
                typeof(XboxAccountManager),
                BindingFlags.Instance | BindingFlags.NonPublic,
                binder: null,
                args: new object[] { AnsiConsole.Console, new SilentLogin() },
                culture: null)!;
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
    }

    private sealed class CacheHttpState
    {
        private int _active;
        private int _requests;

        public TaskCompletionSource HeldStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource HeldCancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Active => Volatile.Read(ref _active);
        public int Requests => Volatile.Read(ref _requests);

        public async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _requests);
            var uri = request.RequestUri!;
            if (uri.Query.Contains("bigIds=HELD", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref _active);
                HeldStarted.TrySetResult();
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    HeldCancelled.TrySetResult();
                    throw;
                }
                finally
                {
                    Interlocked.Decrement(ref _active);
                }
            }

            if (uri.Query.Contains("bigIds=FAIL", StringComparison.Ordinal))
            {
                throw new HttpRequestException("Injected catalog failure");
            }

            string json;
            if (uri.AbsolutePath.Contains("titlehistory", StringComparison.OrdinalIgnoreCase))
            {
                json = "{\"titles\":[{\"titleId\":\"1\",\"pfn\":\"Owned.Pfn\",\"name\":\"Owned App\",\"type\":\"Game\",\"productId\":\"OWNED\"}]}";
            }
            else if (uri.Query.Contains("bigIds=MISSING", StringComparison.Ordinal))
            {
                json = "{\"Products\":[]}";
            }
            else if (uri.Query.Contains("bigIds=", StringComparison.Ordinal))
            {
                var productId = ReadProductId(uri.Query);
                json = $"{{\"Products\":[{{\"ProductId\":\"{productId}\",\"DisplaySkuAvailabilities\":[{{\"Sku\":{{\"Properties\":{{\"Packages\":[{{\"ContentId\":\"content-{productId}\",\"PackageFormat\":\"xvc\"}}]}}}}}}]}}]}}";
            }
            else
            {
                var contentId = uri.AbsolutePath.Split('/').Last();
                json = $"{{\"PackageFound\":true,\"ContentId\":\"{contentId}\",\"VersionId\":\"1\",\"Version\":\"revision-1\",\"PackageFiles\":[{{\"FileName\":\"file.bin\",\"FileSize\":2097153,\"RelativeUrl\":\"/filestreamingservice/files/{contentId}\",\"CdnRootPaths\":[\"https://assets.test\"]}}]}}";
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json)
            };
        }

        private static string ReadProductId(string query)
        {
            const string key = "bigIds=";
            var start = query.IndexOf(key, StringComparison.Ordinal) + key.Length;
            var end = query.IndexOf('&', start);
            return Uri.UnescapeDataString(end < 0 ? query[start..] : query[start..end]);
        }
    }

    private sealed class CacheHttpHandler : HttpMessageHandler
    {
        private readonly CacheHttpState _http;

        public CacheHttpHandler(CacheHttpState http)
        {
            _http = http;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => _http.SendAsync(request, cancellationToken);
    }

    private sealed class SilentLogin : IXboxAuthProvider
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
}
