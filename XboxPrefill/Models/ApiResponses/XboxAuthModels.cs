namespace XboxPrefill.Models.ApiResponses
{
    /// <summary>
    /// Response from the MSA device-code request (<c>oauth20_connect.srf</c>).
    /// </summary>
    public sealed class DeviceCodeResponse
    {
        [JsonPropertyName("user_code")]
        public string UserCode { get; set; }

        [JsonPropertyName("device_code")]
        public string DeviceCode { get; set; }

        [JsonPropertyName("verification_uri")]
        public string VerificationUri { get; set; }

        [JsonPropertyName("interval")]
        public int Interval { get; set; }

        [JsonPropertyName("expires_in")]
        public int ExpiresIn { get; set; }
    }

    /// <summary>
    /// Response from the MSA token endpoint (<c>oauth20_token.srf</c>) while polling the
    /// device-code grant, or when refreshing. Carries the <c>authorization_pending</c> /
    /// <c>slow_down</c> error codes during polling.
    /// </summary>
    public sealed class MsaTokenResponse
    {
        [JsonPropertyName("access_token")]
        public string AccessToken { get; set; }

        [JsonPropertyName("refresh_token")]
        public string RefreshToken { get; set; }

        [JsonPropertyName("expires_in")]
        public int ExpiresIn { get; set; }

        [JsonPropertyName("token_type")]
        public string TokenType { get; set; }

        [JsonPropertyName("error")]
        public string Error { get; set; }
    }

    /// <summary>
    /// Response from <c>user.auth.xboxlive.com/user/authenticate</c> and
    /// <c>device.auth.xboxlive.com/device/authenticate</c>. Both return a JWT in <c>Token</c>.
    /// </summary>
    public sealed class XblAuthResponse
    {
        [JsonPropertyName("Token")]
        public string Token { get; set; }

        [JsonPropertyName("IssueInstant")]
        public DateTime IssueInstant { get; set; }

        [JsonPropertyName("NotAfter")]
        public DateTime NotAfter { get; set; }
    }

    /// <summary>
    /// Response from <c>xsts.auth.xboxlive.com/xsts/authorize</c>. The authorization header is
    /// built as <c>XBL3.0 x={uhs};{Token}</c> using the user-hash from <see cref="DisplayClaims"/>.
    /// </summary>
    public sealed class XstsTokenResponse
    {
        [JsonPropertyName("Token")]
        public string Token { get; set; }

        [JsonPropertyName("IssueInstant")]
        public DateTime IssueInstant { get; set; }

        [JsonPropertyName("NotAfter")]
        public DateTime NotAfter { get; set; }

        [JsonPropertyName("DisplayClaims")]
        public XstsDisplayClaims DisplayClaims { get; set; }
    }

    public sealed class XstsDisplayClaims
    {
        [SuppressMessage("Performance", "CA1819:Properties should not return arrays", Justification = "Direct mapping of XSTS JSON response.")]
        [JsonPropertyName("xui")]
        public XstsUserInfo[] Xui { get; set; }
    }

    public sealed class XstsUserInfo
    {
        /// <summary>User hash, used in the <c>XBL3.0 x={uhs};{token}</c> authorization header.</summary>
        [JsonPropertyName("uhs")]
        public string Uhs { get; set; }

        /// <summary>Xbox user id (xuid), used for titlehub enumeration.</summary>
        [JsonPropertyName("xid")]
        public string Xid { get; set; }
    }
}
