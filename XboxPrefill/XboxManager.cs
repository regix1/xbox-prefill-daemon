namespace XboxPrefill
{
    public sealed class XboxManager : IDisposable
    {
        private readonly IAnsiConsole _ansiConsole;
        private readonly DownloadArguments _downloadArgs;
        private readonly IXboxAuthProvider _authProvider;
        private readonly IPrefillProgress _progress;

        private readonly DownloadHandler _downloadHandler;
        private readonly XboxApi _xboxApi;
        private readonly AppInfoHandler _appInfoHandler;
        private readonly ManifestHandler _manifestHandler;
        private readonly XboxAccountManager _accountManager;
        private readonly HttpClientFactory _httpClientFactory;

        private readonly PrefillSummaryResult _prefillSummaryResult = new PrefillSummaryResult();

        public XboxManager(IAnsiConsole ansiConsole, DownloadArguments downloadArgs, IXboxAuthProvider authProvider, IPrefillProgress? progress = null)
        {
            _ansiConsole = ansiConsole;
            _downloadArgs = downloadArgs;
            // A real auth provider is mandatory: a fresh device-code login dereferences it, so it must never be null.
            _authProvider = authProvider ?? throw new ArgumentNullException(nameof(authProvider));
            _progress = progress ?? NullProgress.Instance;

            // Setup required classes
            _downloadHandler = new DownloadHandler(_ansiConsole, _progress);
            _appInfoHandler = new AppInfoHandler(_ansiConsole);
            _accountManager = XboxAccountManager.LoadFromFile(_ansiConsole, _authProvider);

            _httpClientFactory = new HttpClientFactory(_ansiConsole, _accountManager);
            _xboxApi = new XboxApi(_ansiConsole, _httpClientFactory);
            _manifestHandler = new ManifestHandler(_ansiConsole, _xboxApi);
        }

        public string? DisplayName => _accountManager.DisplayName;

        public async Task InitializeAsync()
        {
            await _accountManager.LoginAsync();
        }

        public async Task DownloadMultipleAppsAsync(bool downloadAllOwnedGames, bool force = false, List<string> manualIds = null, CancellationToken cancellationToken = default)
        {
            var allOwnedGames = await GetAvailableGamesAsync();

            var appIdsToDownload = LoadPreviouslySelectedApps();
            if (manualIds != null)
            {
                appIdsToDownload.AddRange(manualIds);
            }
            if (downloadAllOwnedGames)
            {
                appIdsToDownload = allOwnedGames.Select(e => e.AppId).ToList();
            }

            // Manual ProductIds may not appear in the titlehub library - prefill them directly.
            var ownedById = allOwnedGames.ToDictionary(e => e.AppId, e => e, StringComparer.OrdinalIgnoreCase);

            // Whitespace divider
            _ansiConsole.WriteLine();

            _progress.OnLog(LogLevel.Info, $"Starting prefill of {appIdsToDownload.Count} apps");

            foreach (var appId in appIdsToDownload.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                cancellationToken.ThrowIfCancellationRequested();

                AppInfo? app = null;
                try
                {
                    // Resolve from the owned library, or synthesise an AppInfo for a manually-entered ProductId.
                    app = ownedById.TryGetValue(appId, out var owned)
                        ? owned
                        : new AppInfo { AppId = appId, Title = appId };

                    await DownloadSingleAppAsync(app, force, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    // Propagate cancellation - don't treat it as a download error
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

            _ansiConsole.LogMarkupLine("Prefill complete!");
            _prefillSummaryResult.RenderSummaryTable(_ansiConsole);

            // Notify completion via progress interface
            _progress.OnPrefillCompleted(new PrefillSummary
            {
                TotalApps = _prefillSummaryResult.AlreadyUpToDate + _prefillSummaryResult.Updated + _prefillSummaryResult.FailedApps,
                UpdatedApps = _prefillSummaryResult.Updated,
                AlreadyUpToDate = _prefillSummaryResult.AlreadyUpToDate,
                FailedApps = _prefillSummaryResult.FailedApps,
                TotalBytesTransferred = (long)_prefillSummaryResult.TotalBytesTransferred.Bytes,
                TotalTime = _prefillSummaryResult.PrefillElapsedTime.Elapsed
            });
        }

        private async Task DownloadSingleAppAsync(AppInfo app, bool force = false, CancellationToken cancellationToken = default)
        {
            _progress.OnLog(LogLevel.Info, $"Starting download: {app.Title} ({app.AppId})");

            // Resolve the app's ProductId to a package manifest (ContentId -> GetBasePackage -> file queue).
            PackageManifest manifest;
            try
            {
                manifest = await _manifestHandler.ResolvePackageAsync(app);
                app.BuildVersion = manifest.Version;
                _progress.OnLog(LogLevel.Info, $"Resolved package for {app.Title}: version {manifest.Version}, CDN host {manifest.CdnHost}");
            }
            catch (Exception ex)
            {
                _progress.OnLog(LogLevel.Error, $"Failed to resolve package for {app.Title} ({app.AppId}): {ex.Message}");
                throw;
            }

            // Only download the app if it isn't already up to date (now that we know its version).
            if (force == false && _downloadArgs.Force == false && _appInfoHandler.AppIsUpToDate(app))
            {
                _prefillSummaryResult.AlreadyUpToDate++;
                var cachedAppInfo = new AppDownloadInfo { AppId = app.AppId, Name = app.Title, TotalBytes = 0 };
                _progress.OnAppStarted(cachedAppInfo);
                _progress.OnAppCompleted(cachedAppInfo, AppDownloadResult.AlreadyUpToDate);
                return;
            }

            var chunkDownloadQueue = manifest.QueuedRequests;

            // Logging some metadata about the downloads
            var downloadTimer = Stopwatch.StartNew();
            var totalBytes = ByteSize.FromBytes(chunkDownloadQueue.Sum(e => (long)e.DownloadSizeBytes));
            _prefillSummaryResult.TotalBytesTransferred += totalBytes;

            // Notify that app download is starting
            var appDownloadInfo = new AppDownloadInfo
            {
                AppId = app.AppId,
                Name = app.Title,
                TotalBytes = (long)totalBytes.Bytes,
                ChunkCount = chunkDownloadQueue.Count
            };
            _progress.OnAppStarted(appDownloadInfo);

            _ansiConsole.LogMarkupVerbose($"Downloading {Magenta(totalBytes.ToDecimalString())} from {LightYellow(chunkDownloadQueue.Count)} files");

            // Finally run the queued downloads
            var downloadSuccessful = await _downloadHandler.DownloadQueuedChunksAsync(chunkDownloadQueue, manifest, appId: app.AppId, appName: app.Title, cancellationToken: cancellationToken);
            if (downloadSuccessful)
            {
                // Logging some metrics about the download
                _ansiConsole.LogMarkupLine($"Finished in {LightYellow(downloadTimer.FormatElapsedString())} - {Magenta(totalBytes.CalculateBitrate(downloadTimer))}");
                _ansiConsole.WriteLine();

                _appInfoHandler.MarkDownloadAsSuccessful(app);
                _prefillSummaryResult.Updated++;
                _progress.OnAppCompleted(appDownloadInfo, AppDownloadResult.Success);
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
        public async Task<List<AppInfo>> GetAvailableGamesAsync()
        {
            var ownedTitles = await _xboxApi.GetOwnedTitlesAsync();

            var ownedApps = ownedTitles.Select(title => new AppInfo
            {
                AppId = title.ProductId,
                Title = title.Name ?? title.ProductId,
                Pfn = title.Pfn
            }).ToList();

            return ownedApps.OrderBy(e => e.Title, StringComparer.OrdinalIgnoreCase).ToList();
        }

        /// <summary>
        /// Checks if an app's current build version has been previously downloaded.
        /// </summary>
        public bool IsAppUpToDate(AppInfo app) => _appInfoHandler.AppIsUpToDate(app);

        /// <summary>
        /// Resolves the package manifest for an app, which carries the CDN host + download queue.
        /// </summary>
        public async Task<PackageManifest> GetManifestDownloadUrlAsync(AppInfo app)
        {
            return await _manifestHandler.ResolvePackageAsync(app);
        }

        public async Task<long> GetAppDownloadSizeAsync(AppInfo app)
        {
            var manifest = await _manifestHandler.ResolvePackageAsync(app);
            return manifest.QueuedRequests.Sum(e => (long)e.DownloadSizeBytes);
        }

        public void Dispose()
        {
            _downloadHandler.Dispose();
            _httpClientFactory.Dispose();
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
