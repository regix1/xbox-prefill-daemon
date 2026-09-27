namespace XboxPrefill.Models.ApiResponses
{
    public sealed class XboxProfileResponse
    {
        [JsonPropertyName("profileUsers")]
#pragma warning disable CA2227 // System.Text.Json replaces Xbox API response collections.
        public List<XboxProfileUser> Users { get; set; }
#pragma warning restore CA2227
    }

    public sealed class XboxProfileUser
    {
        [JsonPropertyName("settings")]
#pragma warning disable CA2227 // System.Text.Json replaces Xbox API response collections.
        public List<XboxProfileSetting> Settings { get; set; }
#pragma warning restore CA2227
    }

    public sealed class XboxProfileSetting
    {
        [JsonPropertyName("id")]
        public string Id { get; set; }

        [JsonPropertyName("value")]
        public string Value { get; set; }
    }
}
