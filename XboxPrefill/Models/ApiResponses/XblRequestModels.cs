namespace XboxPrefill.Models.ApiResponses
{
    /// <summary>Request body for <c>user.auth.xboxlive.com/user/authenticate</c>.</summary>
    public sealed class XblUserAuthRequest
    {
        [JsonPropertyName("Properties")]
        public XblUserAuthProperties Properties { get; set; }

        [JsonPropertyName("RelyingParty")]
        public string RelyingParty { get; set; } = "http://auth.xboxlive.com";

        [JsonPropertyName("TokenType")]
        public string TokenType { get; set; } = "JWT";
    }

    public sealed class XblUserAuthProperties
    {
        [JsonPropertyName("AuthMethod")]
        public string AuthMethod { get; set; } = "RPS";

        [JsonPropertyName("SiteName")]
        public string SiteName { get; set; } = "user.auth.xboxlive.com";

        [JsonPropertyName("RpsTicket")]
        public string RpsTicket { get; set; }
    }

    /// <summary>Request body for <c>device.auth.xboxlive.com/device/authenticate</c> (signed, ProofOfPossession).</summary>
    public sealed class XblDeviceAuthRequest
    {
        [JsonPropertyName("RelyingParty")]
        public string RelyingParty { get; set; } = "http://auth.xboxlive.com";

        [JsonPropertyName("TokenType")]
        public string TokenType { get; set; } = "JWT";

        [JsonPropertyName("Properties")]
        public XblDeviceAuthProperties Properties { get; set; }
    }

    public sealed class XblDeviceAuthProperties
    {
        [JsonPropertyName("AuthMethod")]
        public string AuthMethod { get; set; } = "ProofOfPossession";

        [JsonPropertyName("Id")]
        public string Id { get; set; }

        [JsonPropertyName("DeviceType")]
        public string DeviceType { get; set; } = "Win32";

        [JsonPropertyName("Version")]
        public string Version { get; set; } = "10.0.19041.0";

        [JsonPropertyName("ProofKey")]
        public ProofKeyJwk ProofKey { get; set; }
    }

    /// <summary>The P-256 public key (JWK) embedded as the device's proof key.</summary>
    public sealed class ProofKeyJwk
    {
        [JsonPropertyName("crv")]
        public string Crv { get; set; } = "P-256";

        [JsonPropertyName("alg")]
        public string Alg { get; set; } = "ES256";

        [JsonPropertyName("use")]
        public string Use { get; set; } = "sig";

        [JsonPropertyName("kty")]
        public string Kty { get; set; } = "EC";

        [JsonPropertyName("x")]
        public string X { get; set; }

        [JsonPropertyName("y")]
        public string Y { get; set; }
    }

    /// <summary>Request body for <c>xsts.auth.xboxlive.com/xsts/authorize</c>.</summary>
    public sealed class XstsAuthRequest
    {
        [JsonPropertyName("Properties")]
        public XstsAuthProperties Properties { get; set; }

        [JsonPropertyName("RelyingParty")]
        public string RelyingParty { get; set; }

        [JsonPropertyName("TokenType")]
        public string TokenType { get; set; } = "JWT";
    }

    public sealed class XstsAuthProperties
    {
        [JsonPropertyName("SandboxId")]
        public string SandboxId { get; set; } = "RETAIL";

        [SuppressMessage("Performance", "CA1819:Properties should not return arrays", Justification = "Direct mapping of XSTS JSON request.")]
        [JsonPropertyName("UserTokens")]
        public string[] UserTokens { get; set; }

        /// <summary>Device token. OMITTED entirely for the titlehub audience (when null); mandatory for the update audience.</summary>
        [JsonPropertyName("DeviceToken")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string DeviceToken { get; set; }
    }
}
