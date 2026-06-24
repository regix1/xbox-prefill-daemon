using System;
using System.Security.Cryptography;
using XboxPrefill.Handlers;

namespace XboxPrefill.Test
{
    /// <summary>
    /// Golden / layout tests for <see cref="XblRequestSigner"/>. The Xbox request-signing protocol fails as a
    /// silent HTTP 403 when the signed-buffer layout or the Signature header layout is even one byte wrong, so
    /// these tests lock the EXACT bytes for a fixed timestamp + request, and prove the produced signature is a
    /// real P-256 / IEEE-P1363 signature over that buffer.
    /// </summary>
    public sealed class XblRequestSignerTests
    {
        // A fixed Windows-filetime timestamp (Int64). 0x01d51856b75ee000.
        private const long FixedFiletime = 132038524800000000L;

        // The exact buffer that must be hashed+signed for: ts=FixedFiletime, GET /device/authenticate, empty auth,
        // empty body. Layout: [00 00 00 01] [00] int64BE(ts) [00] "GET\0" "/device/authenticate\0" "\0" body "\0".
        private const string ExpectedBufferHex =
            "000000010001d51856b75ee00000474554002f6465766963652f61757468656e746963617465000000";

        private static byte[] HexToBytes(string hex)
        {
            var bytes = new byte[hex.Length / 2];
            for (int i = 0; i < bytes.Length; i++)
            {
                bytes[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
            }
            return bytes;
        }

        [Fact]
        public void SignatureBuffer_HasExactGoldenLayout()
        {
            byte[] buffer = XblRequestSigner.BuildSignatureBuffer(
                FixedFiletime, "GET", "/device/authenticate", string.Empty, Array.Empty<byte>());

            string actualHex = Convert.ToHexString(buffer).ToLowerInvariant();

            Assert.Equal(ExpectedBufferHex, actualHex);

            // Spell out the structural invariants in case the golden hex is ever regenerated.
            Assert.Equal(41, buffer.Length);
            Assert.Equal(new byte[] { 0x00, 0x00, 0x00, 0x01 }, buffer[0..4]); // policy version
            Assert.Equal(0x00, buffer[4]);                                      // separator
            Assert.Equal(HexToBytes("01d51856b75ee000"), buffer[5..13]);        // int64 BE timestamp
            Assert.Equal(0x00, buffer[13]);                                     // separator
            Assert.Equal(0x00, buffer[^1]);                                     // trailing null after body
        }

        [Fact]
        public void SignatureBuffer_BodyAndAuthArePlacedAndNullTerminated()
        {
            byte[] body = System.Text.Encoding.ASCII.GetBytes("{\"a\":1}");
            byte[] buffer = XblRequestSigner.BuildSignatureBuffer(
                FixedFiletime, "POST", "/device/authenticate", "XBL3.0 x=uhs;tok", body);

            // Independently reconstruct the expected layout to lock ordering: ... method\0 path\0 auth\0 body \0
            using var ms = new System.IO.MemoryStream();
            ms.Write(new byte[] { 0x00, 0x00, 0x00, 0x01 });
            ms.WriteByte(0x00);
            ms.Write(HexToBytes("01d51856b75ee000"));
            ms.WriteByte(0x00);
            void Z(string s) { var b = System.Text.Encoding.ASCII.GetBytes(s); ms.Write(b); ms.WriteByte(0x00); }
            Z("POST");
            Z("/device/authenticate");
            Z("XBL3.0 x=uhs;tok");
            ms.Write(body);
            ms.WriteByte(0x00);

            Assert.Equal(ms.ToArray(), buffer);
        }

        [Fact]
        public void SignatureHeader_Is76BytesAndVerifies()
        {
            using var signer = XblRequestSigner.CreateNew();

            string headerBase64 = signer.SignAt(FixedFiletime, "GET", "/device/authenticate", string.Empty, Array.Empty<byte>());
            byte[] header = Convert.FromBase64String(headerBase64);

            // Decoded header layout: [4 policy] + [8 int64BE ts] + [64 r||s IEEE-P1363] = 76 bytes.
            Assert.Equal(76, header.Length);
            Assert.Equal(new byte[] { 0x00, 0x00, 0x00, 0x01 }, header[0..4]);
            Assert.Equal(HexToBytes("01d51856b75ee000"), header[4..12]);

            byte[] signature = header[12..76];
            Assert.Equal(64, signature.Length); // 32-byte r || 32-byte s, NOT DER

            // The signature must verify over the EXACT golden buffer, using SHA256 + IEEE-P1363.
            byte[] signedBuffer = HexToBytes(ExpectedBufferHex);
            bool verified = signer.PublicKey.VerifyData(
                signedBuffer, signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
            Assert.True(verified, "Signature did not verify over the golden buffer with SHA256/IEEE-P1363.");
        }

        [Fact]
        public void ProofKey_CoordinatesAre32BytesBase64Url()
        {
            using var signer = XblRequestSigner.CreateNew();
            var jwk = signer.GetProofKey();

            Assert.Equal("P-256", jwk.Crv);
            Assert.Equal("EC", jwk.Kty);

            // base64url (no padding): must contain no '+', '/' or '=' and decode to exactly 32 bytes each.
            Assert.DoesNotContain('+', jwk.X);
            Assert.DoesNotContain('/', jwk.X);
            Assert.DoesNotContain('=', jwk.X);

            Assert.Equal(32, Base64UrlDecode(jwk.X).Length);
            Assert.Equal(32, Base64UrlDecode(jwk.Y).Length);
        }

        private static byte[] Base64UrlDecode(string value)
        {
            string s = value.Replace('-', '+').Replace('_', '/');
            switch (s.Length % 4)
            {
                case 2: s += "=="; break;
                case 3: s += "="; break;
            }
            return Convert.FromBase64String(s);
        }
    }
}
