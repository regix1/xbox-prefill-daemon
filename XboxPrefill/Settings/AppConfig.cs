namespace XboxPrefill.Settings
{
    public static class AppConfig
    {
        static AppConfig()
        {
            // Create required folders
            Directory.CreateDirectory(ConfigDir);
            Directory.CreateDirectory(TempDir);

            // Debugging folders
            Directory.CreateDirectory(DebugOutputDir);
            Directory.CreateDirectory(MetadataOutputDir);
            Directory.CreateDirectory(DownloadUrlPath);
        }

        /// <summary>
        /// Downloaded manifests, as well as other metadata, are saved into this directory to speedup future prefill runs.
        /// All data in here should be able to be deleted safely.
        /// </summary>
        public static readonly string TempDir = TempDirUtils.GetTempDirBaseDirectories("XboxPrefill", TempDirVersion);

        /// <summary>
        /// Increment when there is a breaking change made to the files in the cache directory
        /// </summary>
        private const string TempDirVersion = "v1";

        /// <summary>
        /// Contains user configuration.  Should not be deleted, doing so will reset the app back to defaults.
        /// </summary>
        public static readonly string ConfigDir = Path.Combine(AppContext.BaseDirectory, "Config");

        //TODO comment
        public static int MaxConcurrentRequests => 30;

        //TODO comment
        private static bool _verboseLogs;
        public static bool VerboseLogs
        {
            get => _verboseLogs;
            set
            {
                _verboseLogs = value;
                AnsiConsoleExtensions.WriteVerboseLogs = value;
            }
        }

        #region Timeouts

        /// <summary>
        /// Bounds a single Xbox Live or Store request. Everything using it carries small JSON: a title list, a
        /// catalog lookup, a package listing, or a token post, all of which answer in well under a second from a
        /// healthy service. The value stays low because it applies twice to one call, once to the wait for response
        /// headers and again to the reply body, and resolving one app makes two such calls before it gives up. That
        /// puts the ceiling for a single app against a dead service at four times this value, which has to stay
        /// inside a couple of minutes: a scheduled run walks a whole library, so a slow give-up multiplies by the
        /// number of games and a ten game run would spend over an hour before reporting anything useful.
        /// </summary>
        public static TimeSpan DefaultRequestTimeout => TimeSpan.FromSeconds(20);

        #endregion

        #region Serialization file paths

        public static readonly string AccountSettingsStorePath = Path.Combine(ConfigDir, "userAccount.json");

        public static readonly string UserSelectedAppsPath = Path.Combine(ConfigDir, "selectedAppsToPrefill.json");

        /// <summary>
        /// Keeps track of which apps + versions have been previously downloaded.  Is used to determine whether or not a game is up to date.
        /// </summary>
        public static readonly string SuccessfullyDownloadedAppsPath = Path.Combine(ConfigDir, "successfullyDownloadedApps.json");

        #endregion

        public static readonly string DefaultUserAgent = "Microsoft.Xbox.GameStreaming/10.0";

        #region Xbox endpoints / auth constants

        /// <summary>MSA client id used for the device-code flow (PROVEN against a live account).</summary>
        public const string ClientId = "0000000048183522";

        /// <summary>OAuth scope requested for the device-code grant.</summary>
        public const string AuthScope = "service::user.auth.xboxlive.com::MBI_SSL";

        public const string DeviceCodeUrl = "https://login.live.com/oauth20_connect.srf";
        public const string TokenUrl = "https://login.live.com/oauth20_token.srf";
        public const string DeviceCodeGrantType = "urn:ietf:params:oauth:grant-type:device_code";

        public const string UserAuthUrl = "https://user.auth.xboxlive.com/user/authenticate";
        public const string DeviceAuthUrl = "https://device.auth.xboxlive.com/device/authenticate";
        public const string XstsAuthUrl = "https://xsts.auth.xboxlive.com/xsts/authorize";

        /// <summary>XSTS relying party for titlehub enumeration. Does not require the device token.</summary>
        public const string TitleHubRelyingParty = "http://xboxlive.com";

        /// <summary>XSTS relying party for the package service. Device-bearing + signed.</summary>
        public const string UpdateRelyingParty = "http://update.xboxlive.com";

        public const string TitleHubBaseUrl = "https://titlehub.xboxlive.com";
        public const string DisplayCatalogBaseUrl = "https://displaycatalog.mp.microsoft.com";
        public const string PackageServiceBaseUrl = "https://packagespc.xboxlive.com/GetBasePackage/";

        /// <summary>
        /// Microsoft's public, unofficial "Most played games" storefront listing for Xbox - store-wide
        /// ranking, not per-account, and anonymous (no XSTS token needed). No documented JSON API is known
        /// to back this list, so <see cref="Handlers.XboxTrendingTitlesProvider"/> parses the server-rendered
        /// HTML directly; see that class for the graceful-degradation behavior if Microsoft changes the page.
        /// </summary>
        public const string MostPlayedGamesPageUrl = "https://www.microsoft.com/en-us/store/most-popular/games/xbox";

        #endregion

        #region Recent / Top preset limits

        /// <summary>
        /// Max number of titles selected for the "Recent" prefill preset (most-recently-played owned/Game
        /// Pass titles, per Xbox Live's title history). Override with <c>XBOX_RECENT_TITLES_LIMIT</c>;
        /// clamped 1-100; defaults to 10.
        /// </summary>
        public static int RecentTitlesLimit { get; } =
            ReadIntEnvironmentVariable("XBOX_RECENT_TITLES_LIMIT", defaultWhenUnset: 10, min: 1, max: 100);

        /// <summary>
        /// Max number of ranked titles requested from <see cref="MostPlayedGamesPageUrl"/> for the "Top"
        /// prefill preset, before intersecting with the account's owned/Game Pass library. Override with
        /// <c>XBOX_TOP_TITLES_LIMIT</c>; clamped 1-100; defaults to 25.
        /// </summary>
        public static int TopTitlesLimit { get; } =
            ReadIntEnvironmentVariable("XBOX_TOP_TITLES_LIMIT", defaultWhenUnset: 25, min: 1, max: 100);

        #endregion

        #region Debugging

        public static readonly string DebugOutputDir = Path.Combine(TempDir, "Debugging");
        public static readonly string MetadataOutputDir = Path.Combine(DebugOutputDir, "App Metadata");
        public static readonly string DownloadUrlPath = Path.Combine(DebugOutputDir, "Download URL");

        private static bool _debugLogs;
        public static bool DebugLogs
        {
            get => _debugLogs;
            set
            {
                _debugLogs = value;

                // Enable verbose logs as well
                VerboseLogs = true;
            }
        }

        /// <summary>
        /// Will skip over downloading chunks, but will still download manifests and build the chunk download list.  Useful for testing
        /// core logic of XboxPrefill without having to wait for downloads to finish.
        /// </summary>
        public static bool SkipDownloads { get; set; }

        /// <summary>
        /// Skips using locally cached manifests. Saves disk space, at the expense of slower subsequent runs.  Intended for debugging.
        /// </summary>
        public static bool NoLocalCache { get; set; }

        /// <summary>
        /// Name-mapping diagnostics. When true, the daemon emits greppable <c>[MAP]</c> lines to stdout
        /// (visible via <c>docker logs</c>) showing, per app, the <c>/filestreamingservice/files/&lt;GUID&gt;</c>
        /// fragments that <c>get-cdn-info</c> emits for naming and, per file, the GUID actually requested through
        /// lancache plus the CDN response status, so a human can confirm the emitted GUID matches the requested one.
        /// <para>
        /// Disabled by default. Set <c>XBOX_DEBUG_MAPPING</c> to a truthy value
        /// (<c>1</c>/<c>true</c>/<c>yes</c>/<c>on</c>, case-insensitive) to enable it. The current environment value
        /// is read whenever the diagnostics gate is queried.
        /// </para>
        /// </summary>
        public static bool DebugMapping
            => ReadBooleanEnvironmentVariable("XBOX_DEBUG_MAPPING", defaultWhenUnset: false);

        /// <summary>
        /// Reads a boolean switch from an environment variable. An unset, blank, or unrecognised value falls back to
        /// <paramref name="defaultWhenUnset"/>. Recognises <c>1</c>/<c>true</c>/<c>yes</c>/<c>on</c> (case-insensitive)
        /// as enabled and <c>0</c>/<c>false</c>/<c>no</c>/<c>off</c> as disabled, so a default-on switch can still be
        /// turned off explicitly.
        /// </summary>
        private static bool ReadBooleanEnvironmentVariable(string variableName, bool defaultWhenUnset)
        {
            var rawValue = Environment.GetEnvironmentVariable(variableName);
            if (string.IsNullOrWhiteSpace(rawValue))
            {
                return defaultWhenUnset;
            }

            return rawValue.Trim().ToLowerInvariant() switch
            {
                "1" or "true" or "yes" or "on" => true,
                "0" or "false" or "no" or "off" => false,
                _ => defaultWhenUnset
            };
        }

        /// <summary>
        /// How many days a freshly issued or rotated Microsoft (MSA) refresh token is treated as valid before an
        /// interactive re-login is required. Mirrors Microsoft's real ~90-day sliding refresh-token lifetime for the
        /// device-code / public-client flow, and is reported as <c>AuthExpiryUtc = lastRefresh + this</c>. This is the
        /// real token ceiling, not a policy knob (the manager's own login-validity setting is the policy); it is
        /// exposed here only so the value can be tuned without a code change if Microsoft's behaviour shifts. Override
        /// with <c>XBOX_REFRESH_TOKEN_VALIDITY_DAYS</c>; clamped to 1-365; defaults to 90. Read once at startup.
        /// </summary>
        public static int RefreshTokenValidityDays { get; } =
            ReadIntEnvironmentVariable("XBOX_REFRESH_TOKEN_VALIDITY_DAYS", defaultWhenUnset: 90, min: 1, max: 365);

        /// <summary>
        /// Reads a positive integer from an environment variable, clamped to [<paramref name="min"/>,
        /// <paramref name="max"/>]. An unset, blank, or unparseable value falls back to
        /// <paramref name="defaultWhenUnset"/>.
        /// </summary>
        private static int ReadIntEnvironmentVariable(string variableName, int defaultWhenUnset, int min, int max)
        {
            var rawValue = Environment.GetEnvironmentVariable(variableName);
            if (string.IsNullOrWhiteSpace(rawValue) || !int.TryParse(rawValue.Trim(), out var parsed))
            {
                return defaultWhenUnset;
            }

            return Math.Clamp(parsed, min, max);
        }

        #endregion
    }
}
