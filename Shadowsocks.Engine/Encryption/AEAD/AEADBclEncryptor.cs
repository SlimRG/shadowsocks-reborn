using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using Shadowsocks.Encryption.Exception;

namespace Shadowsocks.Encryption.AEAD
{
    public sealed class AEADBclEncryptor : AEADEncryptor
    {
        private const int CIPHER_AES_GCM = 1;
        private const int CIPHER_CHACHA20_POLY1305 = 2;

        private static readonly Dictionary<string, EncryptorInfo> _ciphers = new Dictionary<string, EncryptorInfo>
        {
            {"aes-128-gcm", new EncryptorInfo(16, 16, 12, 16, CIPHER_AES_GCM)},
            {"aes-192-gcm", new EncryptorInfo(24, 24, 12, 16, CIPHER_AES_GCM)},
            {"aes-256-gcm", new EncryptorInfo(32, 32, 12, 16, CIPHER_AES_GCM)},
            {"chacha20-ietf-poly1305", new EncryptorInfo(32, 32, 12, 16, CIPHER_CHACHA20_POLY1305)},
        };

        private byte[] _encryptSubkey;
        private byte[] _decryptSubkey;
        private AesGcm _encryptAes;
        private AesGcm _decryptAes;
        private ChaCha20Poly1305 _encryptChaCha;
        private ChaCha20Poly1305 _decryptChaCha;
        private bool _disposed;

        public AEADBclEncryptor(string method, string password)
            : base(method, password)
        {
            _encryptSubkey = new byte[keyLen];
            _decryptSubkey = new byte[keyLen];
        }

        public static List<string> SupportedCiphers()
        {
            return new List<string>(_ciphers.Keys);
        }

        protected override Dictionary<string, EncryptorInfo> getCiphers()
        {
            return _ciphers;
        }

        public override void InitCipher(byte[] salt, bool isEncrypt, bool isUdp)
        {
            base.InitCipher(salt, isEncrypt, isUdp);

            byte[] subkey = isEncrypt ? _encryptSubkey : _decryptSubkey;
            DeriveSessionKey(isEncrypt ? _encryptSalt : _decryptSalt, _masterKey, subkey);

            switch (_cipher)
            {
                case CIPHER_AES_GCM:
                    if (isEncrypt)
                    {
                        _encryptAes?.Dispose();
                        _encryptAes = new AesGcm(subkey, tagLen);
                    }
                    else
                    {
                        _decryptAes?.Dispose();
                        _decryptAes = new AesGcm(subkey, tagLen);
                    }
                    break;

                case CIPHER_CHACHA20_POLY1305:
                    if (!ChaCha20Poly1305.IsSupported)
                    {
                        throw new PlatformNotSupportedException(
                            "ChaCha20-Poly1305 is not supported by this Windows version.");
                    }

                    if (isEncrypt)
                    {
                        _encryptChaCha?.Dispose();
                        _encryptChaCha = new ChaCha20Poly1305(subkey);
                    }
                    else
                    {
                        _decryptChaCha?.Dispose();
                        _decryptChaCha = new ChaCha20Poly1305(subkey);
                    }
                    break;

                default:
                    throw new NotSupportedException($"Encryption method '{_method}' is not supported.");
            }
        }

        public override void cipherEncrypt(byte[] plaintext, uint plen, byte[] ciphertext, ref uint clen)
        {
            int plainLength = checked((int)plen);
            if (ciphertext.Length < plainLength + tagLen)
            {
                throw new ArgumentException("Ciphertext buffer is too small.", nameof(ciphertext));
            }

            ReadOnlySpan<byte> plain = plaintext.AsSpan(0, plainLength);
            Span<byte> encrypted = ciphertext.AsSpan(0, plainLength);
            Span<byte> tag = ciphertext.AsSpan(plainLength, tagLen);

            try
            {
                switch (_cipher)
                {
                    case CIPHER_AES_GCM:
                        if (_encryptAes == null)
                            throw new InvalidOperationException("Encryption cipher is not initialized.");
                        _encryptAes.Encrypt(_encNonce, plain, encrypted, tag);
                        break;

                    case CIPHER_CHACHA20_POLY1305:
                        if (_encryptChaCha == null)
                            throw new InvalidOperationException("Encryption cipher is not initialized.");
                        _encryptChaCha.Encrypt(_encNonce, plain, encrypted, tag);
                        break;

                    default:
                        throw new NotSupportedException($"Encryption method '{_method}' is not supported.");
                }
            }
            catch (CryptographicException e)
            {
                throw new CryptoErrorException("AEAD encryption failed.", e);
            }

            clen = plen + (uint)tagLen;
        }

        public override void cipherDecrypt(byte[] ciphertext, uint clen, byte[] plaintext, ref uint plen)
        {
            int cipherLength = checked((int)clen);
            if (cipherLength < tagLen)
            {
                throw new CryptoErrorException("AEAD ciphertext is shorter than the authentication tag.");
            }

            int plainLength = cipherLength - tagLen;
            if (plaintext.Length < plainLength)
            {
                throw new ArgumentException("Plaintext buffer is too small.", nameof(plaintext));
            }

            ReadOnlySpan<byte> encrypted = ciphertext.AsSpan(0, plainLength);
            ReadOnlySpan<byte> tag = ciphertext.AsSpan(plainLength, tagLen);
            Span<byte> plain = plaintext.AsSpan(0, plainLength);

            try
            {
                switch (_cipher)
                {
                    case CIPHER_AES_GCM:
                        if (_decryptAes == null)
                            throw new InvalidOperationException("Decryption cipher is not initialized.");
                        _decryptAes.Decrypt(_decNonce, encrypted, tag, plain);
                        break;

                    case CIPHER_CHACHA20_POLY1305:
                        if (_decryptChaCha == null)
                            throw new InvalidOperationException("Decryption cipher is not initialized.");
                        _decryptChaCha.Decrypt(_decNonce, encrypted, tag, plain);
                        break;

                    default:
                        throw new NotSupportedException($"Encryption method '{_method}' is not supported.");
                }
            }
            catch (CryptographicException e)
            {
                throw new CryptoErrorException("AEAD authentication or decryption failed.", e);
            }

            plen = (uint)plainLength;
        }

        public override void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            _encryptAes?.Dispose();
            _decryptAes?.Dispose();
            _encryptChaCha?.Dispose();
            _decryptChaCha?.Dispose();
            _encryptAes = null;
            _decryptAes = null;
            _encryptChaCha = null;
            _decryptChaCha = null;

            if (_encryptSubkey != null)
                CryptographicOperations.ZeroMemory(_encryptSubkey);
            if (_decryptSubkey != null)
                CryptographicOperations.ZeroMemory(_decryptSubkey);
            if (_masterKey != null)
                CryptographicOperations.ZeroMemory(_masterKey);
        }
    }
}
