namespace XboxPrefill
{
    [JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata, WriteIndented = true)]
    // Persisted session + collections
    [JsonSerializable(typeof(XboxAccount))]
    [JsonSerializable(typeof(List<string>))]
    [JsonSerializable(typeof(Dictionary<string, HashSet<string>>))]
    // MSA device-code flow
    [JsonSerializable(typeof(DeviceCodeResponse))]
    [JsonSerializable(typeof(MsaTokenResponse))]
    // XBL auth chain (requests + responses)
    [JsonSerializable(typeof(XblUserAuthRequest))]
    [JsonSerializable(typeof(XblDeviceAuthRequest))]
    [JsonSerializable(typeof(XstsAuthRequest))]
    [JsonSerializable(typeof(XblAuthResponse))]
    [JsonSerializable(typeof(XstsTokenResponse))]
    // Catalog + package resolution
    [JsonSerializable(typeof(TitleHubResponse))]
    [JsonSerializable(typeof(DisplayCatalogResponse))]
    [JsonSerializable(typeof(GetBasePackageResponse))]
    // Internal models (registered for completeness; serialized only if ever persisted/emitted)
    [JsonSerializable(typeof(PackageManifest))]
    [JsonSerializable(typeof(QueuedRequest))]
    internal sealed partial class SerializationContext : JsonSerializerContext
    {
    }
}
