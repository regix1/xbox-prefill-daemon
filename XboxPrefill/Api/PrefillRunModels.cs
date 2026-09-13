#nullable enable

namespace XboxPrefill.Api;

public sealed class PrefillStart
{
    public bool Success => true;
    public string RunId { get; init; } = string.Empty;
    public string DaemonInstanceId { get; init; } = string.Empty;
    public string State { get; init; } = "started";
}

public class PrefillOptions
{
    public bool DownloadAllOwnedGames { get; set; }
    public bool Force { get; set; }

    /// <summary>Prefill the account's most-recently-played owned/Game Pass titles (Xbox Live title history).</summary>
    public bool Recent { get; set; }

    /// <summary>Prefill owned/Game Pass titles that also appear on Microsoft's public "most played" ranking.</summary>
    public bool Top { get; set; }

    /// <summary>
    /// Explicit Store ProductIds to prefill, in addition to the previously-selected apps. These may be IDs that
    /// are not present in the titlehub-owned library; they are prefilled directly by ProductId.
    /// </summary>
#pragma warning disable CA2227 // The socket request serializer replaces this collection from JSON.
    public List<string> ProductIds { get; set; } = new();
#pragma warning restore CA2227
}

public class PrefillResult
{
    public bool Success { get; init; }
    public string? ErrorMessage { get; init; }
    public TimeSpan TotalTime { get; init; }
}

public class ClearCacheResult
{
    public bool Success { get; init; }
    public int FileCount { get; init; }
    public long BytesCleared { get; init; }
    public string? Message { get; init; }
}

public class AppStatus
{
    public string AppId { get; init; } = "";
    public string Name { get; init; } = "";
    public long DownloadSize { get; init; }
    public bool IsUpToDate { get; init; }
}

public class SelectedAppsStatus
{
    public List<AppStatus> Apps { get; init; } = new();
    public long TotalDownloadSize { get; init; }
    public string? Message { get; init; }
}

public class OwnedGame
{
    public string AppId { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
}

public class CacheStatusResult
{
    public List<AppCacheStatus> Apps { get; init; } = new();
    public string? Message { get; init; }
}

public class AppCacheStatus
{
    public string AppId { get; init; } = "";
    public string Name { get; init; } = "";
    public bool IsUpToDate { get; init; }
}

public class CdnInfo
{
    public string AppId { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string CdnHost { get; init; } = string.Empty;
    public string ChunkBaseUrl { get; init; } = string.Empty;

    /// <summary>
    /// Stable per-file path fragments (<c>/filestreamingservice/files/&lt;36-char-GUID&gt;</c>, query string
    /// stripped) for each downloadable package file. The manager uses these to map cache hits back to this
    /// product. Empty when the manifest resolved no downloadable files.
    /// </summary>
    public List<string> FilePathFragments { get; init; } = new();
}

public class CdnInfoResult
{
    public List<CdnInfo> Apps { get; init; } = new();
    public string? Message { get; init; }
}

public class PrefillProgressUpdate
{
    public string? OperationId { get; init; }
    public string? DaemonInstanceId { get; init; }
    public long Sequence { get; init; }
    public string? Reason { get; init; }
    public DateTimeOffset? StartedAt { get; init; }
    public int SkippedApps { get; init; }
    public int CancelledApps { get; init; }
    public RunItemSnapshot? CurrentItem { get; init; }
    [System.Text.Json.Serialization.JsonPropertyName("state")]
    public string State { get; set; } = "idle";

    [System.Text.Json.Serialization.JsonPropertyName("message")]
    public string? Message { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("currentAppId")]
    public string? CurrentAppId { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("currentAppName")]
    public string? CurrentAppName { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("totalBytes")]
    public long TotalBytes { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("bytesDownloaded")]
    public long BytesDownloaded { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("percentComplete")]
    public double PercentComplete { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("bytesPerSecond")]
    public double BytesPerSecond { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("elapsed")]
    public TimeSpan Elapsed { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("elapsedSeconds")]
    public double ElapsedSeconds => Elapsed.TotalSeconds;

    [System.Text.Json.Serialization.JsonPropertyName("result")]
    public string? Result { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("errorMessage")]
    public string? ErrorMessage { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("totalApps")]
    public int TotalApps { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("updatedApps")]
    public int UpdatedApps { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("alreadyUpToDate")]
    public int AlreadyUpToDate { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("failedApps")]
    public int FailedApps { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("totalBytesTransferred")]
    public long TotalBytesTransferred { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("totalTime")]
    public TimeSpan TotalTime { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("totalTimeSeconds")]
    public double TotalTimeSeconds => TotalTime.TotalSeconds;

    [System.Text.Json.Serialization.JsonPropertyName("updatedAt")]
    public DateTime UpdatedAt { get; set; }
}

public class StatusData
{
    public int ProtocolVersion { get; init; }
    public IReadOnlyList<string> Features { get; init; } = Array.Empty<string>();
    public string DaemonInstanceId { get; init; } = string.Empty;
    public int MaxConcurrentRuns { get; init; }
    public int MaxConcurrentRequests { get; init; }
    public int RetentionHours => PrefillProtocol.RetentionHours;
    public int RetentionOperations => PrefillProtocol.RetentionOperations;
    public int RetentionItems => PrefillProtocol.RetentionItems;
    public IReadOnlyList<RunSnapshot> ActiveOperations { get; init; } = Array.Empty<RunSnapshot>();
    public IReadOnlyList<RunSnapshot> RecentOperations { get; init; } = Array.Empty<RunSnapshot>();
    public bool IsLoggedIn { get; init; }
    public bool IsInitialized { get; init; }
    public bool IsPrefilling { get; init; }

    /// <summary>
    /// UTC ISO-8601 expiry of the real login bound (the MSA refresh token, ~90d sliding) when an explicit
    /// expiry is stored. The refresh token itself carries no persisted expiry timestamp, so this is null
    /// unless one becomes available; <see cref="XstsExpiryUtc"/> is always populated while logged in.
    /// Null when not logged in.
    /// </summary>
    public DateTime? AuthExpiryUtc { get; init; }

    /// <summary>
    /// UTC ISO-8601 expiry of the short-lived (~16h) XSTS tokens. Populated whenever tokens have been
    /// minted; null when not logged in.
    /// </summary>
    public DateTime? XstsExpiryUtc { get; init; }

    /// <summary>The signed-in account display name (gamertag), when available; null otherwise.</summary>
    public string? AccountDisplayName { get; init; }
}

/// <summary>
/// The decrypted payload for the <c>provide-auto-login</c> command: the long-lived MSA refresh token and the
/// device identity ECDSA private key (PKCS#8, base64). Sent encrypted over the same ECDH + AES-GCM channel the
/// interactive device-code flow uses, so no new credential transport / crypto is introduced.
/// </summary>
public class AutoLoginPayload
{
    [System.Text.Json.Serialization.JsonPropertyName("refreshToken")]
    public string RefreshToken { get; init; } = string.Empty;

    [System.Text.Json.Serialization.JsonPropertyName("deviceKeyPkcs8")]
    public string? DeviceKeyPkcs8 { get; init; }
}

public class CommandRequest
{
    public string Id { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
#pragma warning disable CA2227 // The socket request serializer replaces this collection from JSON.
    public Dictionary<string, string>? Parameters { get; set; }
#pragma warning restore CA2227
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class CommandResponse
{
    public string Id { get; set; } = string.Empty;
    public bool Success { get; set; }
    public string? Message { get; set; }
    public string? Error { get; set; }
    public object? Data { get; set; }
    public bool RequiresLogin { get; set; }
    public DateTime CompletedAt { get; set; } = DateTime.UtcNow;
}

public enum LogLevel
{
    Debug,
    Info,
    Warning,
    Error
}

public class AppDownloadInfo
{
    public string AppId { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public long TotalBytes { get; init; }
    public int ChunkCount { get; init; }
}

public class DownloadProgressInfo
{
    public string AppId { get; init; } = string.Empty;
    public string AppName { get; init; } = string.Empty;
    public long BytesDownloaded { get; init; }
    public long TotalBytes { get; init; }
    public double PercentComplete => TotalBytes > 0 ? (double)BytesDownloaded / TotalBytes * 100 : 0;
    public double BytesPerSecond { get; init; }
    public TimeSpan Elapsed { get; init; }
}

public enum AppDownloadResult
{
    Success,
    AlreadyUpToDate,
    Failed,
    Skipped,
    NoDepotsToDownload
}

public class PrefillSummary
{
    public int TotalApps { get; init; }
    public int UpdatedApps { get; init; }
    public int AlreadyUpToDate { get; init; }
    public int FailedApps { get; init; }
    public long TotalBytesTransferred { get; init; }
    public TimeSpan TotalTime { get; init; }
}

public class SocketEvent<T>
{
    public string Type { get; init; } = string.Empty;
    public T? Data { get; init; }
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
}

public class CredentialChallengeEvent : SocketEvent<CredentialChallenge>
{
    public CredentialChallengeEvent(CredentialChallenge challenge)
    {
        Type = "credential-challenge";
        Data = challenge;
    }
}

public class ProgressEvent : SocketEvent<PrefillProgressUpdate>
{
    public ProgressEvent(PrefillProgressUpdate progress)
    {
        Type = "progress";
        Data = progress;
    }
}

public class AuthStateEvent : SocketEvent<AuthStateData>
{
    public AuthStateEvent(string state, string? message = null, string? displayName = null)
    {
        Type = "auth-state";
        Data = new AuthStateData { State = state, Message = message, DisplayName = displayName };
    }
}

public class AuthStateData
{
    public string State { get; init; } = string.Empty;
    public string? Message { get; init; }
    public string? DisplayName { get; init; }
}

public enum SocketServerMode
{
    UnixSocket,
    Tcp
}
