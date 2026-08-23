using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using NLog;
using Shadowsocks.Controller;
using Shadowsocks.Encryption.CircularBuffer;
using Shadowsocks.Controller.Service;
using Shadowsocks.Encryption.Exception;

namespace Shadowsocks.Encryption.AEAD
{
    public abstract class AEADEncryptor
        : EncryptorBase
    {
        private static readonly Logger logger = LogManager.GetCurrentClassLogger();
        // We are using the same SaltLength and KeyLength
        private const string Info = "ss-subkey";
        private static readonly byte[] InfoBytes = Encoding.ASCII.GetBytes(Info);

        // for UDP only
        private static readonly byte[] s_udpTempBuffer = new byte[65536];

        // every connection should create its own buffer
        private readonly ByteCircularBuffer _encCircularBuffer = new(MaxInputSize * 2);
        private readonly ByteCircularBuffer _decCircularBuffer = new(MaxInputSize * 2);

        public const int ChunkLengthBytes = 2;
        public const uint ChunkLengthMask = 0x3FFFu;

        protected string MethodName { get; private set; }
        protected int CipherType { get; private set; }
        // internal name in the crypto library
        protected byte[] MasterKey { get; private set; }
        protected int KeyLength { get; private set; }
        protected int SaltLength { get; private set; }
        protected int TagLength { get; private set; }
        protected int NonceLength { get; private set; }

        protected byte[] EncryptSalt { get; private set; }
        protected byte[] DecryptSalt { get; private set; }

        private readonly object _nonceIncrementLock = new();
        protected byte[] EncryptNonce { get; private set; }
        protected byte[] DecryptNonce { get; private set; }
        // Is first packet
        private bool _decryptSaltReceived;
        private bool _encryptSaltSent;

        // Is first chunk(tcp request)
        private bool _tcpRequestSent;

        public AEADEncryptor(string method, string password)
            : base(method, password)
        {
            InitEncryptorInfo(method);
            InitKey(password);
            // Initialize all-zero nonce for each connection
            EncryptNonce = new byte[NonceLength];
            DecryptNonce = new byte[NonceLength];
        }

        protected abstract IReadOnlyDictionary<string, EncryptorInfo> GetCiphers();

        protected void InitEncryptorInfo(string method)
        {
            method = method.ToLowerInvariant();
            MethodName = method;
            IReadOnlyDictionary<string, EncryptorInfo> ciphers = GetCiphers();
            EncryptorInfo cipherInfo = ciphers[MethodName];
            CipherType = cipherInfo.Type;
            if (CipherType == 0)
            {
                throw new NotSupportedException($"Encryption method '{MethodName}' is not supported.");
            }
            KeyLength = cipherInfo.KeySize;
            SaltLength = cipherInfo.SaltSize;
            TagLength = cipherInfo.TagSize;
            NonceLength = cipherInfo.NonceSize;
        }

        protected void InitKey(string password)
        {
            byte[] passbuf = Encoding.UTF8.GetBytes(password);
            // The master key belongs to this encryptor instance. Keeping it per-instance
            // prevents concurrent connections to servers with different passwords from
            // overwriting each other's key material.
            MasterKey = new byte[KeyLength];
            DeriveKey(passbuf, MasterKey, KeyLength);
        }

        public static void DeriveKey(byte[] password, byte[] key, int keylen)
        {
            byte[] result = new byte[password.Length + Md5Length];
            int i = 0;
            byte[] md5sum = null;
            while (i < keylen)
            {
                if (i == 0)
                {
                    // Shadowsocks EVP_BytesToKey compatibility requires MD5; changing it breaks the protocol.
#pragma warning disable CA5351
                    md5sum = MD5.HashData(password);
#pragma warning restore CA5351
                }
                else
                {
                    Array.Copy(md5sum, 0, result, 0, Md5Length);
                    Array.Copy(password, 0, result, Md5Length, password.Length);
#pragma warning disable CA5351
                    md5sum = MD5.HashData(result);
#pragma warning restore CA5351
                }
                Array.Copy(md5sum, 0, key, i, Math.Min(Md5Length, keylen - i));
                i += Md5Length;
            }
        }

        public void DeriveSessionKey(byte[] salt, byte[] masterKey, byte[] sessionKey)
        {
            HKDF.DeriveKey(
                HashAlgorithmName.SHA1,
                masterKey.AsSpan(0, KeyLength),
                sessionKey.AsSpan(0, KeyLength),
                salt.AsSpan(0, SaltLength),
                InfoBytes);
        }

        protected void IncrementNonce(bool isEncrypt)
        {
            lock (_nonceIncrementLock)
            {
                byte[] nonce = isEncrypt ? EncryptNonce : DecryptNonce;
                // Shadowsocks AEAD treats the nonce as a little-endian unsigned integer.
                for (int i = 0; i < NonceLength; i++)
                {
                    nonce[i]++;
                    if (nonce[i] != 0)
                        break;
                }
            }
        }

        public virtual void InitCipher(byte[] salt, bool isEncrypt, bool isUdp)
        {
            if (isEncrypt)
            {
                EncryptSalt = new byte[SaltLength];
                Array.Copy(salt, EncryptSalt, SaltLength);
            }
            else
            {
                DecryptSalt = new byte[SaltLength];
                Array.Copy(salt, DecryptSalt, SaltLength);
            }
            logger.Dump("Salt", salt, SaltLength);
        }

        public static void randBytes(byte[] buf, int length) { RNG.GetBytes(buf, length); }

        public abstract void cipherEncrypt(byte[] plaintext, uint plen, byte[] ciphertext, ref uint clen);

        public abstract void cipherDecrypt(byte[] ciphertext, uint clen, byte[] plaintext, ref uint plen);

        #region TCP

        public override void Encrypt(byte[] buf, int length, byte[] outbuf, out int outlength)
        {
            Debug.Assert(_encCircularBuffer != null, "_encCircularBuffer != null");

            _encCircularBuffer.Put(buf, 0, length);
            outlength = 0;
            logger.Trace("---Start Encryption");
            if (!_encryptSaltSent)
            {
                _encryptSaltSent = true;
                // Generate salt
                byte[] saltBytes = new byte[SaltLength];
                randBytes(saltBytes, SaltLength);
                InitCipher(saltBytes, true, false);
                Array.Copy(saltBytes, 0, outbuf, 0, SaltLength);
                outlength = SaltLength;
                logger.Trace($"_encryptSaltSent outlength {outlength}");
            }

            if (!_tcpRequestSent)
            {
                _tcpRequestSent = true;
                // The first TCP request
                int encAddrBufLength;
                byte[] encAddrBufBytes = new byte[AddrBufLength + TagLength * 2 + ChunkLengthBytes];
                byte[] addrBytes = _encCircularBuffer.Read(AddrBufLength);
                ChunkEncrypt(addrBytes, AddrBufLength, encAddrBufBytes, out encAddrBufLength);
                Debug.Assert(encAddrBufLength == AddrBufLength + TagLength * 2 + ChunkLengthBytes);
                Array.Copy(encAddrBufBytes, 0, outbuf, outlength, encAddrBufLength);
                outlength += encAddrBufLength;
                logger.Trace($"_tcpRequestSent outlength {outlength}");
            }

            // handle other chunks
            while (true)
            {
                uint bufSize = (uint)_encCircularBuffer.Size;
                if (bufSize <= 0) return;
                var chunklength = (int)Math.Min(bufSize, ChunkLengthMask);
                byte[] chunkBytes = _encCircularBuffer.Read(chunklength);
                int encChunkLength;
                byte[] encChunkBytes = new byte[chunklength + TagLength * 2 + ChunkLengthBytes];
                ChunkEncrypt(chunkBytes, chunklength, encChunkBytes, out encChunkLength);
                Debug.Assert(encChunkLength == chunklength + TagLength * 2 + ChunkLengthBytes);
                Buffer.BlockCopy(encChunkBytes, 0, outbuf, outlength, encChunkLength);
                outlength += encChunkLength;
                logger.Trace("chunks enc outlength " + outlength);
                // check if we have enough space for outbuf
                if (outlength + TransportBufferSizes.ChunkOverheadSize > TransportBufferSizes.BufferSize)
                {
                    logger.Trace("enc outbuf almost full, giving up");
                    return;
                }
                bufSize = (uint)_encCircularBuffer.Size;
                if (bufSize <= 0)
                {
                    logger.Trace("No more data to encrypt, leaving");
                    return;
                }
            }
        }


        public override void Decrypt(byte[] buf, int length, byte[] outbuf, out int outlength)
        {
            Debug.Assert(_decCircularBuffer != null, "_decCircularBuffer != null");
            int bufSize;
            outlength = 0;
            // drop all into buffer
            _decCircularBuffer.Put(buf, 0, length);

            logger.Trace("---Start Decryption");
            if (!_decryptSaltReceived)
            {
                bufSize = _decCircularBuffer.Size;
                // check if we get the leading salt
                if (bufSize <= SaltLength)
                {
                    // need more
                    return;
                }
                _decryptSaltReceived = true;
                byte[] salt = _decCircularBuffer.Read(SaltLength);
                InitCipher(salt, false, false);
                logger.Trace("get salt len " + SaltLength);
            }

            // handle chunks
            while (true)
            {
                bufSize = _decCircularBuffer.Size;
                // check if we have any data
                if (bufSize <= 0)
                {
                    logger.Trace("No data in _decCircularBuffer");
                    return;
                }

                // first get chunk length
                if (bufSize <= ChunkLengthBytes + TagLength)
                {
                    // so we only have chunk length and its tag?
                    return;
                }

                #region Chunk Decryption

                byte[] encLenBytes = _decCircularBuffer.Peek(ChunkLengthBytes + TagLength);
                uint decChunkLenLength = 0;
                byte[] decChunkLenBytes = new byte[ChunkLengthBytes];
                // try to dec chunk len
                cipherDecrypt(encLenBytes, ChunkLengthBytes + (uint)TagLength, decChunkLenBytes, ref decChunkLenLength);
                Debug.Assert(decChunkLenLength == ChunkLengthBytes);
                // finally we get the real chunk len
                ushort chunkLen = (ushort)IPAddress.NetworkToHostOrder((short)BitConverter.ToUInt16(decChunkLenBytes, 0));
                if (chunkLen > ChunkLengthMask)
                {
                    // we get invalid chunk
                    logger.Error($"Invalid chunk length: {chunkLen}");
                    throw new CryptoErrorException();
                }
                logger.Trace("Get the real chunk len:" + chunkLen);
                bufSize = _decCircularBuffer.Size;
                if (bufSize < ChunkLengthBytes + TagLength /* we haven't remove them */+ chunkLen + TagLength)
                {
                    logger.Trace("No more data to decrypt one chunk");
                    return;
                }
                IncrementNonce(false);

                // we have enough data to decrypt one chunk
                // drop chunk len and its tag from buffer
                _decCircularBuffer.Skip(ChunkLengthBytes + TagLength);
                byte[] encChunkBytes = _decCircularBuffer.Read(chunkLen + TagLength);
                byte[] decChunkBytes = new byte[chunkLen];
                uint decChunkLen = 0;
                cipherDecrypt(encChunkBytes, chunkLen + (uint)TagLength, decChunkBytes, ref decChunkLen);
                Debug.Assert(decChunkLen == chunkLen);
                IncrementNonce(false);

                #endregion

                // output to outbuf
                Buffer.BlockCopy(decChunkBytes, 0, outbuf, outlength, (int)decChunkLen);
                outlength += (int)decChunkLen;
                logger.Trace("aead dec outlength " + outlength);
                if (outlength + 100 > TransportBufferSizes.BufferSize)
                {
                    logger.Trace("dec outbuf almost full, giving up");
                    return;
                }
                bufSize = _decCircularBuffer.Size;
                // check if we already done all of them
                if (bufSize <= 0)
                {
                    logger.Trace("No data in _decCircularBuffer, already all done");
                    return;
                }
            }
        }

        #endregion

        #region UDP

        public override void EncryptUDP(byte[] buf, int length, byte[] outbuf, out int outlength)
        {
            // Generate salt
            randBytes(outbuf, SaltLength);
            InitCipher(outbuf, true, true);
            uint olen = 0;
            lock (s_udpTempBuffer)
            {
                cipherEncrypt(buf, (uint)length, s_udpTempBuffer, ref olen);
                Debug.Assert(olen == length + TagLength);
                Buffer.BlockCopy(s_udpTempBuffer, 0, outbuf, SaltLength, (int)olen);
                outlength = (int)(SaltLength + olen);
            }
        }

        public override void DecryptUDP(byte[] buf, int length, byte[] outbuf, out int outlength)
        {
            InitCipher(buf, false, true);
            uint olen = 0;
            lock (s_udpTempBuffer)
            {
                // copy remaining data to first pos
                Buffer.BlockCopy(buf, SaltLength, buf, 0, length - SaltLength);
                cipherDecrypt(buf, (uint)(length - SaltLength), s_udpTempBuffer, ref olen);
                Buffer.BlockCopy(s_udpTempBuffer, 0, outbuf, 0, (int)olen);
                outlength = (int)olen;
            }
        }

        #endregion

        // we know the plaintext length before encryption, so we can do it in one operation
        private void ChunkEncrypt(byte[] plaintext, int plainLen, byte[] ciphertext, out int cipherLen)
        {
            if (plainLen > ChunkLengthMask)
            {
                logger.Error("enc chunk too big");
                throw new CryptoErrorException();
            }

            // encrypt len
            byte[] encLenBytes = new byte[ChunkLengthBytes + TagLength];
            uint encChunkLenLength = 0;
            byte[] lenbuf = BitConverter.GetBytes((ushort)IPAddress.HostToNetworkOrder((short)plainLen));
            cipherEncrypt(lenbuf, ChunkLengthBytes, encLenBytes, ref encChunkLenLength);
            Debug.Assert(encChunkLenLength == ChunkLengthBytes + TagLength);
            IncrementNonce(true);

            // encrypt corresponding data
            byte[] encBytes = new byte[plainLen + TagLength];
            uint encBufLength = 0;
            cipherEncrypt(plaintext, (uint)plainLen, encBytes, ref encBufLength);
            Debug.Assert(encBufLength == plainLen + TagLength);
            IncrementNonce(true);

            // construct outbuf
            Array.Copy(encLenBytes, 0, ciphertext, 0, (int)encChunkLenLength);
            Buffer.BlockCopy(encBytes, 0, ciphertext, (int)encChunkLenLength, (int)encBufLength);
            cipherLen = (int)(encChunkLenLength + encBufLength);
        }
    }
}
