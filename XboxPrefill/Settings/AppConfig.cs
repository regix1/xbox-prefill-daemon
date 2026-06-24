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

        //TODO comment
        public static TimeSpan DefaultRequestTimeout => TimeSpan.FromSeconds(60);

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

        #endregion
    }
}