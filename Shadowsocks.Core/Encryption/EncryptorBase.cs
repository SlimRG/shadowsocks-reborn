namespace Shadowsocks.Encryption
{
    public class EncryptorInfo
    {
        public int KeySize { get; set; }
        public int IvSize { get; set; }
        public int SaltSize { get; set; }
        public int TagSize { get; set; }
        public int NonceSize { get; set; }
        public int Type { get; set; }
        public string InnerLibName { get; set; }

        // For those who make use of internal crypto method name
        // e.g. mbed TLS

        #region Stream ciphers

        public EncryptorInfo(string innerLibName, int keySize, int ivSize, int type)
        {
            this.KeySize = keySize;
            this.IvSize = ivSize;
            this.Type = type;
            this.InnerLibName = innerLibName;
        }

        public EncryptorInfo(int keySize, int ivSize, int type)
        {
            this.KeySize = keySize;
            this.IvSize = ivSize;
            this.Type = type;
            this.InnerLibName = string.Empty;
        }

        #endregion

        #region AEAD ciphers

        public EncryptorInfo(string innerLibName, int keySize, int saltSize, int nonceSize, int tagSize, int type)
        {
            this.KeySize = keySize;
            this.SaltSize = saltSize;
            this.NonceSize = nonceSize;
            this.TagSize = tagSize;
            this.Type = type;
            this.InnerLibName = innerLibName;
        }

        public EncryptorInfo(int keySize, int saltSize, int nonceSize, int tagSize, int type)
        {
            this.KeySize = keySize;
            this.SaltSize = saltSize;
            this.NonceSize = nonceSize;
            this.TagSize = tagSize;
            this.Type = type;
            this.InnerLibName = string.Empty;
        }

        #endregion
    }

    public abstract class EncryptorBase
        : IEncryptor
    {
        public const int MaxInputSize = 32768;

        public const int MaxDomainLength = 255;
        public const int AddressPortLength = 2;
        public const int AddressTypeLength = 1;

        public const int AddressTypeIPv4 = 0x01;
        public const int AddressTypeDomain = 0x03;
        public const int AddressTypeIPv6 = 0x04;

        public const int Md5Length = 16;

        protected EncryptorBase(string method, string password)
        {
            Method = method;
            Password = password;
        }

        protected string Method { get; }
        protected string Password { get; }

        public abstract void Encrypt(byte[] buf, int length, byte[] outbuf, out int outlength);

        public abstract void Decrypt(byte[] buf, int length, byte[] outbuf, out int outlength);

        public abstract void EncryptUDP(byte[] buf, int length, byte[] outbuf, out int outlength);

        public abstract void DecryptUDP(byte[] buf, int length, byte[] outbuf, out int outlength);

        public abstract void Dispose();

        public int AddrBufLength { get; set; } = -1;
    }
}
