#nullable enable annotations

namespace XboxPrefill.Handlers
{
    public sealed class AppInfoHandler
    {
        private static readonly object CommitLock = new();
        private readonly string _path;
        private readonly Action<string, string> _replace;
        private readonly Action? _prepared;

        public AppInfoHandler(IAnsiConsole ansiConsole)
            : this(AppConfig.SuccessfullyDownloadedAppsPath, (source, target) => File.Move(source, target, true))
        {
        }

        internal AppInfoHandler(string path, Action<string, string> replace, Action? prepared = null)
        {
            _path = path;
            _replace = replace;
            _prepared = prepared;
        }

        public void MarkDownloadAsSuccessful(AppInfo appInfo)
            => MarkDownloadAsSuccessful(appInfo, null, new AppDownloadInfo { AppId = appInfo.AppId }, CancellationToken.None);

        internal bool MarkDownloadAsSuccessful(AppInfo appInfo, PrefillRun? run, AppDownloadInfo app, CancellationToken cancellationToken)
        {
            lock (CommitLock)
            {
                var downloaded = Read();
                if (!downloaded.TryGetValue(appInfo.AppId, out var versions))
                {
                    versions = new HashSet<string>();
                    downloaded.Add(appInfo.AppId, versions);
                }
                versions.Add(appInfo.BuildVersion);
                var temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    {
                        JsonSerializer.Serialize(stream, downloaded, SerializationContext.Default.DictionaryStringHashSetString);
                        stream.Flush(true);
                    }
                    _prepared?.Invoke();
                    if (run != null)
                    {
                        return run.CompleteItem(app, () => _replace(temporary, _path), cancellationToken);
                    }
                    _replace(temporary, _path);
                    return true;
                }
                finally
                {
                    if (File.Exists(temporary)) File.Delete(temporary);
                }
            }
        }

        public bool? AppIsUpToDate(AppInfo appInfo)
        {
            lock (CommitLock)
            {
                return Read().TryGetValue(appInfo.AppId, out var versions)
                    ? versions.Contains(appInfo.BuildVersion)
                    : null;
            }
        }

        private Dictionary<string, HashSet<string>> Read()
        {
            if (!File.Exists(_path)) return new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            var downloaded = JsonSerializer.Deserialize(File.ReadAllText(_path), SerializationContext.Default.DictionaryStringHashSetString)
                ?? throw new JsonException("The downloaded Xbox app list is empty.");
            return new Dictionary<string, HashSet<string>>(downloaded, StringComparer.OrdinalIgnoreCase);
        }
    }
}
