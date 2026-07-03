using System;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Spectre.Console;
using XboxPrefill.Api;
using XboxPrefill.Handlers;
using XboxPrefill.Settings;

namespace XboxPrefill.Test
{
    /// <summary>
    /// Covers the token-storage-at-rest fix: the AES-256-GCM key must be derived from a stable key file next
    /// to the account file (not the per-container hostname/socket secret), and an undecryptable stored token
    /// must self-heal into a fresh login instead of aborting <see cref="XboxAccountManager.LoadFromFile"/> with
    /// an uncaught <see cref="CryptographicException"/>. Uses the real <see cref="AppConfig.ConfigDir"/> since
    /// neither AppConfig nor XboxAccountManager accept a path override; storage.key and the account file are
    /// deleted before/after every test so runs don't collide with each other or leave residue.
    /// </summary>
    [Collection("XboxAccountFile")]
    public sealed class TokenStorageEncryptionTests : IDisposable
    {
        private static readonly string KeyPath = Path.Combine(AppConfig.ConfigDir, "storage.key");
        private static readonly string AccountPath = AppConfig.AccountSettingsStorePath;

        public TokenStorageEncryptionTests()
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
        public void Encrypt_ThenDecrypt_WithStableKeyFile_RoundTrips()
        {
            const string plaintext = "{\"refreshToken\":\"abc123\"}";

            var encrypted = TokenStorageEncryption.Encrypt(plaintext);
            var decrypted = TokenStorageEncryption.Decrypt(encrypted);

            Assert.Equal(plaintext, decrypted);
            Assert.True(File.Exists(KeyPath), "Encrypt should have created storage.key on first use.");
        }

        [Fact]
        public void EncryptDecrypt_WrongLengthKeyFile_RegeneratesValidKey()
        {
            // Simulates a torn/partial write (valid Base64, wrong length) - before the length
            // validation hardening, this would be silently accepted as short/malformed HKDF input key
            // material forever instead of being detected and regenerated.
            var shortKey = new byte[16];
            RandomNumberGenerator.Fill(shortKey);
            File.WriteAllText(KeyPath, Convert.ToBase64String(shortKey));

            var encrypted = TokenStorageEncryption.Encrypt("{\"refreshToken\":\"abc123\"}");
            var decrypted = TokenStorageEncryption.Decrypt(encrypted);

            Assert.Equal("{\"refreshToken\":\"abc123\"}", decrypted);
            var regenerated = Convert.FromBase64String(File.ReadAllText(KeyPath).Trim());
            Assert.Equal(32, regenerated.Length);
        }

        [Fact]
        public void LoadFromFile_KeyFileRotated_ReturnsFreshManagerAndDeletesStaleAccountFile()
        {
            var encrypted = TokenStorageEncryption.Encrypt("{\"refreshToken\":\"abc123\"}");
            File.WriteAllText(AccountPath, encrypted);

            // Rotate the key file to simulate a lost/regenerated key - the stored blob can no longer decrypt
            // under the new key, mirroring what happens today when a container is recreated.
            var rotatedKey = new byte[32];
            RandomNumberGenerator.Fill(rotatedKey);
            File.WriteAllText(KeyPath, Convert.ToBase64String(rotatedKey));

            var manager = XboxAccountManager.LoadFromFile(AnsiConsole.Console, new NullXboxAuthProvider());

            Assert.Null(manager.Account);
            Assert.False(File.Exists(AccountPath), "The undecryptable account file should have been deleted.");
        }

        [Fact]
        public void LoadFromFile_LegacyPoisonedBlob_ReturnsFreshManagerAndDeletesStaleAccountFile()
        {
            // An "ENC:"-prefixed blob that no key derived by this process can decrypt (random ciphertext/tag).
            // Against the pre-fix code (unguarded TokenStorageEncryption.Decrypt in LoadFromFile) this throws
            // CryptographicException and aborts XboxAccountManager construction - this test fails until the
            // catch(CryptographicException) guard is in place.
            var garbage = new byte[64];
            RandomNumberGenerator.Fill(garbage);
            File.WriteAllText(AccountPath, "ENC:" + Convert.ToBase64String(garbage));

            var manager = XboxAccountManager.LoadFromFile(AnsiConsole.Console, new NullXboxAuthProvider());

            Assert.Null(manager.Account);
            Assert.False(File.Exists(AccountPath), "The undecryptable account file should have been deleted.");
        }

        private sealed class NullXboxAuthProvider : IXboxAuthProvider
        {
            public Task PresentDeviceCodeAsync(string userCode, string verificationUri, CancellationToken cancellationToken = default)
                => Task.CompletedTask;

            public void CancelPendingRequest()
            {
            }
        }
    }
}
