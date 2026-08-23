using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using Shadowsocks.Encryption.AEAD;
using Shadowsocks.Encryption.Stream;

namespace Shadowsocks.Encryption
{
    public static class EncryptorFactory
    {
        private static readonly Dictionary<string, Type> _registeredEncryptors = new();

        private static readonly Type[] ConstructorTypes = { typeof(string), typeof(string) };

        static EncryptorFactory()
        {
            foreach (string method in AEADBclEncryptor.SupportedCiphers())
            {
                _registeredEncryptors.Add(method, typeof(AEADBclEncryptor));
            }

            foreach (string method in PlainEncryptor.SupportedCiphers())
            {
                _registeredEncryptors.Add(method, typeof(PlainEncryptor));
            }
        }

        public static IEncryptor GetEncryptor(string method, string password)
        {
            if (string.IsNullOrEmpty(method))
            {
                method = Model.Server.DefaultMethod;
            }

            method = method.ToLowerInvariant();
            if (!_registeredEncryptors.TryGetValue(method, out Type t))
            {
                throw new NotSupportedException($"Encryption method '{method}' is not supported.");
            }

            ConstructorInfo c = t.GetConstructor(ConstructorTypes);
            if (c is null) throw new InvalidOperationException($"Encryptor type '{t.FullName}' does not expose the expected constructor.");
            IEncryptor result = (IEncryptor)c.Invoke(new object[] { method, password });
            return result;
        }

        public static string DumpRegisteredEncryptor()
        {
            var sb = new StringBuilder();
            sb.Append(Environment.NewLine);
            sb.AppendLine("=========================");
            sb.AppendLine("Registered Encryptor Info");
            foreach (var encryptor in _registeredEncryptors)
            {
                sb.AppendLine(System.Globalization.CultureInfo.InvariantCulture, $"{encryptor.Key}=>{encryptor.Value.Name}");
            }

            sb.AppendLine("=========================");
            return sb.ToString();
        }
    }
}
