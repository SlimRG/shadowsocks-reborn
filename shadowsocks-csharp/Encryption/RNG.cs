using System;
using System.Security.Cryptography;

namespace Shadowsocks.Encryption
{
    public static class RNG
    {
        private static RandomNumberGenerator _rng = null;

        public static void Init()
        {
            _rng = _rng ?? RandomNumberGenerator.Create();
        }

        public static void Close()
        {
            _rng?.Dispose();
            _rng = null;
        }

        public static void Reload()
        {
            Close();
            Init();
        }

        public static void GetBytes(byte[] buf)
        {
            GetBytes(buf, buf.Length);
        }

        public static void GetBytes(byte[] buf, int len)
        {
            if (_rng == null) Init();
            if (buf == null) throw new ArgumentNullException(nameof(buf));
            if ((uint)len > (uint)buf.Length) throw new ArgumentOutOfRangeException(nameof(len));
            _rng.GetBytes(buf.AsSpan(0, len));
        }
    }
}