#nullable enable

using System.Text.Json;
using System.Threading;

namespace XboxPrefill.Api;

/// <summary>
/// Command interface that uses Unix Domain Socket or TCP for IPC.
/// Handles all socket commands for the Xbox prefill daemon.
/// </summary>
public sealed class SocketCommandInterface : IAsyncDisposable
{
    private readonly SocketServer _socketServer;
    private readonly SocketAuthProvider _authProvider;
    private readonly SocketProgress _progress;
    private readonly CancellationTokenSource _cts = new();
    private readonly OwnedOperationCoordinator _prefillOperation;
    private readonly PrefillProtocol _protocol;
    private readonly Func<PrefillRun, CancellationToken, Task>? _execute;
    private readonly RequestBudget _budget;
    private readonly ItemClaims _claims = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, PrefillRun> _runs = new();
    private volatile bool _authLost;
    private CancellationTokenSource? _loginCts;
    private XboxPrefillApi? _api;
    private Task? _loginTask;
    private volatile bool _isLoggedIn;
    private bool _isLoggingIn;
    private bool _disposed;

    // Bumped by logout (and cancel-login) so a login task that is still unwinding (or already
    // orphaned by a cancellation that never got observed) can tell it has been superseded and must
    // not resurrect _isLoggedIn/_api for whatever now owns them. Written from the socket command
    // loop, read from thread-pool login-task continuations after an await - always accessed via
    // Interlocked, never a plain read/increment.
    private long _loginGeneration;

    // How long logout waits for an in-flight login task to unwind before force-cleaning up
    // anyway. Logout must never hang on a stuck login.
    private static readonly TimeSpan LogoutLoginTaskTimeout = TimeSpan.FromSeconds(8);

    private static readonly HashSet<string> PreLoginCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "login",
        "logout",
        "status",
        "cancel-login",
        "provide-credential",
        "provide-auto-login"
        , "get-operation", "cancel-prefill"
    };

    public SocketCommandInterface(string socketPath)
    {
        _protocol = PrefillProtocol.FromEnvironment(AppConfig.MaxConcurrentRequests);
        _prefillOperation = new OwnedOperationCoordinator(_protocol.MaxConcurrentRuns);
        _budget = new RequestBudget(_protocol.MaxConcurrentRequests);
        _progress = new SocketProgress();
        _socketServer = new SocketServer(socketPath, _progress);
        _authProvider = new SocketAuthProvider(_socketServer, _progress);
        _socketServer.OnCommand = HandleCommandAsync;
        _socketServer.CommandLaneSelector = SelectCommandLane;

        _progress.SocketServer = _socketServer;
    }

    public SocketCommandInterface(int tcpPort)
        : this(tcpPort, PrefillProtocol.FromEnvironment(AppConfig.MaxConcurrentRequests), null)
    {
    }

    internal SocketCommandInterface(int tcpPort, PrefillProtocol protocol, Func<PrefillRun, CancellationToken, Task>? execute)
    {
        _protocol = protocol;
        _execute = execute;
        _isLoggedIn = execute != null;
        _prefillOperation = new OwnedOperationCoordinator(_protocol.MaxConcurrentRuns);
        _budget = new RequestBudget(_protocol.MaxConcurrentRequests);
        _progress = new SocketProgress();
        _socketServer = new SocketServer(tcpPort, _progress);
        _authProvider = new SocketAuthProvider(_socketServer, _progress);
        _socketServer.OnCommand = HandleCommandAsync;
        _socketServer.CommandLaneSelector = SelectCommandLane;

        _progress.SocketServer = _socketServer;
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        _progress.OnLog(LogLevel.Info, "Starting socket command interface...");

        await _socketServer.StartAsync(cancellationToken);

        await BroadcastStatusAsync("awaiting-login", "Login required before other commands can be executed");

        _progress.OnLog(LogLevel.Info, "Socket command interface started - awaiting login");
    }

    public async Task StopAsync()
    {
        await _cts.CancelAsync();
        await _prefillOperation.CancelAllAndWaitAsync();
        await _socketServer.StopAsync();
        _progress.OnLog(LogLevel.Info, "Socket command interface stopped");
    }

    internal async Task<CommandResponse> HandleCommandAsync(CommandRequest request, CancellationToken cancellationToken)
    {
        _progress.OnLog(LogLevel.Debug, $"Processing command: {request.Type} (ID: {request.Id})");

        if (!_isLoggedIn && !PreLoginCommands.Contains(request.Type))
        {
            return new CommandResponse
            {
                Id = request.Id,
                Success = false,
                Error = "Authentication required. Please login first.",
                RequiresLogin = true,
                CompletedAt = DateTime.UtcNow
            };
        }

        try
        {
            return request.Type.ToLowerInvariant() switch
            {
                "login" => await HandleLoginAsync(request, cancellationToken),
                "logout" => await HandleLogoutAsync(request, cancellationToken),
                "cancel-login" => await HandleCancelLoginAsync(request),
                "cancel-prefill" => await HandleCancelPrefillAsync(request, cancellationToken),
                "provide-credential" => HandleProvideCredential(request),
                "provide-auto-login" => await HandleProvideAutoLoginAsync(request, cancellationToken),
                "status" => HandleStatus(request),
                "get-operation" => HandleGetOperation(request),
                "get-owned-games" => await HandleGetOwnedGamesAsync(request, cancellationToken),
                "get-cdn-info" => await HandleGetCdnInfoAsync(request, cancellationToken),
                "get-selected-apps" => HandleGetSelectedApps(request),
                "set-selected-apps" => HandleSetSelectedApps(request),
                "get-selected-apps-status" => await HandleGetSelectedAppsStatusAsync(request, cancellationToken),
                "prefill" => await HandlePrefillAsync(request, cancellationToken),
                "clear-cache" => HandleClearCache(request),
                "get-cache-info" => HandleGetCacheInfo(request),
                "check-cache-status" => await HandleCheckCacheStatusAsync(request, cancellationToken),
                "shutdown" => await HandleShutdownAsync(request, cancellationToken),
                _ => new CommandResponse
                {
                    Id = request.Id,
                    Success = false,
                    Error = $"Unknown command type: {request.Type}",
                    CompletedAt = DateTime.UtcNow
                }
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _progress.OnLog(LogLevel.Error, $"Error handling command {request.Type}: {ex.Message}");
            return new CommandResponse
            {
                Id = request.Id,
                Success = false,
                Error = ex.Message,
                CompletedAt = DateTime.UtcNow
            };
        }
    }

    private static DaemonCommandLane SelectCommandLane(CommandRequest request)
        => request.Type.ToLowerInvariant() switch
        {
            "cancel-login" or "cancel-prefill" or "status" or "get-operation" or "shutdown"
                => DaemonCommandLane.Control,
            "get-owned-games" or "get-cdn-info" or "get-selected-apps" or
            "get-selected-apps-status" or "get-cache-info" or "check-cache-status"
                => DaemonCommandLane.Concurrent,
            _ => DaemonCommandLane.Serialized
        };

    private Task<CommandResponse> HandleLoginAsync(CommandRequest request, CancellationToken cancellationToken)
    {
        if (_isLoggedIn)
        {
            _progress.OnLog(LogLevel.Info, "Already logged in");
            return Task.FromResult(new CommandResponse
            {
                Id = request.Id,
                Success = true,
                Message = "Already logged in",
                CompletedAt = DateTime.UtcNow
            });
        }

        if (_isLoggingIn)
        {
            _progress.OnLog(LogLevel.Info, "Login already in progress");
            return Task.FromResult(new CommandResponse
            {
                Id = request.Id,
                Success = true,
                Message = "Login already in progress",
                CompletedAt = DateTime.UtcNow
            });
        }

        if (_prefillOperation.IsRunning)
        {
            return Task.FromResult(new CommandResponse { Id = request.Id, Error = "Active runs are still draining." });
        }
        if (_api != null) CleanupApiInstance();
        _progress.OnLog(LogLevel.Info, "Starting secure login process via socket...");
        _isLoggingIn = true;

        _loginCts?.Dispose();
        _loginCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        var loginCts = _loginCts;

        // Captured now: if logout runs before this task settles, it bumps _loginGeneration so a
        // late-completing (superseded) task can tell and must not touch shared login state.
        var loginGeneration = Interlocked.Increment(ref _loginGeneration);

        var api = new XboxPrefillApi(_authProvider, _progress, _budget, _protocol.MaxConcurrentRequests);
        _api = api;

        _loginTask = Task.Run(async () =>
        {
            try
            {
                await api.InitializeAsync(loginCts.Token);

                if (loginGeneration != Interlocked.Read(ref _loginGeneration))
                {
                    _progress.OnLog(LogLevel.Info, "Login superseded by logout - discarding orphaned session");
                    DisposeOrphanedApi(api);
                    return;
                }

                _isLoggedIn = true;
                _authLost = false;
                _isLoggingIn = false;
                _progress.OnLog(LogLevel.Info, "Login successful - commands now available");

                await BroadcastStatusAsync("logged-in", "Authenticated and ready for commands", api.DisplayName);
            }
            catch (OperationCanceledException)
            {
                _progress.OnLog(LogLevel.Info, "Login cancelled");
                if (loginGeneration != Interlocked.Read(ref _loginGeneration))
                {
                    DisposeOrphanedApi(api);
                    return;
                }
                _isLoggingIn = false;
                CleanupApiInstance();
                await BroadcastStatusAsync("awaiting-login", "Login cancelled - ready for new attempt");
            }
            catch (Exception ex)
            {
                _progress.OnLog(LogLevel.Error, $"Login failed: {ex.Message}");
                if (loginGeneration != Interlocked.Read(ref _loginGeneration))
                {
                    DisposeOrphanedApi(api);
                    return;
                }
                _isLoggingIn = false;
                CleanupApiInstance();
                await BroadcastStatusAsync("awaiting-login", $"Login failed: {ex.Message}");
            }
            finally
            {
                if (loginGeneration == Interlocked.Read(ref _loginGeneration))
                {
                    _loginCts?.Dispose();
                    _loginCts = null;
                }
            }
        }, loginCts.Token);

        return Task.FromResult(new CommandResponse
        {
            Id = request.Id,
            Success = true,
            Message = "Login started - awaiting credentials",
            CompletedAt = DateTime.UtcNow
        });
    }

    private async Task<CommandResponse> HandleLogoutAsync(
        CommandRequest request,
        CancellationToken cancellationToken)
    {
        // Bump the generation so any login task still unwinding (or one that never observes
        // the cancellation below) cannot resurrect _isLoggedIn/_api once it finally settles.
        Interlocked.Increment(ref _loginGeneration);

        // Logout while a login is in progress: cancel it the same way cancel-login does, then
        // fall through to the same cleanup + credential wipe below (cancel-then-forget). Xbox
        // previously had no such logic (unlike steam/epic) - this was the "weakest" daemon per the
        // complete-forget audit.
        if (_isLoggingIn)
        {
            _authProvider.CancelPendingRequest();

            try
            {
                if (_loginCts != null) await _loginCts.CancelAsync();
            }
            catch (Exception ex) { _progress.OnLog(LogLevel.Debug, $"Error cancelling login CTS: {ex.Message}"); }

            // Bounded wait for the login task to unwind. A stuck login must never hang logout -
            // if it doesn't finish in time we force-cleanup below anyway; the generation bump
            // above keeps a late finish from resurrecting state.
            var loginTask = _loginTask;
            if (loginTask != null)
            {
                await Task.WhenAny(loginTask, Task.Delay(LogoutLoginTaskTimeout, CancellationToken.None));
            }
        }

        await _prefillOperation.CancelAllAndWaitAsync(cancellationToken);
        CleanupApiInstance();

        // Wipe the persisted account file AND its storage.key so the refresh token cannot linger
        // (or be silently re-decrypted by a later login re-using the same key) after logout.
        // Session 20260703-221336-2070027597 (RC6): logout previously deleted only the account
        // file, leaving storage.key on disk - any subsequent login re-persisted a token the SAME
        // key could still decrypt, making the erase-on-stop/clear-logins policy incomplete.
        EraseAccountStore(_progress);

        _progress.OnLog(LogLevel.Info, "Logged out");

        return new CommandResponse
        {
            Id = request.Id,
            Success = true,
            Message = "Logged out successfully",
            CompletedAt = DateTime.UtcNow
        };
    }

    private async Task<CommandResponse> HandleCancelLoginAsync(CommandRequest request)
    {
        if (!_isLoggingIn)
            return new CommandResponse { Id = request.Id, Success = true, Message = "No login in progress" };
        _progress.OnLog(LogLevel.Info, "Cancelling login...");

        // Bump the generation first, same as logout: a login task that races past this
        // cancellation (or whose exception is swallowed) must not be able to resurrect
        // _isLoggedIn/_api once it finally settles, even though this handler isn't a logout.
        Interlocked.Increment(ref _loginGeneration);

        _authProvider.CancelPendingRequest();

        try
        {
            if (_loginCts != null) await _loginCts.CancelAsync();
        }
        catch (Exception ex) { _progress.OnLog(LogLevel.Debug, $"Error cancelling login CTS: {ex.Message}"); }

        CleanupApiInstance();
        await BroadcastStatusAsync("awaiting-login", "Login cancelled - ready for new attempt");

        return new CommandResponse
        {
            Id = request.Id,
            Success = true,
            Message = "Login cancelled",
            CompletedAt = DateTime.UtcNow
        };
    }

    private async Task<CommandResponse> HandleCancelPrefillAsync(
        CommandRequest request,
        CancellationToken cancellationToken)
    {
        if (request.Parameters?.TryGetValue("operationId", out var operationId) == true)
        {
            var instance = request.Parameters.GetValueOrDefault("daemonInstanceId") ?? string.Empty;
            _protocol.ValidateInstance(instance);
            var snapshot = _prefillOperation.Cancel(operationId, instance);
            return new CommandResponse
            {
                Id = request.Id,
                Success = snapshot != null,
                Data = snapshot,
                Error = snapshot == null ? "operation-not-found" : null
            };
        }

        if (!_prefillOperation.IsRunning)
        {
            return new CommandResponse
            {
                Id = request.Id,
                Success = true,
                Message = "No prefill in progress",
                CompletedAt = DateTime.UtcNow
            };
        }

        _progress.OnLog(LogLevel.Info, "Cancelling prefill...");
        await _prefillOperation.CancelAndWaitAsync(cancellationToken);

        return new CommandResponse
        {
            Id = request.Id,
            Success = true,
            Message = "Prefill cancelled",
            CompletedAt = DateTime.UtcNow
        };
    }

    private CommandResponse HandleProvideCredential(CommandRequest request)
    {
        var challengeId = request.Parameters?.GetValueOrDefault("challengeId");
        var clientPublicKey = request.Parameters?.GetValueOrDefault("clientPublicKey");
        var encryptedCredential = request.Parameters?.GetValueOrDefault("encryptedCredential");
        var nonce = request.Parameters?.GetValueOrDefault("nonce");
        var tag = request.Parameters?.GetValueOrDefault("tag");

        if (string.IsNullOrEmpty(challengeId) || string.IsNullOrEmpty(clientPublicKey) ||
            string.IsNullOrEmpty(encryptedCredential) || string.IsNullOrEmpty(nonce) || string.IsNullOrEmpty(tag))
        {
            return new CommandResponse
            {
                Id = request.Id,
                Success = false,
                Error = "Missing required credential parameters",
                CompletedAt = DateTime.UtcNow
            };
        }

        var response = new EncryptedCredentialResponse
        {
            ChallengeId = challengeId,
            ClientPublicKey = clientPublicKey,
            EncryptedCredential = encryptedCredential,
            Nonce = nonce,
            Tag = tag
        };

        _authProvider.ReceiveCredential(response);

        return new CommandResponse
        {
            Id = request.Id,
            Success = true,
            Message = "Credential received",
            CompletedAt = DateTime.UtcNow
        };
    }

    private async Task<CommandResponse> HandleProvideAutoLoginAsync(CommandRequest request, CancellationToken cancellationToken)
    {
        if (_isLoggedIn)
        {
            return new CommandResponse
            {
                Id = request.Id,
                Success = true,
                Message = "Already logged in",
                CompletedAt = DateTime.UtcNow
            };
        }

        var challengeId = request.Parameters?.GetValueOrDefault("challengeId");
        var clientPublicKey = request.Parameters?.GetValueOrDefault("clientPublicKey");
        var encryptedCredential = request.Parameters?.GetValueOrDefault("encryptedCredential");
        var nonce = request.Parameters?.GetValueOrDefault("nonce");
        var tag = request.Parameters?.GetValueOrDefault("tag");

        // Step 1: no encrypted payload yet — issue a challenge over the existing secure channel and return
        // the server public key so the caller can encrypt the refresh token + device key against it.
        if (string.IsNullOrEmpty(encryptedCredential))
        {
            var challenge = await _authProvider.IssueAutoLoginChallengeAsync(cancellationToken);
            return new CommandResponse
            {
                Id = request.Id,
                Success = true,
                Message = "Auto-login challenge issued - resend provide-auto-login with the encrypted payload",
                Data = challenge,
                CompletedAt = DateTime.UtcNow
            };
        }

        // Step 2: decrypt the supplied payload using the same ECDH + AES-GCM exchange as interactive auth.
        if (string.IsNullOrEmpty(challengeId) || string.IsNullOrEmpty(clientPublicKey) ||
            string.IsNullOrEmpty(nonce) || string.IsNullOrEmpty(tag))
        {
            return new CommandResponse
            {
                Id = request.Id,
                Success = false,
                Error = "Missing required encrypted auto-login parameters",
                CompletedAt = DateTime.UtcNow
            };
        }

        var encResponse = new EncryptedCredentialResponse
        {
            ChallengeId = challengeId,
            ClientPublicKey = clientPublicKey,
            EncryptedCredential = encryptedCredential,
            Nonce = nonce,
            Tag = tag
        };

        var plaintext = _authProvider.DecryptAutoLoginPayload(encResponse);
        if (string.IsNullOrEmpty(plaintext))
        {
            return new CommandResponse
            {
                Id = request.Id,
                Success = false,
                Error = "Failed to decrypt auto-login payload (expired or invalid challenge)",
                CompletedAt = DateTime.UtcNow
            };
        }

        AutoLoginPayload? payload;
        try
        {
            payload = JsonSerializer.Deserialize(plaintext, DaemonSerializationContext.Default.AutoLoginPayload);
        }
        catch (JsonException ex)
        {
            return new CommandResponse
            {
                Id = request.Id,
                Success = false,
                Error = $"Malformed auto-login payload: {ex.Message}",
                CompletedAt = DateTime.UtcNow
            };
        }

        if (payload == null || string.IsNullOrEmpty(payload.RefreshToken))
        {
            return new CommandResponse
            {
                Id = request.Id,
                Success = false,
                Error = "Auto-login payload missing refreshToken",
                CompletedAt = DateTime.UtcNow
            };
        }

        if (_isLoggingIn)
        {
            return new CommandResponse
            {
                Id = request.Id,
                Success = false,
                Error = "A login is already in progress",
                CompletedAt = DateTime.UtcNow
            };
        }

        if (_prefillOperation.IsRunning)
        {
            return new CommandResponse { Id = request.Id, Error = "Active runs are still draining." };
        }
        if (_api != null) CleanupApiInstance();
        _progress.OnLog(LogLevel.Info, "Starting non-interactive auto-login from imported credentials...");
        _isLoggingIn = true;

        _loginCts?.Dispose();
        _loginCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        var loginCts = _loginCts;
        var loginGeneration = Interlocked.Increment(ref _loginGeneration);

        var api = new XboxPrefillApi(_authProvider, _progress, _budget, _protocol.MaxConcurrentRequests);
        _api = api;

        var refreshToken = payload.RefreshToken;
        var deviceKey = payload.DeviceKeyPkcs8;

        _loginTask = Task.Run(async () =>
        {
            try
            {
                await api.InitializeWithImportAsync(refreshToken, deviceKey, loginCts.Token);

                if (loginGeneration != Interlocked.Read(ref _loginGeneration))
                {
                    _progress.OnLog(LogLevel.Info, "Auto-login superseded by logout - discarding orphaned session");
                    DisposeOrphanedApi(api);
                    return;
                }

                _isLoggedIn = true;
                _authLost = false;
                _isLoggingIn = false;
                _progress.OnLog(LogLevel.Info, "Auto-login successful - commands now available");

                await BroadcastStatusAsync("logged-in", "Authenticated and ready for commands", api.DisplayName);
            }
            catch (OperationCanceledException)
            {
                _progress.OnLog(LogLevel.Info, "Auto-login cancelled");
                if (loginGeneration != Interlocked.Read(ref _loginGeneration))
                {
                    DisposeOrphanedApi(api);
                    return;
                }
                _isLoggingIn = false;
                CleanupApiInstance();
                await BroadcastStatusAsync("awaiting-login", "Auto-login cancelled - ready for new attempt");
            }
            catch (Exception ex)
            {
                _progress.OnLog(LogLevel.Error, $"Auto-login failed: {ex.Message}");
                if (loginGeneration != Interlocked.Read(ref _loginGeneration))
                {
                    DisposeOrphanedApi(api);
                    return;
                }
                _isLoggingIn = false;
                CleanupApiInstance();
                await BroadcastStatusAsync("awaiting-login", $"Auto-login failed: {ex.Message}");
            }
            finally
            {
                if (loginGeneration == Interlocked.Read(ref _loginGeneration))
                {
                    _loginCts?.Dispose();
                    _loginCts = null;
                }
            }
        }, loginCts.Token);

        return new CommandResponse
        {
            Id = request.Id,
            Success = true,
            Message = "Auto-login started",
            CompletedAt = DateTime.UtcNow
        };
    }

    private CommandResponse HandleStatus(CommandRequest request)
    {
        return new CommandResponse
        {
            Id = request.Id,
            Success = true,
            Data = new StatusData
            {
                IsLoggedIn = _isLoggedIn,
                IsInitialized = _api?.IsInitialized ?? false,
                IsPrefilling = _prefillOperation.IsRunning,
                ProtocolVersion = PrefillProtocol.Version,
                Features = PrefillProtocol.Features,
                DaemonInstanceId = _protocol.DaemonInstanceId,
                MaxConcurrentRuns = _protocol.MaxConcurrentRuns,
                MaxConcurrentRequests = _protocol.MaxConcurrentRequests,
                ActiveOperations = _prefillOperation.GetActiveOperations(),
                RecentOperations = _prefillOperation.GetRecentOperations(),
                // The real login bound is the MSA refresh token (~90d sliding). It is stamped on every
                // login/refresh as (issued time + 90d) and surfaced here; the short XSTS expiry is surfaced
                // separately via XstsExpiryUtc while logged in.
                AuthExpiryUtc = _isLoggedIn ? _api?.AuthExpiryUtc : null,
                XstsExpiryUtc = _isLoggedIn ? _api?.XstsExpiryUtc : null,
                AccountDisplayName = _isLoggedIn ? _api?.DisplayName : null
            },
            CompletedAt = DateTime.UtcNow
        };
    }

    private async Task<CommandResponse> HandleGetOwnedGamesAsync(CommandRequest request, CancellationToken cancellationToken)
    {
        EnsureLoggedIn();
        var games = await _api!.GetOwnedGamesAsync(cancellationToken);

        return new CommandResponse
        {
            Id = request.Id,
            Success = true,
            Data = games,
            CompletedAt = DateTime.UtcNow
        };
    }

    private async Task<CommandResponse> HandleGetCdnInfoAsync(CommandRequest request, CancellationToken cancellationToken)
    {
        EnsureLoggedIn();

        // Optional: filter by specific appIds
        List<string>? appIds = null;
        if (request.Parameters?.TryGetValue("appIds", out var appIdsJson) == true && !string.IsNullOrEmpty(appIdsJson))
        {
            appIds = JsonSerializer.Deserialize(appIdsJson, DaemonSerializationContext.Default.ListString);
        }

        var result = await _api!.GetCdnInfoAsync(appIds, cancellationToken);
        return new CommandResponse
        {
            Id = request.Id,
            Success = true,
            Data = result,
            CompletedAt = DateTime.UtcNow
        };
    }

    private CommandResponse HandleGetSelectedApps(CommandRequest request)
    {
        EnsureLoggedIn();
        var selected = _api!.GetSelectedApps();

        return new CommandResponse
        {
            Id = request.Id,
            Success = true,
            Data = selected,
            CompletedAt = DateTime.UtcNow
        };
    }

    private CommandResponse HandleSetSelectedApps(CommandRequest request)
    {
        EnsureLoggedIn();

        var appIdsJson = request.Parameters?.GetValueOrDefault("appIds");
        if (string.IsNullOrEmpty(appIdsJson))
        {
            return new CommandResponse
            {
                Id = request.Id,
                Success = false,
                Error = "appIds parameter required",
                CompletedAt = DateTime.UtcNow
            };
        }

        var appIds = JsonSerializer.Deserialize(appIdsJson, DaemonSerializationContext.Default.ListString);
        if (appIds == null)
        {
            return new CommandResponse
            {
                Id = request.Id,
                Success = false,
                Error = "appIds must be a JSON array",
                CompletedAt = DateTime.UtcNow
            };
        }

        // An empty array is a valid request: it clears the current selection.
        _api!.SetSelectedApps(appIds);
        _progress.OnLog(LogLevel.Info, $"Set {appIds.Count} selected apps");

        return new CommandResponse
        {
            Id = request.Id,
            Success = true,
            Message = "Apps selected",
            CompletedAt = DateTime.UtcNow
        };
    }

    private async Task<CommandResponse> HandleGetSelectedAppsStatusAsync(CommandRequest request, CancellationToken cancellationToken)
    {
        EnsureLoggedIn();

        List<string>? operatingSystems = null;
        var osParam = request.Parameters?.GetValueOrDefault("os");
        if (!string.IsNullOrEmpty(osParam))
        {
            operatingSystems = osParam.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        }

        var status = await _api!.GetSelectedAppsStatusAsync(operatingSystems, cancellationToken);

        return new CommandResponse
        {
            Id = request.Id,
            Success = true,
            Data = status,
            Message = status.Message,
            CompletedAt = DateTime.UtcNow
        };
    }

    private async Task<CommandResponse> HandlePrefillAsync(
        CommandRequest request,
        CancellationToken cancellationToken)
    {
        EnsureLoggedIn();

        if (request.Parameters?.TryGetValue("protocolVersion", out var version) == true && version != "2")
            return new CommandResponse { Id = request.Id, Error = "Unsupported protocol version." };

        if (request.Parameters?.GetValueOrDefault("protocolVersion") == "2")
        {
            var captured = PrefillRun.Capture(request, _protocol);
            var api = _api!;
            var run = new PrefillRun(request.Id, _protocol, captured, _budget, _claims, _progress,
                (snapshot, token) => _socketServer.BroadcastProgressAsync(new ProgressEvent(ToProgress(snapshot)), token));
            var admission = await _prefillOperation.StartAsync(request.Id, PrefillProtocol.Fingerprint(captured), run.Progress,
                async token =>
                {
                    _runs.TryAdd(run.OperationId, run);
                    try
                    {
                        if (_authLost) throw new XboxLoginException("Xbox re-login required.");
                        if (_execute != null) await _execute(run, token);
                        else await api.PrefillAsync(run, token);
                    }
                    catch (XboxLoginException)
                    {
                        _authLost = true;
                        _isLoggedIn = false;
                        foreach (var active in _runs.Values)
                        {
                            active.TryChooseTerminal("failed", "auth-lost");
                            _prefillOperation.Cancel(active.OperationId, _protocol.DaemonInstanceId);
                        }
                        throw;
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested)
                    {
                        run.TryChooseTerminal("cancelled");
                        throw;
                    }
                    catch (Exception ex)
                    {
                        _progress.OnLog(LogLevel.Error, $"Prefill failed: {ex.Message}");
                        var reason = ex switch
                        {
                            LancacheNotFoundException => "lancache-not-found",
                            TimeoutException => "download-timeout",
                            IOException => "cache-write-failed",
                            _ => "download-failed"
                        };
                        run.TryChooseTerminal("failed", reason);
                        throw;
                    }
                    finally
                    {
                        await run.CompleteAsync();
                        _runs.TryRemove(run.OperationId, out _);
                    }
                }, _cts.Token);
            return new CommandResponse
            {
                Id = request.Id,
                Success = admission.Accepted || admission.Replayed,
                Error = admission.Error,
                Data = admission.Operation == null ? null : new PrefillStart
                {
                    RunId = request.Id,
                    DaemonInstanceId = _protocol.DaemonInstanceId,
                    State = admission.Replayed ? admission.Operation.State : "started"
                }
            };
        }

        if (_prefillOperation.IsRunning)
        {
            return new CommandResponse
            {
                Id = request.Id,
                Success = false,
                Error = "A prefill is already in progress",
                CompletedAt = DateTime.UtcNow
            };
        }

        var options = new PrefillOptions();

        if (request.Parameters != null)
        {
            if (bool.TryParse(request.Parameters.GetValueOrDefault("all"), out var all))
                options.DownloadAllOwnedGames = all;
            if (bool.TryParse(request.Parameters.GetValueOrDefault("force"), out var force))
                options.Force = force;
            if (bool.TryParse(request.Parameters.GetValueOrDefault("recent"), out var recent))
                options.Recent = recent;
            if (bool.TryParse(request.Parameters.GetValueOrDefault("top"), out var top))
                options.Top = top;

            // Optional explicit Store ProductIds (manual entry); these flow through to manualIds and may be IDs
            // that aren't present in the titlehub-owned library.
            var productIdsJson = request.Parameters.GetValueOrDefault("productIds");
            if (!string.IsNullOrEmpty(productIdsJson))
            {
                var productIds = JsonSerializer.Deserialize(productIdsJson, DaemonSerializationContext.Default.ListString);
                if (productIds != null && productIds.Count > 0)
                {
                    options.ProductIds = productIds;
                }
            }
        }

        await _prefillOperation.StartAsync(async operationToken =>
        {
            try
            {
                var result = await _api!.PrefillAsync(options, operationToken);
                operationToken.ThrowIfCancellationRequested();

                if (result.Success)
                {
                    _progress.OnLog(LogLevel.Info, "Prefill completed successfully");
                }
                else
                {
                    _progress.OnLog(LogLevel.Warning, $"Prefill completed with errors: {result.ErrorMessage}");
                    throw new InvalidOperationException(result.ErrorMessage ?? "Prefill failed");
                }
            }
            catch (OperationCanceledException) when (operationToken.IsCancellationRequested)
            {
                _progress.OnCancelled("Prefill cancelled by user");
                throw;
            }
        }, _cts.Token);

        return new CommandResponse
        {
            Id = request.Id,
            Success = true,
            Message = "Prefill started",
            CompletedAt = DateTime.UtcNow
        };
    }

    private CommandResponse HandleGetOperation(CommandRequest request)
    {
        var parameters = request.Parameters ?? throw new ArgumentException("Missing operation parameters.", nameof(request));
        _protocol.ValidateInstance(parameters.GetValueOrDefault("daemonInstanceId") ?? string.Empty);
        var operationId = parameters.GetValueOrDefault("operationId") ?? string.Empty;
        var offset = parameters.TryGetValue("offset", out var start) ? int.Parse(start, System.Globalization.CultureInfo.InvariantCulture) : 0;
        var limit = parameters.TryGetValue("limit", out var count) ? int.Parse(count, System.Globalization.CultureInfo.InvariantCulture) : 100;
        var operation = _prefillOperation.GetOperation(operationId, offset, limit);
        return new CommandResponse
        {
            Id = request.Id,
            Success = operation != null,
            Data = operation,
            Error = operation == null ? "operation-not-found" : null
        };
    }

    private static PrefillProgressUpdate ToProgress(RunSnapshot snapshot) => new()
    {
        OperationId = snapshot.OperationId,
        DaemonInstanceId = snapshot.DaemonInstanceId,
        Sequence = snapshot.Sequence,
        State = snapshot.State,
        Reason = snapshot.Reason ?? snapshot.CurrentItem?.Reason,
        StartedAt = snapshot.StartedAt,
        UpdatedAt = snapshot.UpdatedAt.UtcDateTime,
        CurrentAppId = snapshot.CurrentItem?.AppId,
        CurrentAppName = snapshot.CurrentItem?.Name,
        TotalBytes = snapshot.CurrentItem?.TotalBytes ?? 0,
        BytesDownloaded = snapshot.CurrentItem?.BytesTransferred ?? 0,
        PercentComplete = snapshot.CurrentItem?.TotalBytes > 0
            ? Math.Min(100, 100.0 * snapshot.CurrentItem.BytesTransferred / snapshot.CurrentItem.TotalBytes.Value) : 0,
        TotalTime = snapshot.UpdatedAt - snapshot.StartedAt,
        Result = snapshot.CurrentItem?.Result,
        TotalApps = snapshot.TotalApps,
        UpdatedApps = snapshot.CompletedApps,
        AlreadyUpToDate = snapshot.CachedApps,
        FailedApps = snapshot.FailedApps,
        SkippedApps = snapshot.SkippedApps,
        CancelledApps = snapshot.CancelledApps,
        TotalBytesTransferred = snapshot.BytesTransferred,
        CurrentItem = snapshot.CurrentItem
    };

    private CommandResponse HandleClearCache(CommandRequest request)
    {
        if (_prefillOperation.IsRunning) return new CommandResponse { Id = request.Id, Error = "A prefill is in progress" };
        var result = XboxPrefillApi.ClearCache();

        return new CommandResponse
        {
            Id = request.Id,
            Success = result.Success,
            Data = result,
            Message = result.Message,
            CompletedAt = DateTime.UtcNow
        };
    }

    private CommandResponse HandleGetCacheInfo(CommandRequest request)
    {
        var info = XboxPrefillApi.GetCacheInfo();

        return new CommandResponse
        {
            Id = request.Id,
            Success = info.Success,
            Data = info,
            Message = info.Message,
            CompletedAt = DateTime.UtcNow
        };
    }

    private async Task<CommandResponse> HandleCheckCacheStatusAsync(CommandRequest request, CancellationToken cancellationToken)
    {
        EnsureLoggedIn();

        // Accept app IDs as a JSON string list in "appIds" parameter
        List<string> appIds;
        var appIdsJson = request.Parameters?.GetValueOrDefault("appIds");
        if (!string.IsNullOrEmpty(appIdsJson))
        {
            appIds = JsonSerializer.Deserialize(appIdsJson, DaemonSerializationContext.Default.ListString) ?? new List<string>();
        }
        else
        {
            // No app IDs provided
            return new CommandResponse
            {
                Id = request.Id,
                Success = true,
                Data = new CacheStatusResult { Apps = new List<AppCacheStatus>(), Message = "No app IDs provided" },
                Message = "No app IDs provided",
                CompletedAt = DateTime.UtcNow
            };
        }

        var status = await _api!.CheckCacheStatusAsync(appIds, cancellationToken);

        return new CommandResponse
        {
            Id = request.Id,
            Success = true,
            Data = status,
            Message = status.Message,
            CompletedAt = DateTime.UtcNow
        };
    }

    private async Task<CommandResponse> HandleShutdownAsync(
        CommandRequest request,
        CancellationToken cancellationToken)
    {
        _isLoggedIn = false;
        await _cts.CancelAsync();
        await _prefillOperation.CancelAllAndWaitAsync(cancellationToken);
        CleanupApiInstance();

        return new CommandResponse
        {
            Id = request.Id,
            Success = true,
            Message = "Shutdown complete",
            CompletedAt = DateTime.UtcNow
        };
    }

    private void EnsureLoggedIn()
    {
        if (_authLost || !_isLoggedIn || (_execute == null && (_api == null || !_api.IsInitialized)))
            throw new InvalidOperationException("Not logged in. Please login first.");
    }

    private void CleanupApiInstance()
    {
        try
        {
            _api?.Shutdown();
            _api?.Dispose();
        }
        catch (Exception ex) { _progress.OnLog(LogLevel.Debug, $"Error during API cleanup: {ex.Message}"); }
        _api = null;
        _isLoggedIn = false;
        _isLoggingIn = false;
    }

    /// <summary>
    /// Tears down an api instance that lost the generation race (superseded by a logout) without
    /// touching any of the shared fields, since a newer login/logout cycle may already own them.
    /// Also erases the account store: session 20260703-221336-2070027597 (RC6) confirmed that
    /// <see cref="Handlers.XboxAccountManager.LoginAsync"/>/<c>ImportAndLoginAsync</c> call Save()
    /// BEFORE this generation check runs, so an orphaned login that raced past a logout can still
    /// persist fresh tokens to disk. Erasing here closes that resurrection window; the erase is
    /// idempotent (both files may already be gone from the logout that superseded this task).
    /// </summary>
    private static void DisposeOrphanedApi(XboxPrefillApi api)
    {
        try
        {
            api.Shutdown();
            api.Dispose();
        }
        catch { /* ignore cleanup errors for a discarded orphan */ }

        EraseAccountStore();
    }

    /// <summary>
    /// Deletes the persisted account file and its storage.key. Shared by <see cref="HandleLogoutAsync"/>
    /// (explicit logout) and <see cref="DisposeOrphanedApi"/> (superseded-login resurrection guard) -
    /// session 20260703-221336-2070027597 (RC6). Best-effort: both files may already be absent.
    /// </summary>
    private static void EraseAccountStore(IPrefillProgress? progress = null)
    {
        TryDeleteAccountStoreFile(AppConfig.AccountSettingsStorePath, "Account credentials", progress);
        TryDeleteAccountStoreFile(Path.Combine(AppConfig.ConfigDir, "storage.key"), "Storage key", progress);
    }

    private static void TryDeleteAccountStoreFile(string path, string label, IPrefillProgress? progress)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
                progress?.OnLog(LogLevel.Info, $"{label} removed from disk");
            }
        }
        catch (Exception ex)
        {
            progress?.OnLog(LogLevel.Warning, $"Could not remove {label.ToLowerInvariant()} from disk: {ex.Message}");
        }
    }

    private async Task BroadcastStatusAsync(string status, string message, string? displayName = null)
    {
        var statusEvent = new AuthStateEvent(status, message, displayName);
        await _socketServer.BroadcastAuthStateAsync(statusEvent);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;

        await _cts.CancelAsync();
        _loginCts?.Dispose();
        await _prefillOperation.DisposeAsync().AsTask();
        _budget.Dispose();
        _cts.Dispose();
        _api?.Dispose();
        _authProvider.Dispose();
        await _socketServer.DisposeAsync();
        _disposed = true;

        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Progress implementation that broadcasts updates via socket.
    /// </summary>
    internal sealed class SocketProgress : IPrefillProgress
    {
        public SocketServer? SocketServer { get; set; }
        private readonly DaemonLogSink _logSink = new(
            Console.WriteLine,
            AppConfig.DebugLogs ? DaemonLogLevel.Debug : DaemonLogLevel.Info);
        private DateTime _lastProgressBroadcast = DateTime.MinValue;
        private static readonly TimeSpan BroadcastThrottle = TimeSpan.FromMilliseconds(250);

        public void OnLog(LogLevel level, string message)
        {
            var daemonLevel = level switch
            {
                LogLevel.Debug => DaemonLogLevel.Debug,
                LogLevel.Info => DaemonLogLevel.Info,
                LogLevel.Warning => DaemonLogLevel.Warning,
                LogLevel.Error => DaemonLogLevel.Error,
                _ => DaemonLogLevel.Info
            };
            var prefix = level switch
            {
                LogLevel.Debug => "[DEBUG]",
                LogLevel.Info => "[INFO]",
                LogLevel.Warning => "[WARN]",
                LogLevel.Error => "[ERROR]",
                _ => "[LOG]"
            };
            _logSink.Write(daemonLevel, $"{DateTime.UtcNow:HH:mm:ss} {prefix} {message}");
        }

        public void OnOperationStarted(string operationName)
            => OnLog(LogLevel.Info, $"Starting: {operationName}");

        public void OnOperationCompleted(string operationName, TimeSpan elapsed)
            => OnLog(LogLevel.Info, $"Completed: {operationName} ({elapsed.TotalSeconds:F2}s)");

        public void OnAppStarted(AppDownloadInfo app)
        {
            OnLog(LogLevel.Info, $"Downloading: {app.Name} ({app.AppId})");
            BroadcastProgress(new PrefillProgressUpdate
            {
                State = "downloading",
                CurrentAppId = app.AppId,
                CurrentAppName = app.Name,
                TotalBytes = app.TotalBytes,
                BytesDownloaded = 0,
                PercentComplete = 0,
                UpdatedAt = DateTime.UtcNow
            });
        }

        public void OnDownloadProgress(DownloadProgressInfo progress)
        {
            var now = DateTime.UtcNow;
            if (now - _lastProgressBroadcast < BroadcastThrottle)
                return;

            _lastProgressBroadcast = now;

            var downloadedStr = FormatBytes(progress.BytesDownloaded);
            var totalStr = FormatBytes(progress.TotalBytes);
            var speedStr = FormatBytes((long)progress.BytesPerSecond) + "/s";
            OnLog(LogLevel.Debug, $"{progress.AppName}: {progress.PercentComplete:F1}% - {speedStr} - {downloadedStr} / {totalStr}");

            BroadcastProgress(new PrefillProgressUpdate
            {
                State = "downloading",
                CurrentAppId = progress.AppId,
                CurrentAppName = progress.AppName,
                TotalBytes = progress.TotalBytes,
                BytesDownloaded = progress.BytesDownloaded,
                PercentComplete = progress.PercentComplete,
                BytesPerSecond = progress.BytesPerSecond,
                Elapsed = progress.Elapsed,
                UpdatedAt = DateTime.UtcNow
            });
        }

        private static string FormatBytes(long bytes)
        {
            string[] sizes = { "B", "KB", "MB", "GB", "TB" };
            int order = 0;
            double size = bytes;
            while (size >= 1024 && order < sizes.Length - 1)
            {
                order++;
                size /= 1024;
            }
            return $"{size:F2} {sizes[order]}";
        }

        public void OnAppCompleted(AppDownloadInfo app, AppDownloadResult result)
        {
            OnLog(LogLevel.Info, $"Completed: {app.Name} - {result}");
            var bytesDownloaded = result == AppDownloadResult.Success ? app.TotalBytes : 0;
            var state = result == AppDownloadResult.AlreadyUpToDate ? "already_cached" : "app_completed";

            BroadcastProgress(new PrefillProgressUpdate
            {
                State = state,
                CurrentAppId = app.AppId,
                CurrentAppName = app.Name,
                TotalBytes = app.TotalBytes,
                BytesDownloaded = bytesDownloaded,
                Result = result.ToString(),
                UpdatedAt = DateTime.UtcNow
            });
        }

        public void OnPrefillCompleted(PrefillSummary summary)
        {
            OnLog(LogLevel.Info, $"Prefill complete: {summary.UpdatedApps} updated, {summary.AlreadyUpToDate} up-to-date, {summary.FailedApps} failed");
            BroadcastProgress(new PrefillProgressUpdate
            {
                State = "completed",
                TotalApps = summary.TotalApps,
                UpdatedApps = summary.UpdatedApps,
                AlreadyUpToDate = summary.AlreadyUpToDate,
                FailedApps = summary.FailedApps,
                TotalBytesTransferred = summary.TotalBytesTransferred,
                TotalTime = summary.TotalTime,
                UpdatedAt = DateTime.UtcNow
            });
        }

        public void OnError(string message, Exception? exception = null)
        {
            OnLog(LogLevel.Error, message);
            BroadcastProgress(new PrefillProgressUpdate
            {
                State = "error",
                ErrorMessage = message,
                UpdatedAt = DateTime.UtcNow
            });
        }

        public void OnCancelled(string message)
        {
            OnLog(LogLevel.Info, message);
            BroadcastProgress(new PrefillProgressUpdate
            {
                State = "cancelled",
                ErrorMessage = message,
                UpdatedAt = DateTime.UtcNow
            });
        }

        private void BroadcastProgress(PrefillProgressUpdate update)
        {
            if (SocketServer == null) return;

            var progressEvent = new ProgressEvent(update);
            _ = SocketServer.BroadcastProgressAsync(progressEvent);
        }
    }
}
