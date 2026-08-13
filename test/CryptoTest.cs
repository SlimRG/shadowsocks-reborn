using System;
using System.Security.Cryptography;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Shadowsocks.Encryption;
using Shadowsocks.Encryption.AEAD;

namespace Shadowsocks.Test
{
    [TestClass]
    public class CryptoTest
    {
        private static readonly byte[] Plaintext = Encoding.ASCII.GetBytes("Shadowsocks BCL AEAD test vector.");

        [TestMethod]
        public void Aes128GcmMatchesKnownVector()
        {
            AssertCipherVector(
                "aes-128-gcm",
                16,
                "bba8aa0b54856cc1a4b63f7e8970fbf9b6e66699924451cda14fea4a140c83f76ea29a8f2cb2749f8773bf9a63e6b34e00");
        }

        [TestMethod]
        public void Aes192GcmMatchesKnownVector()
        {
            AssertCipherVector(
                "aes-192-gcm",
                24,
                "2b528572ee8a4f9638e4f59dfbd9b57d00e7a6c6924d0f4ee22f54b61dc4533b3d57bd9eb8329953e5f314b3fa60a03410");
        }

        [TestMethod]
        public void Aes256GcmMatchesKnownVector()
        {
            AssertCipherVector(
                "aes-256-gcm",
                32,
                "b3b8c1facbe8751d4d7ce600f6cd5fa9cd8273d3e48139b2eb4d2ae7876d819d12776ded0d6194192833f2a25b12113581");
        }

        [TestMethod]
        public void ChaCha20Poly1305MatchesKnownVector()
        {
            if (!ChaCha20Poly1305.IsSupported)
            {
                Assert.Inconclusive("ChaCha20-Poly1305 is not supported by this Windows version.");
            }

            AssertCipherVector(
                "chacha20-ietf-poly1305",
                32,
                "2977565a6f87f83abd2a2e01a0524eb0f30a977d31e52368cf4b1491f3ea4d8a583f7dc40bba501dd5bfb38aa5734f2f00");
        }

        [TestMethod]
        public void FactoryDoesNotRegisterXChaCha()
        {
            string registered = EncryptorFactory.DumpRegisteredEncryptor();
            Assert.IsFalse(registered.Contains("xchacha20-ietf-poly1305", StringComparison.Ordinal));
            Assert.IsTrue(registered.Contains("aes-256-gcm=>AEADBclEncryptor", StringComparison.Ordinal));
            Assert.IsTrue(registered.Contains("chacha20-ietf-poly1305=>AEADBclEncryptor", StringComparison.Ordinal));
        }

        private static void AssertCipherVector(string method, int saltLength, string expectedHex)
        {
            using var encryptor = new AEADBclEncryptor(method, "test-password");
            byte[] salt = new byte[saltLength];
            for (int i = 0; i < salt.Length; i++)
                salt[i] = (byte)i;

            encryptor.InitCipher(salt, true, true);
            byte[] ciphertext = new byte[Plaintext.Length + 16];
            uint ciphertextLength = 0;
            encryptor.cipherEncrypt(Plaintext, (uint)Plaintext.Length, ciphertext, ref ciphertextLength);

            Assert.AreEqual(ciphertext.Length, (int)ciphertextLength);
            Assert.AreEqual(expectedHex, Convert.ToHexString(ciphertext).ToLowerInvariant());

            encryptor.InitCipher(salt, false, true);
            byte[] decrypted = new byte[Plaintext.Length];
            uint plaintextLength = 0;
            encryptor.cipherDecrypt(ciphertext, ciphertextLength, decrypted, ref plaintextLength);

            Assert.AreEqual(Plaintext.Length, (int)plaintextLength);
            CollectionAssert.AreEqual(Plaintext, decrypted);
        }
    }
}
