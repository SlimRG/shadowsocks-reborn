using System;

namespace Shadowsocks
{
    /// <summary>
    /// Compatibility implementation of HttpServerUtility.UrlTokenEncode/UrlTokenDecode.
    /// The format is base64url without '=' padding plus one trailing digit containing
    /// the removed padding count; this is intentionally not ordinary Base64Url.
    /// </summary>
    public static class HttpServerUtilityUrlToken
    {
        public static string Encode(byte[] bytes)
        {
            if (bytes == null) throw new ArgumentNullException(nameof(bytes));
            if (bytes.Length == 0) return string.Empty;

            string base64 = Convert.ToBase64String(bytes);
            int end = base64.Length;
            while (end > 0 && base64[end - 1] == '=') end--;

            char[] result = new char[end + 1];
            for (int i = 0; i < end; i++)
            {
                char c = base64[i];
                result[i] = c == '+' ? '-' : c == '/' ? '_' : c;
            }
            result[end] = (char)('0' + (base64.Length - end));
            return new string(result);
        }

        public static byte[] Decode(string input)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));
            if (input.Length == 0) return Array.Empty<byte>();

            int padding = input[input.Length - 1] - '0';
            if (padding < 0 || padding > 2) throw new FormatException("Invalid URL token padding.");

            char[] chars = new char[input.Length - 1 + padding];
            for (int i = 0; i < input.Length - 1; i++)
            {
                char c = input[i];
                chars[i] = c == '-' ? '+' : c == '_' ? '/' : c;
            }
            for (int i = input.Length - 1; i < chars.Length; i++) chars[i] = '=';
            return Convert.FromBase64CharArray(chars, 0, chars.Length);
        }
    }
}
