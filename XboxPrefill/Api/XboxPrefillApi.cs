#nullable enable

using XboxPrefill.Handlers;
using XboxPrefill.Models;
using XboxPrefill.Settings;

namespace XboxPrefill.Api;

/// <summary>
/// High-level programmatic API for Xbox Prefill operations.
/// </summary>
public sealed class XboxPrefillApi : IDisposable
{
    private readonly IXboxAuthProvider _authProvider;
    private readonly IPrefillProgress _progress;
    private readonly RequestBudget? _budget;
    private readonly int? _maxRequests;

    private XboxManager? _xboxManager;

    private List<string>? _selectedAppsCache;
    private bool _isInitialized;
    private bool _isDisposed;

    public XboxPrefillApi(
        IXboxAuthProvider authProvider,
        IPrefillProgress? progress = null, RequestBudget? budget = null, int? maxRequests = null)
    {
        _budget = budget;
        _maxRequests = maxRequests;
        _authProvider = authProvider ?? throw new ArgumentNullException(nameof(authProvider));
        _progress = progress ?? NullProgress.Instance;
    }

    public bool IsInitialized => _isInitialized;

    public Task PrefillAsync(PrefillRun run, CancellationToken cancellationToken)
    {
        ThrowIfNotInitialized();
        ThrowIfDisposed();
        return _xboxManager!.DownloadMultipleAppsAsync(run, cancellationToken);
    }

    public string? DisplayName => _xboxManager?.DisplayName;

    /// <summary>True when a long-lived MSA refresh token is stored (the real ~90d login bound).</summary>
    public bool HasRefreshToken => _xboxManager?.HasRefreshToken ?? false;

    /// <summary>Expiry (UTC) of the short-lived (~16h) XSTS tokens; null when none minted / not logged in.</summary>
    public DateTime? XstsExpiryUtc => _xboxManager?.XstsExpiryUtc;

    /// <summary>Expiry (UTC) of the MSA refresh token (issued + ~90d); the real ~90d re-login bound. Null when none stamped / not logged in.</summary>
    public DateTime? AuthExpiryUtc => _xboxManager?.AuthExpiryUtc;

    /// <summary>
    /// Initializes the API and logs into Xbox.
    /// </summary>
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        if (_isInitialized)
            return;

        _progress.OnOperationStarted("Initializing Xbox connection");
        var timer = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            BuildManager();

            await _xboxManager!.InitializeAsync();
            _isInitialized = true;

            _progress.OnOperationCompleted("Initializing Xbox connection", timer.Elapsed);
            _progress.OnLog(LogLevel.Info, "Successfully logged into Xbox");
        }
        catch (Exception ex)
        {
            _progress.OnError("Failed to initialize Xbox connection", ex);
            throw;
        }
    }

    /// <summary>
    /// Non-interactive login: imports a supplied MSA refresh token + device key (PKCS#8) into the
    /// encrypted store, then mints XSTS tokens. The device-code fallback is suppressed for this path.
    /// </summary>
    public async Task InitializeWithImportAsync(string refreshToken, string? deviceKeyPkcs8, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        if (_isInitialized)
            return;

        _progress.OnOperationStarted("Initializing Xbox connection (imported credentials)");
        var timer = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            BuildManager();

            await _xboxManager!.ImportLoginAsync(refreshToken, deviceKeyPkcs8, cancellationToken);
            _isInitialized = true;

            _progress.OnOperationCompleted("Initializing Xbox connection (imported credentials)", timer.Elapsed);
            _progress.OnLog(LogLevel.Info, "Successfully logged into Xbox (non-interactive)");
        }
        catch (Exception ex)
        {
            _progress.OnError("Failed to initialize Xbox connection from imported credentials", ex);
            throw;
        }
    }

    private void BuildManager()
    {
        var consoleAdapter = new ApiConsoleAdapter(_authProvider, _progress);

        var downloadArgs = new DownloadArguments
        {
            Force = false,
            TransferSpeedUnit = LancachePrefill.Common.Enums.TransferSpeedUnit.Bits
        };

        _xboxManager = new XboxManager(consoleAdapter, downloadArgs, _authProvider, _progress, _budget, _maxRequests);
    }

    /// <summary>
    /// Gets all games owned by the logged-in user
    /// </summary>
    public async Task<List<OwnedGame>> GetOwnedGamesAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfNotInitialized();
        ThrowIfDisposed();

        _progress.OnOperationStarted("Fetching owned games");
        var timer = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            var apps = await _xboxManager!.GetAvailableGamesAsync(cancellationToken);
            var result = apps.Select(a => new OwnedGame
            {
                AppId = a.AppId,
                Name = a.Title
            }).ToList();

            _progress.OnOperationCompleted("Fetching owned games", timer.Elapsed);
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _progress.OnError("Failed to fetch owned games", ex);
            throw;
        }
    }

    /// <summary>
    /// Gets CDN URL patterns for owned games.
    /// For each game, resolves the manifest URL to extract the CDN host and chunk base URL.
    /// These patterns can be used to identify which game a cached download belongs to.
    /// </summary>
    public async Task<CdnInfoResult> GetCdnInfoAsync(List<string>? appIds = null, CancellationToken cancellationToken = default)
    {
        ThrowIfNotInitialized();
        ThrowIfDisposed();

        _progress.OnOperationStarted("Fetching CDN info");
        var timer = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            var allGames = await _xboxManager!.GetAvailableGamesAsync(cancellationToken);

            // Filter to requested appIds if provided. Manually-entered ProductIds may not be in the owned library;
            // synthesize an AppInfo for those so they are still resolved rather than silently dropped.
            if (appIds != null && appIds.Count > 0)
            {
                var ownedById = allGames.ToDictionary(g => g.AppId, g => g, StringComparer.OrdinalIgnoreCase);
                allGames = appIds.Distinct(StringComparer.OrdinalIgnoreCase)
                                 .Select(id => ownedById.TryGetValue(id, out var owned)
                                     ? owned
                                     : new AppInfo { AppId = id, Title = id })
                                 .ToList();
            }

            var results = new List<CdnInfo>();
            foreach (var app in allGames)
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    var manifest = await _xboxManager.GetManifestDownloadUrlAsync(app, cancellationToken);
                    results.Add(new CdnInfo
                    {
                        AppId = app.AppId,
                        Name = app.Title,
                        CdnHost = manifest.ManifestDownloadUri.Host,
                        ChunkBaseUrl = manifest.ChunkBaseUrl,
                        FilePathFragments = manifest.FilePathFragments
                    });

                    // Opt-in [MAP] diagnostics: log the exact /filestreamingservice/files/<GUID> fragments this app
                    // emits for naming, so they can be compared against the GUIDs the daemon later requests.
                    MappingDebugLogger.LogCdnInfoEmit(_progress, app.Title, app.AppId, manifest.ManifestDownloadUri.Host, manifest.FilePathFragments);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _progress.OnLog(LogLevel.Warning, $"Failed to get CDN info for {app.Title} ({app.AppId}): {ex.Message}");
                    // Continue with other games
                }
            }

            _progress.OnOperationCompleted("Fetching CDN info", timer.Elapsed);
            return new CdnInfoResult
            {
                Apps = results,
                Message = $"Retrieved CDN info for {results.Count} of {allGames.Count} games"
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _progress.OnError("Failed to fetch CDN info", ex);
            throw;
        }
    }

    public List<string> GetSelectedApps()
    {
        ThrowIfNotInitialized();
        ThrowIfDisposed();

        if (_selectedAppsCache != null && _selectedAppsCache.Count > 0)
        {
            _progress.OnLog(LogLevel.Info, $"GetSelectedApps: Returning {_selectedAppsCache.Count} cached apps");
            return _selectedAppsCache;
        }

        var fileApps = _xboxManager!.LoadPreviouslySelectedApps();
        _progress.OnLog(LogLevel.Info, $"GetSelectedApps: Loaded {fileApps.Count} apps from file");
        return fileApps;
    }

    /// <summary>
    /// Gets status of selected apps including names and download sizes.
    /// Downloads manifests to calculate actual sizes (may take a moment for many apps).
    /// </summary>
    public async Task<SelectedAppsStatus> GetSelectedAppsStatusAsync(List<string>? operatingSystems = null, CancellationToken cancellationToken = default)
    {
        ThrowIfNotInitialized();
        ThrowIfDisposed();

        var selectedAppIds = GetSelectedApps();
        if (selectedAppIds.Count == 0)
        {
            return new SelectedAppsStatus
            {
                Apps = new List<AppStatus>(),
                TotalDownloadSize = 0,
                Message = "No apps selected"
            };
        }

        try
        {
            var allGames = await _xboxManager!.GetAvailableGamesAsync(cancellationToken);
            var gamesByAppId = allGames.ToDictionary(g => g.AppId, g => g);

            var apps = new List<AppStatus>();
            long totalDownloadSize = 0;

            foreach (var appId in selectedAppIds)
            {
                // Manually-entered ProductIds may not be in the owned library; synthesize an AppInfo so they are
                // still resolved (size + up-to-date check) instead of reported as size 0.
                if (!gamesByAppId.TryGetValue(appId, out var game))
                {
                    game = new AppInfo { AppId = appId, Title = appId };
                }

                var isUpToDate = _xboxManager.IsAppUpToDate(game);
                long downloadSize = 0;

                if (!isUpToDate)
                {
                    try
                    {
                        downloadSize = await _xboxManager.GetAppDownloadSizeAsync(game, cancellationToken);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        _progress.OnLog(LogLevel.Warning, $"Failed to get size for {game.Title}: {ex.Message}");
                    }
                }

                totalDownloadSize += downloadSize;
                apps.Add(new AppStatus
                {
                    AppId = appId,
                    Name = game.Title,
                    DownloadSize = downloadSize,
                    IsUpToDate = isUpToDate
                });
            }

            return new SelectedAppsStatus
            {
                Apps = apps,
                TotalDownloadSize = totalDownloadSize
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _progress.OnError("Failed to get selected apps status", ex);
            return new SelectedAppsStatus
            {
                Apps = new List<AppStatus>(),
                TotalDownloadSize = 0,
                Message = $"Error: {ex.Message}"
            };
        }
    }

    /// <summary>
    /// Checks cache status by comparing app build versions against previously downloaded versions.
    /// Returns which apps are up-to-date and which need updating.
    /// </summary>
    public async Task<CacheStatusResult> CheckCacheStatusAsync(List<CachedAppInput> cachedApps, CancellationToken cancellationToken = default)
    {
        ThrowIfNotInitialized();
        ThrowIfDisposed();

        if (cachedApps.Count == 0)
        {
            return new CacheStatusResult
            {
                Apps = new List<AppCacheStatus>(),
                Message = "No app IDs provided"
            };
        }

        try
        {
            var allGames = await _xboxManager!.GetAvailableGamesAsync(cancellationToken);
            var gamesByAppId = allGames.ToDictionary(g => g.AppId, g => g, StringComparer.OrdinalIgnoreCase);

            var apps = new List<AppCacheStatus>();
            foreach (var cachedApp in cachedApps
                         .Where(app => !string.IsNullOrWhiteSpace(app.Revision))
                         .DistinctBy(app => app.AppId, StringComparer.OrdinalIgnoreCase))
            {
                // Manually-entered ProductIds may not be in the owned library; synthesize an AppInfo so the cache
                // status is still reported (instead of omitting the requested ID).
                if (!gamesByAppId.TryGetValue(cachedApp.AppId, out var game))
                {
                    game = new AppInfo { AppId = cachedApp.AppId, Title = cachedApp.AppId };
                }
                var currentRevision = await _xboxManager.GetCurrentRevisionAsync(game, cancellationToken);

                apps.Add(new AppCacheStatus
                {
                    AppId = cachedApp.AppId,
                    Name = game.Title,
                    IsUpToDate = StringComparer.Ordinal.Equals(cachedApp.Revision, currentRevision)
                });
            }

            return new CacheStatusResult
            {
                Apps = apps,
                Message = $"Checked {apps.Count} apps"
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _progress.OnError("Failed to check cache status", ex);
            return new CacheStatusResult
            {
                Apps = new List<AppCacheStatus>(),
                Message = $"Error: {ex.Message}"
            };
        }
    }

    public void SetSelectedApps(IEnumerable<string> appIds)
    {
        ThrowIfNotInitialized();
        ThrowIfDisposed();

        var appIdList = appIds.ToList();

        _selectedAppsCache = appIdList;

        var tuiApps = appIdList.Select(id => new LancachePrefill.Common.SelectAppsTui.TuiAppInfo(id, "")
        {
            IsSelected = true
        }).ToList();

        _xboxManager!.SetAppsAsSelected(tuiApps);
        _progress.OnLog(LogLevel.Info, $"Set {tuiApps.Count} apps for prefill (cached in memory)");
    }

    /// <summary>
    /// Runs the prefill operation
    /// </summary>
    public async Task<PrefillResult> PrefillAsync(
        PrefillOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfNotInitialized();
        ThrowIfDisposed();

        options ??= new PrefillOptions();

        _progress.OnOperationStarted("Prefill operation");
        var timer = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            await _xboxManager!.DownloadMultipleAppsAsync(
                downloadAllOwnedGames: options.DownloadAllOwnedGames,
                force: options.Force,
                manualIds: options.ProductIds is { Count: > 0 } ? options.ProductIds : null,
                recent: options.Recent,
                top: options.Top,
                cancellationToken: cancellationToken);

            cancellationToken.ThrowIfCancellationRequested();
            _progress.OnOperationCompleted("Prefill operation", timer.Elapsed);

            return new PrefillResult
            {
                Success = true,
                TotalTime = timer.Elapsed
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _progress.OnLog(LogLevel.Info, "Prefill operation cancelled");
            throw;
        }
        catch (Exception ex)
        {
            _progress.OnError("Prefill operation failed", ex);
            throw;
        }
    }

    private static (int FileCount, long TotalBytes)? GetCacheStats()
    {
        var tempDir = new DirectoryInfo(AppConfig.TempDir);
        if (!tempDir.Exists)
            return null;

        var tempFiles = tempDir.EnumerateFiles("*.*", SearchOption.AllDirectories).ToList();
        return (tempFiles.Count, tempFiles.Sum(e => e.Length));
    }

    public static ClearCacheResult ClearCache()
    {
        var stats = GetCacheStats();
        if (stats is not { FileCount: > 0 })
        {
            return new ClearCacheResult { Success = true, FileCount = 0, BytesCleared = 0, Message = "Cache directory is already empty" };
        }

        var (fileCount, totalBytes) = stats.Value;

        try
        {
            Directory.Delete(AppConfig.TempDir, true);
            Directory.CreateDirectory(AppConfig.TempDir);
            var clearedSize = ByteSize.FromBytes(totalBytes);
            return new ClearCacheResult
            {
                Success = true,
                FileCount = fileCount,
                BytesCleared = totalBytes,
                Message = $"Cleared {fileCount} files ({clearedSize.ToDecimalString()})"
            };
        }
        catch (Exception ex)
        {
            return new ClearCacheResult { Success = false, FileCount = 0, BytesCleared = 0, Message = $"Failed to clear cache: {ex.Message}" };
        }
    }

    public static ClearCacheResult GetCacheInfo()
    {
        var stats = GetCacheStats();
        if (stats == null)
        {
            return new ClearCacheResult { Success = true, FileCount = 0, BytesCleared = 0, Message = "Cache directory is empty" };
        }

        var (fileCount, totalBytes) = stats.Value;
        var cacheSize = ByteSize.FromBytes(totalBytes);

        return new ClearCacheResult
        {
            Success = true,
            FileCount = fileCount,
            BytesCleared = totalBytes,
            Message = $"Cache contains {fileCount} files ({cacheSize.ToDecimalString()})"
        };
    }

    public void Shutdown()
    {
        // Unconditional (not gated on _isInitialized): _xboxManager is constructed synchronously
        // before the MSA/XSTS login handshake completes, so a logout racing a mid-login task must
        // still be able to drop the in-memory token state even though _isInitialized never
        // flipped true.
        _xboxManager?.ClearAccount();
        _isInitialized = false;
        _progress.OnLog(LogLevel.Info, "Disconnected from Xbox");
    }

    public void Dispose()
    {
        if (_isDisposed) return;

        Shutdown();
        _xboxManager?.Dispose();
        _isDisposed = true;
    }

    private void ThrowIfNotInitialized()
    {
        if (!_isInitialized)
            throw new InvalidOperationException("XboxPrefillApi not initialized. Call InitializeAsync first.");
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
    }
}
