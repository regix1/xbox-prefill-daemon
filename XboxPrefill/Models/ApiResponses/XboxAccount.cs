namespace XboxPrefill.Models.ApiResponses
{
    /// <summary>
    /// The persisted Xbox account session. Stored encrypted at <see cref="Settings.AppConfig.AccountSettingsStorePath"/>.
    /// Holds the long-lived MSA refresh token, the stable device identity (its ECDSA private key, so the
    /// signer survives restarts), and the most recently minted XSTS tokens for both relying parties.
    /// </summary>
    public sealed class XboxAccount
    {
        /// <summary>MSA refresh token. Long-lived, used to re-mint access tokens without a fresh device-code login.</summary>
        [JsonPropertyName("refresh_token")]
        public string RefreshToken { get; set; }

        /// <summary>
        /// UTC timestamp at which the current <see cref="RefreshToken"/> was issued / last rotated. The MSA
        /// refresh token has a ~90-day sliding lifetime, so this stamp + 90 days is the true re-login bound.
        /// Re-stamped on every device-code login and every rolling refresh.
        /// </summary>
        [JsonPropertyName("refresh_token_issued_at")]
        public DateTime? RefreshTokenIssuedUtc { get; set; }

        /// <summary>Gamertag / display name, captured for the UI.</summary>
        [JsonPropertyName("displayName")]
        public string DisplayName { get; set; }

        /// <summary>Xbox user id (xuid), used for titlehub enumeration.</summary>
        [JsonPropertyName("xuid")]
        public string Xuid { get; set; }

        /// <summary>
        /// The device identity ECDSA private key, exported as PKCS#8 and base64-encoded. Reused for every
        /// signed request so the device token stays valid. Generated once on first login.
        /// </summary>
        [JsonPropertyName("device_key_pkcs8")]
        public string DeviceKeyPkcs8 { get; set; }

        /// <summary>XSTS token for the titlehub relying party (<c>http://xboxlive.com</c>).</summary>
        [JsonPropertyName("xboxlive_token")]
        public string XboxLiveToken { get; set; }

        /// <summary>User hash for the titlehub relying party.</summary>
        [JsonPropertyName("xboxlive_uhs")]
        public string XboxLiveUhs { get; set; }

        /// <summary>Expiry of <see cref="XboxLiveToken"/>.</summary>
        [JsonPropertyName("xboxlive_expires_at")]
        public DateTime XboxLiveExpiresAt { get; set; }

        /// <summary>XSTS token for the package relying party (<c>http://update.xboxlive.com</c>), device-bearing.</summary>
        [JsonPropertyName("update_token")]
        public string UpdateToken { get; set; }

        /// <summary>User hash for the package relying party.</summary>
        [JsonPropertyName("update_uhs")]
        public string UpdateUhs { get; set; }

        /// <summary>Expiry of <see cref="UpdateToken"/>.</summary>
        [JsonPropertyName("update_expires_at")]
        public DateTime UpdateExpiresAt { get; set; }
    }
}
