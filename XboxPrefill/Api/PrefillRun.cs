#nullable enable

using LancachePrefill.Common;

namespace XboxPrefill.Api;

public sealed class PrefillRun : IPrefillProgress
{
    private static readonly string[] Presets = { "all", "recent", "top" };
    private readonly IPrefillProgress _log;
    private readonly RequestBudget _budget;
    private readonly object _sync = new();
    private readonly Dictionary<string, long> _bytes = new(StringComparer.Ordinal);
    public RunProgress Progress { get; }
    public RunOptions Options { get; }
    public ItemClaims Claims { get; }
    public string OperationId { get; }

    public bool IsCached(string appId, string revision)
        => Options.CachedApps.Any(app => string.Equals(app.AppId, appId, StringComparison.OrdinalIgnoreCase)
            && StringComparer.Ordinal.Equals(app.Revision, revision));

    public PrefillRun(string operationId, PrefillProtocol protocol, RunOptions options,
        RequestBudget budget, ItemClaims claims, IPrefillProgress log,
        Func<RunSnapshot, CancellationToken, Task>? publish = null)
    {
        OperationId = operationId;
        Options = protocol.Capture(options);
        Progress = new RunProgress(operationId, protocol.DaemonInstanceId, Options, publish);
        _budget = budget;
        Claims = claims;
        _log = log;
    }

    public Task<IDisposable> AcquireAsync(CancellationToken cancellationToken)
        => _budget.AcquireAsync(OperationId, Options.MaxConcurrency, cancellationToken);

    public void OnLog(LogLevel level, string message) => _log.OnLog(level, message);
    public void OnOperationStarted(string operationName) => OnLog(LogLevel.Info, operationName);
    public void OnOperationCompleted(string operationName, TimeSpan elapsed) => OnLog(LogLevel.Info, operationName);

    public void OnAppStarted(AppDownloadInfo app) => Progress.UpdateItem(new RunItemSnapshot
    {
        AppId = app.AppId,
        Name = app.Name,
        State = "downloading",
        TotalBytes = app.TotalBytes
    });

    public void OnDownloadProgress(DownloadProgressInfo progress)
    {
        lock (_sync)
        {
            var actual = Math.Max(_bytes.GetValueOrDefault(progress.AppId), progress.BytesDownloaded);
            _bytes[progress.AppId] = actual;
            Progress.UpdateItem(new RunItemSnapshot
            {
                AppId = progress.AppId,
                Name = progress.AppName,
                State = "downloading",
                BytesTransferred = actual,
                TotalBytes = progress.TotalBytes
            });
        }
    }

    public Task CompleteAsync()
    {
        lock (_sync) { return Progress.CompleteAsync(itemBytesTransferred: new Dictionary<string, long>(_bytes)); }
    }

    public bool TryChooseTerminal(string state, string? reason = null)
    {
        lock (_sync) { return Progress.TryChooseTerminal(state, reason); }
    }

    public bool CompleteItem(AppDownloadInfo app, Action commit, CancellationToken cancellationToken)
    {
        lock (_sync)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Progress.TryCommitItem(new RunItemSnapshot
            {
                AppId = app.AppId,
                Name = app.Name,
                State = "completed",
                Result = "success",
                TotalBytes = app.TotalBytes,
                CacheRevision = app.CacheRevision,
                BytesTransferred = _bytes.GetValueOrDefault(app.AppId)
            }, commit);
        }
    }

    public void OnAppCompleted(AppDownloadInfo app, AppDownloadResult result)
    {
        var outcome = result switch
        {
            AppDownloadResult.Success => "success",
            AppDownloadResult.AlreadyUpToDate => "already_cached",
            AppDownloadResult.Skipped => "skipped",
            _ => "failed"
        };
        lock (_sync) Progress.UpdateItem(new RunItemSnapshot
        {
            AppId = app.AppId,
            Name = app.Name,
            State = "completed",
            Result = outcome,
            Reason = outcome == "skipped" ? "skippedOverlap" : null,
            TotalBytes = app.TotalBytes,
            CacheRevision = app.CacheRevision,
            BytesTransferred = _bytes.GetValueOrDefault(app.AppId)
        });
    }

    public void OnPrefillCompleted(PrefillSummary summary) { }
    public void OnError(string message, Exception? exception = null) => OnLog(LogLevel.Error, message);

    public static RunOptions Capture(CommandRequest request, PrefillProtocol protocol)
    {
        var parameters = request.Parameters ?? throw new ArgumentException("Missing parameters.", nameof(request));
        protocol.ValidateInstance(parameters.GetValueOrDefault("daemonInstanceId") ?? string.Empty);
        var presets = Presets
            .Where(key => bool.TryParse(parameters.GetValueOrDefault(key), out var value) && value).ToArray();
        if (presets.Length > 1) { throw new ArgumentException("Conflicting selection presets.", nameof(request)); }
        var selection = parameters.GetValueOrDefault("selection") ?? presets.FirstOrDefault() ?? "selected";
        if (selection is not ("selected" or "all" or "recent" or "top") ||
            (presets.Length == 1 && presets[0] != selection))
        {
            throw new ArgumentException("Unsupported selection preset.", nameof(request));
        }
        IReadOnlyList<string>? ids = null;
        if (parameters.TryGetValue("appIds", out var json))
        {
            var parsed = JsonSerializer.Deserialize(json, DaemonSerializationContext.Default.ListString)
                ?? throw new ArgumentException("appIds must be an array.", nameof(request));
            ids = parsed.Select(id => id.Trim().ToUpperInvariant()).ToArray();
        }
        var cachedApps = parameters.TryGetValue("cachedApps", out var cachedJson)
            ? JsonSerializer.Deserialize(cachedJson, DaemonSerializationContext.Default.ListCachedAppInput)
                ?? throw new ArgumentException("cachedApps must be an array.", nameof(request))
            : [];
        var maximum = protocol.MaxConcurrentRequests;
        if (parameters.TryGetValue("maxConcurrency", out var concurrency) && !int.TryParse(concurrency, out maximum))
        {
            throw new ArgumentException("Invalid maxConcurrency.", nameof(request));
        }
        int? topCount = null;
        if (parameters.TryGetValue("topCount", out var count))
        {
            if (!int.TryParse(count, out var parsed) || parsed < 1 || parsed > 100)
            {
                throw new ArgumentException("topCount must be from 1 through 100.", nameof(request));
            }
            topCount = parsed;
        }
        return protocol.Capture(new RunOptions
        {
            AppIds = ids,
            Selection = selection,
            MaxConcurrency = maximum,
            TopCount = topCount,
            CachedApps = cachedApps,
            Force = bool.TryParse(parameters.GetValueOrDefault("force"), out var force) && force
        });
    }
}
