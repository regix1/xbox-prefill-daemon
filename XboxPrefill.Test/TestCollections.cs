using Xunit;

namespace XboxPrefill.Test
{
    /// <summary>
    /// TokenStorageEncryptionTests and LogoutClearsAccountTests both read/write the shared on-disk
    /// account store file (AppConfig.AccountSettingsStorePath). xunit runs different test classes in
    /// parallel by default, and two of them touching that file at the same time throws IOException
    /// ("used by another process"). Forcing them into one non-parallel collection avoids the race.
    /// </summary>
    [CollectionDefinition("XboxAccountFile", DisableParallelization = true)]
    public sealed class XboxAccountFileCollection
    {
    }
}
