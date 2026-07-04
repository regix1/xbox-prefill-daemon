using System;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using XboxPrefill.Api;
using XboxPrefill.Handlers;
using XboxPrefill.Settings;
using Xunit;

namespace XboxPrefill.Test
{
    /// <summary>
    /// RC6 (session 20260703-221336-2070027597): HandleLogoutAsync deleted the account file but
    /// left storage.key on disk, so any later login could re-persist tokens the SAME key could
    /// still decrypt - making the erase-on-stop / "clear stored logins" policy incomplete. Logout
    /// must now delete both files. Touches the shared AppConfig.ConfigDir/AccountSettingsStorePath
    /// paths, so this runs in the same non-parallel collection as TokenStorageEncryptionTests /
    /// LogoutClearsAccountTests to avoid a file-in-use race.
    /// </summary>
    [Collection("XboxAccountFile")]
    public sealed class LogoutErasesStorageKeyTests : IDisposable
    {
        private static readonly string KeyPath = Path.Combine(AppConfig.ConfigDir, "storage.key");
        private static readonly string AccountPath = AppConfig.AccountSettingsStorePath;

        public LogoutErasesStorageKeyTests()
        {
            DeleteTestFiles();
        }

        public void Dispose()
        {
            DeleteTestFiles();
        }

        private static void DeleteTestFiles()
        {
            if (File.Exists(KeyPath))
            {
                File.Delete(KeyPath);
            }
            if (File.Exists(AccountPath))
            {
                File.Delete(AccountPath);
            }
        }

        [Fact]
        public async Task Logout_DeletesBothAccountFileAndStorageKey()
        {
            // Force-create storage.key (encrypting anything lazily creates it) and a plaintext
            // account file (migration-path content is fine - only its existence is asserted).
            TokenStorageEncryption.Encrypt("seed");
            File.WriteAllText(AccountPath, "{}");

            Assert.True(File.Exists(KeyPath), "Precondition: storage.key must exist before logout.");
            Assert.True(File.Exists(AccountPath), "Precondition: account file must exist before logout.");

            using var socketInterface = new SocketCommandInterface(
                Path.Combine(Path.GetTempPath(), $"xbox-logout-storage-key-{Guid.NewGuid():N}.sock"));

            var handleCommandAsync = typeof(SocketCommandInterface).GetMethod(
                "HandleCommandAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;

            var request = new CommandRequest { Id = "1", Type = "logout" };
            var response = await (Task<CommandResponse>)handleCommandAsync.Invoke(
                socketInterface, new object[] { request, CancellationToken.None })!;

            Assert.True(response.Success);
            // Before the fix: only AccountPath was deleted; storage.key survived.
            Assert.False(File.Exists(AccountPath), "Account file should be deleted after logout.");
            Assert.False(File.Exists(KeyPath), "storage.key should be deleted after logout.");
        }
    }
}
