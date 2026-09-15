#nullable enable annotations

namespace XboxPrefill
{
    public sealed class XboxManager : IDisposable
    {
        private readonly IAnsiConsole _ansiConsole;
        private readonly DownloadArguments _downloadArgs;
        private readonly IXboxAuthProvider _authProvider;
        private readonly IPrefillProgress _progress;
        private readonly PrefillRun? _run;
        private readonly bool _ownsAccount = true;

        private readonly DownloadHandler _downloadHandler;
        private readonly XboxApi _xboxApi;
        private readonly AppInfoHandler _appInfoHandler;
        private readonly ManifestHandler _manifestHandler;
        private readonly XboxAccountManager _accountManager;
        private readonly HttpClientFactory _httpClientFactory;
        private readonly XboxTrendingTitlesProvider _trendingTitlesProvider;

        private PrefillSummaryResult _prefillSummaryResult = new PrefillSummaryResult();

        public XboxManager(IAnsiConsole ansiConsole, DownloadArguments downloadArgs, IXboxAuthProvider authProvider, IPrefillProgress? progress = null,
            RequestBudget? budget = null, int? maxRequests = null)
        {
            _ansiConsole = ansiConsole;
            _downloadArgs = downloadArgs;
            // A real auth provider is mandatory: a fresh device-code login dereferences it, so it must never be null.
            _authProvider = authProvider ?? throw new ArgumentNullException(nameof(authProvider));
            _progress = progress ?? NullProgress.Instance;

            // Setup required classes
            _downloadHandler = new DownloadHandler(_ansiConsole, _progress, budget, maxRequests);
            _appInfoHandler = new AppInfoHandler(_ansiConsole);
            _accountManager = XboxAccountManager.LoadFromFile(_ansiConsole, _authProvider);

            _httpClientFactory = new HttpClientFactory(_ansiConsole, _accountManager);
            _xboxApi = new XboxApi(_ansiConsole, _httpClientFactory);
            _manifestHandler = new ManifestHandler(_ansiConsole, _xboxApi);
            _trendingTitlesProvider = new XboxTrendingTitlesProvider(_ansiConsole, _httpClientFactory.AnonymousClient);
        }

        public string? DisplayName => _accountManager.DisplayName;

        private XboxManager(XboxManager session, PrefillRun run)
        {
            _run = run;
            _ownsAccount = false;
            _authProvider = session._authProvider;
            _progress = run;
            _ansiConsole = new ApiConsoleAdapter(_authProvider, run);
            _downloadArgs = new DownloadArguments { Force = run.Options.Force };
            _downloadHandler = new DownloadHandler(_ansiConsole, run);
            _appInfoHandler = session._appInfoHandler;
            _accountManager = session._accountManager;
            _httpClientFactory = session._httpClientFactory;
            _xboxApi = session._xboxApi;
            _manifestHandler = session._manifestHandler;
            _trendingTitlesProvider = session._trendingTitlesProvider;
        }

        public async Task DownloadMultipleAppsAsync(PrefillRun run, CancellationToken cancellationToken)
        {
            using var execution = new XboxManager(this, run);
            await execution.DownloadMultipleAppsAsync(run.Options.Selection == "all", run.Options.Force,
                run.Options.AppIds?.ToList(), run.Options.Selection == "recent", run.Options.Selection == "top", cancellationToken);
        }

        /// <summary>True when a long-lived MSA refresh token is stored (the real ~90d login bound).</summary>
        public bool HasRefreshToken => _accountManager.HasRefreshToken;

        /// <summary>Expiry (UTC) of the short-lived (~16h) XSTS tokens; null when none minted.</summary>
        public DateTime? XstsExpiryUtc => _accountManager.XstsExpiryUtc;

        /// <summary>Expiry (UTC) of the MSA refresh token (issued + ~90d); null when none stamped.</summary>
        public DateTime? AuthExpiryUtc => _accountManager.AuthExpiryUtc;

        /// <summary>Drops the in-memory MSA/XSTS token state. See <see cref="XboxAccountManager.ClearAccount"/>.</summary>
        public void ClearAccount() => _accountManager.ClearAccount();

        public async Task InitializeAsync()
        {
            await _accountManager.LoginAsync();
        }

        /// <summary>
        /// Imports an MSA refresh token + device key (PKCS#8) into the encrypted store and logs in
        /// non-interactively, with the device-code fallback suppressed.
        /// </summary>
        public async Task ImportLoginAsync(string refreshToken, string? deviceKeyPkcs8, CancellationToken cancellationToken = default)
        {
            await _accountManager.ImportAndLoginAsync(refreshToken, deviceKeyPkcs8, cancellationToken);
        }

        public async Task DownloadMultipleAppsAsync(
            bool downloadAllOwnedGames,
            bool force = false,
            List<string>? manualIds = null,
            bool recent = false,
            bool top = false,
            CancellationToken cancellationToken = default)
        {
            _prefillSummaryResult = new PrefillSummaryResult();
            var allOwnedGames = await GetAvailableGamesAsync(cancellationToken);

            var appIdsToDownload = _run == null ? LoadPreviouslySelectedApps() : new List<string>();
            if (manualIds != null)
            {
                appIdsToDownload.AddRange(manualIds);
            }
            if (downloadAllOwnedGames)
            {
                appIdsToDownload = allOwnedGames.Select(e => e.AppId).ToList();
            }
            else if (recent)
            {
                appIdsToDownload = SelectRecentlyPlayedAppIds(allOwnedGames);
            }
            else if (top)
            {
                appIdsToDownload = await SelectTopTitleAppIdsAsync(allOwnedGames, cancellationToken);
            }

            appIdsToDownload = appIdsToDownload.Select(id => id.Trim().ToUpperInvariant())
                .Distinct(StringComparer.Ordinal).ToList();
            if (_run != null && !_run.Progress.Snapshot.SelectionResolved) _run.Progress.ResolveSelection(appIdsToDownload);

            // Manual ProductIds may not appear in the titlehub library - prefill them directly.
            var ownedById = allOwnedGames.ToDictionary(e => e.AppId, e => e, StringComparer.OrdinalIgnoreCase);

            // Whitespace divider
            _ansiConsole.WriteLine();

            _progress.OnLog(LogLevel.Info, $"Starting prefill of {appIdsToDownload.Count} apps");

            foreach (var appId in appIdsToDownload.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                cancellationToken.ThrowIfCancellationRequested();

                using var claim = _run?.Claims.TryClaim(_run.OperationId, new[] { appId });
                if (_run != null && claim == null)
                {
                    _progress.OnAppCompleted(new AppDownloadInfo { AppId = appId, Name = appId }, AppDownloadResult.Skipped);
                    continue;
                }

                AppInfo? app = null;
                try
                {
                    // Resolve from the owned library, or synthesise an AppInfo for a manually-entered ProductId.
                    app = ownedById.TryGetValue(appId, out var owned)
                        ? new AppInfo { AppId = appId, Title = owned.Title, Pfn = owned.Pfn, LastTimePlayed = owned.LastTimePlayed }
                        : new AppInfo { AppId = appId, Title = appId };

                    await DownloadSingleAppAsync(app, force, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    // Propagate cancellation - don't treat it as a download error
                    throw;
                }
                catch (XboxLoginException)
                {
                    throw;
                }
                catch (Exception e) when (e is LancacheNotFoundException)
                {
                    // We'll want to bomb out the entire process for these exceptions, as they mean we can't prefill any apps at all
                    throw;
                }
                catch (Exception e)
                {
                    // Need to catch any exceptions that might happen during a single download, so that the other apps won't be affected
                    var appName = app?.Title ?? appId;
                    _progress.OnLog(LogLevel.Error, $"Download error for {appName}: {e.Message}");
                    _prefillSummaryResult.FailedApps++;

                    _progress.OnAppCompleted(
                        new AppDownloadInfo { AppId = app?.AppId ?? appId, Name = appName, TotalBytes = 0 },
                        AppDownloadResult.Failed);
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            _ansiConsole.LogMarkupLine("Prefill complete!");
            _prefillSummaryResult.RenderSummaryTable(_ansiConsole);

            if (_prefillSummaryResult.FailedApps > 0)
            {
                throw new InvalidOperationException("One or more Xbox downloads failed.");
            }

            // Notify completion via progress interface
            _progress.OnPrefillCompleted(new PrefillSummary
            {
                TotalApps = appIdsToDownload.Count,
                UpdatedApps = _prefillSummaryResult.Updated,
                AlreadyUpToDate = _prefillSummaryResult.AlreadyUpToDate,
                FailedApps = _prefillSummaryResult.FailedApps,
                TotalBytesTransferred = (long)_prefillSummaryResult.TotalBytesTransferred.Bytes,
                TotalTime = _prefillSummaryResult.PrefillElapsedTime.Elapsed
            });
        }

        /// <summary>
        /// Owned/Game Pass titles with Xbox Live title-history data, newest-played first, capped at
        /// <see cref="AppConfig.RecentTitlesLimit"/>. Titles Xbox Live never reported a
        /// <see cref="AppInfo.LastTimePlayed"/> for (never played, or history not visible) are excluded
        /// rather than sorted to the end, since "recent" implies an actual play event.
        /// </summary>
        private static List<string> SelectRecentlyPlayedAppIds(List<AppInfo> allOwnedGames)
        {
            return allOwnedGames
                .Where(g => g.LastTimePlayed.HasValue)
                .OrderByDescending(g => g.LastTimePlayed.Value)
                .Take(AppConfig.RecentTitlesLimit)
                .Select(g => g.AppId)
                .ToList();
        }

        /// <summary>
        /// Intersects Microsoft's public "most played" storefront ranking with the account's owned/Game Pass
        /// library (only entitled titles are actually downloadable), ordered by that external rank. Falls
        /// back to every owned game - loudly logged, never silently - if the ranking is unavailable or none
        /// of it matches anything owned, since "Top" must never silently prefill zero games.
        /// </summary>
        private async Task<List<string>> SelectTopTitleAppIdsAsync(List<AppInfo> allOwnedGames, CancellationToken cancellationToken)
        {
            using var permit = _run == null ? null : await _run.AcquireAsync(cancellationToken);
            var trendingProductIds = await _trendingTitlesProvider.GetTrendingProductIdsAsync(_run?.Options.TopCount ?? AppConfig.TopTitlesLimit, cancellationToken);
            if (trendingProductIds.Count == 0)
            {
                _progress.OnLog(LogLevel.Warning, "Could not retrieve Microsoft's most-played games list; falling back to all owned games for the \"Top\" prefill.");
                return allOwnedGames.Select(e => e.AppId).ToList();
            }

            var rank = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < trendingProductIds.Count; i++)
            {
                rank.TryAdd(trendingProductIds[i], i);
            }

            var rankedOwnedAppIds = allOwnedGames
                .Where(g => rank.ContainsKey(g.AppId))
                .OrderBy(g => rank[g.AppId])
                .Select(g => g.AppId)
                .ToList();

            if (rankedOwnedAppIds.Count == 0)
            {
                _progress.OnLog(LogLevel.Warning, "None of Microsoft's currently most-played games are in your owned/Game Pass library; falling back to all owned games for the \"Top\" prefill.");
                return allOwnedGames.Select(e => e.AppId).ToList();
            }

            return rankedOwnedAppIds;
        }

        private async Task DownloadSingleAppAsync(AppInfo app, bool force = false, CancellationToken cancellationToken = default)
        {
            _progress.OnLog(LogLevel.Info, $"Starting download: {app.Title} ({app.AppId})");

            // Resolve the app's ProductId to a package manifest (ContentId -> GetBasePackage -> file queue).
            PackageManifest manifest;
            try
            {
                manifest = await _manifestHandler.ResolvePackageAsync(app, _run, cancellationToken);
                app.BuildVersion = manifest.Version;
                _progress.OnLog(LogLevel.Info, $"Resolved package for {app.Title}: version {manifest.Version}, CDN host {manifest.CdnHost}");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _progress.OnLog(LogLevel.Error, $"Failed to resolve package for {app.Title} ({app.AppId}): {ex.Message}");
                throw;
            }

            // Only download the app if it isn't already up to date (now that we know its version).
            var isCached = _run != null
                ? _run.IsCached(app.AppId, manifest.Version)
                : _appInfoHandler.AppIsUpToDate(app);
            if (force == false && _downloadArgs.Force == false && isCached == true)
            {
                _prefillSummaryResult.AlreadyUpToDate++;
                var cachedApp = new AppDownloadInfo
                {
                    AppId = app.AppId,
                    Name = app.Title,
                    TotalBytes = 0,
                    CacheRevision = manifest.Version
                };
                _progress.OnAppStarted(cachedApp);
                _progress.OnAppCompleted(cachedApp, AppDownloadResult.AlreadyUpToDate);
                return;
            }

            var chunkDownloadQueue = manifest.QueuedRequests;

            // Logging some metadata about the downloads
            var downloadTimer = Stopwatch.StartNew();
            var totalBytes = ByteSize.FromBytes(chunkDownloadQueue.Sum(e => (long)e.DownloadSizeBytes));

            // Notify that app download is starting
            var appDownloadInfo = new AppDownloadInfo
            {
                AppId = app.AppId,
                Name = app.Title,
                TotalBytes = (long)totalBytes.Bytes,
                ChunkCount = chunkDownloadQueue.Count,
                CacheRevision = manifest.Version
            };
            _progress.OnAppStarted(appDownloadInfo);

            _ansiConsole.LogMarkupVerbose($"Downloading {Magenta(totalBytes.ToDecimalString())} from {LightYellow(chunkDownloadQueue.Count)} files");

            // Finally run the queued downloads
            bool downloadSuccessful;
            try
            {
                downloadSuccessful = await _downloadHandler.DownloadQueuedChunksAsync(chunkDownloadQueue, manifest, appId: app.AppId, appName: app.Title, cancellationToken: cancellationToken);
            }
            finally
            {
                _progress.OnDownloadProgress(new DownloadProgressInfo
                {
                    AppId = app.AppId,
                    AppName = app.Title,
                    TotalBytes = appDownloadInfo.TotalBytes,
                    BytesDownloaded = _downloadHandler.BytesTransferred
                });
            }
            cancellationToken.ThrowIfCancellationRequested();
            _prefillSummaryResult.TotalBytesTransferred += ByteSize.FromBytes(_downloadHandler.BytesTransferred);
            if (downloadSuccessful)
            {
                // Logging some metrics about the download
                _ansiConsole.LogMarkupLine($"Finished in {LightYellow(downloadTimer.FormatElapsedString())} - {Magenta(totalBytes.CalculateBitrate(downloadTimer))}");
                _ansiConsole.WriteLine();

                if (_run != null)
                {
                    var committed = AppConfig.SkipDownloads
                        ? _run.CompleteItem(appDownloadInfo, static () => { }, cancellationToken)
                        : _appInfoHandler.MarkDownloadAsSuccessful(app, _run, appDownloadInfo, cancellationToken);
                    if (!committed) return;
                }
                else
                {
                    if (!AppConfig.SkipDownloads) _appInfoHandler.MarkDownloadAsSuccessful(app);
                    _progress.OnAppCompleted(appDownloadInfo, AppDownloadResult.Success);
                }
                _prefillSummaryResult.Updated++;
            }
            else
            {
                _prefillSummaryResult.FailedApps++;
                _progress.OnAppCompleted(appDownloadInfo, AppDownloadResult.Failed);
            }
        }

        /// <summary>
        /// Returns the account's prefillable titles (MS-Store/Xbox titles from titlehub).
        /// </summary>
        public async Task<List<AppInfo>> GetAvailableGamesAsync(CancellationToken cancellationToken = default)
        {
            var ownedTitles = await _xboxApi.GetOwnedTitlesAsync(_run, cancellationToken);

            var ownedApps = ownedTitles.Select(title => new AppInfo
            {
                AppId = title.ProductId,
                Title = title.Name ?? title.ProductId,
                Pfn = title.Pfn,
                LastTimePlayed = title.TitleHistory?.LastTimePlayed
            }).ToList();

            return ownedApps.OrderBy(e => e.Title, StringComparer.OrdinalIgnoreCase).ToList();
        }

        /// <summary>
        /// Checks if an app's current build version has been previously downloaded.
        /// </summary>
        public bool? IsAppUpToDate(AppInfo app) => _appInfoHandler.AppIsUpToDate(app);

        public async Task<string> GetCurrentRevisionAsync(AppInfo app, CancellationToken cancellationToken = default)
        {
            var manifest = await _manifestHandler.ResolvePackageAsync(app, null, cancellationToken);
            return manifest.Version;
        }

        /// <summary>
        /// Resolves the package manifest for an app, which carries the CDN host + download queue.
        /// </summary>
        public async Task<PackageManifest> GetManifestDownloadUrlAsync(
            AppInfo app,
            CancellationToken cancellationToken = default)
        {
            return await _manifestHandler.ResolvePackageAsync(app, cancellationToken);
        }

        public async Task<long> GetAppDownloadSizeAsync(
            AppInfo app,
            CancellationToken cancellationToken = default)
        {
            var manifest = await _manifestHandler.ResolvePackageAsync(app, cancellationToken);
            return manifest.QueuedRequests.Sum(e => (long)e.DownloadSizeBytes);
        }

        public void Dispose()
        {
            _downloadHandler.Dispose();
            if (_ownsAccount) _httpClientFactory.Dispose();
        }

        #region Select Apps

        public void SetAppsAsSelected(List<TuiAppInfo> userSelected)
        {
            List<string> selectedAppIds = userSelected.Where(e => e.IsSelected)
                                                      .Select(e => e.AppId)
                                                      .ToList();
            File.WriteAllText(AppConfig.UserSelectedAppsPath, JsonSerializer.Serialize(selectedAppIds, SerializationContext.Default.ListString));

            _ansiConsole.LogMarkupLine($"Selected {Magenta(selectedAppIds.Count)} apps to prefill!  ");
        }

        public List<string> LoadPreviouslySelectedApps()
        {
            if (File.Exists(AppConfig.UserSelectedAppsPath))
            {
                return JsonSerializer.Deserialize(File.ReadAllText(AppConfig.UserSelectedAppsPath), SerializationContext.Default.ListString);
            }
            return new List<string>();
        }

        #endregion
    }
}
