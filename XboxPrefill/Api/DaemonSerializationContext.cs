using System.Text.Json.Serialization;

namespace XboxPrefill.Api;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(CommandRequest))]
[JsonSerializable(typeof(CommandResponse))]
[JsonSerializable(typeof(CredentialChallenge))]
[JsonSerializable(typeof(EncryptedCredentialResponse))]
[JsonSerializable(typeof(List<OwnedGame>))]
[JsonSerializable(typeof(List<string>))]
[JsonSerializable(typeof(PrefillResult))]
[JsonSerializable(typeof(StatusData))]
[JsonSerializable(typeof(AutoLoginPayload))]
[JsonSerializable(typeof(PrefillProgressUpdate))]
[JsonSerializable(typeof(ClearCacheResult))]
[JsonSerializable(typeof(AppStatus))]
[JsonSerializable(typeof(SelectedAppsStatus))]
[JsonSerializable(typeof(CacheStatusResult))]
[JsonSerializable(typeof(AppCacheStatus))]
[JsonSerializable(typeof(CdnInfo))]
[JsonSerializable(typeof(CdnInfoResult))]
// Socket event types
[JsonSerializable(typeof(SocketEvent<CredentialChallenge>))]
[JsonSerializable(typeof(SocketEvent<PrefillProgressUpdate>))]
[JsonSerializable(typeof(SocketEvent<AuthStateData>))]
[JsonSerializable(typeof(AuthStateData))]
[JsonSerializable(typeof(object))]
internal sealed partial class DaemonSerializationContext : JsonSerializerContext
{
}

public class StatusData
{
    public bool IsLoggedIn { get; init; }
    public bool IsInitialized { get; init; }

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
    public Dictionary<string, string>? Parameters { get; set; }
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
