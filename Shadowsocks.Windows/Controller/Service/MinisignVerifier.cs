using System;
using System.IO;
using System.Linq;
using System.Text;
using Org.BouncyCastle.Crypto.Digests;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;

namespace Shadowsocks.Controller.Service
{
    internal static class MinisignVerifier
    {
        private const string UntrustedCommentPrefix = "untrusted comment: ";
        private const string TrustedCommentPrefix = "trusted comment: ";
        private const int PublicKeyLength = 42;
        private const int SignatureRecordLength = 74;
        private const int Ed25519SignatureLength = 64;
        private const int Blake2bHashLength = 64;

        public static bool VerifyFile(string filePath, string signatureText, string publicKeyBase64)
        {
            if (string.IsNullOrWhiteSpace(filePath))
                throw new ArgumentException("A signed file path is required.", nameof(filePath));
            if (!File.Exists(filePath))
                throw new FileNotFoundException("The signed file was not found.", filePath);

            ParsedPublicKey publicKey = ParsePublicKey(publicKeyBase64);
            ParsedSignature signature = ParseSignature(signatureText);
            if (!publicKey.KeyId.SequenceEqual(signature.KeyId))
                return false;

            byte[] messageHash = ComputeBlake2b512(filePath);
            if (!VerifyEd25519(publicKey.PublicKey, messageHash, signature.Signature))
                return false;

            byte[] trustedComment = Encoding.UTF8.GetBytes(signature.TrustedComment);
            byte[] globalMessage = new byte[signature.Signature.Length + trustedComment.Length];
            Buffer.BlockCopy(signature.Signature, 0, globalMessage, 0, signature.Signature.Length);
            Buffer.BlockCopy(trustedComment, 0, globalMessage, signature.Signature.Length, trustedComment.Length);
            return VerifyEd25519(publicKey.PublicKey, globalMessage, signature.GlobalSignature);
        }

        private static ParsedPublicKey ParsePublicKey(string publicKeyBase64)
        {
            if (string.IsNullOrWhiteSpace(publicKeyBase64))
                throw new InvalidDataException("The Minisign public key is empty.");

            byte[] raw;
            try
            {
                raw = Convert.FromBase64String(publicKeyBase64.Trim());
            }
            catch (FormatException ex)
            {
                throw new InvalidDataException("The Minisign public key is not valid Base64.", ex);
            }

            if (raw.Length != PublicKeyLength || raw[0] != (byte)'E' || raw[1] != (byte)'d')
                throw new InvalidDataException("The Minisign public key has an unsupported format.");

            return new ParsedPublicKey(raw[2..10], raw[10..42]);
        }

        private static ParsedSignature ParseSignature(string signatureText)
        {
            if (string.IsNullOrWhiteSpace(signatureText))
                throw new InvalidDataException("The Minisign signature is empty.");

            string[] lines = signatureText
                .Replace("\r\n", "\n", StringComparison.Ordinal)
                .Replace('\r', '\n')
                .Split('\n', StringSplitOptions.RemoveEmptyEntries);

            if (lines.Length != 4)
                throw new InvalidDataException("The Minisign signature must contain exactly four non-empty lines.");
            if (!lines[0].StartsWith(UntrustedCommentPrefix, StringComparison.Ordinal))
                throw new InvalidDataException("The Minisign signature has an invalid untrusted comment.");
            if (!lines[2].StartsWith(TrustedCommentPrefix, StringComparison.Ordinal))
                throw new InvalidDataException("The Minisign signature has an invalid trusted comment.");

            byte[] signatureRecord = DecodeBase64(lines[1], "signature");
            if (signatureRecord.Length != SignatureRecordLength
                || signatureRecord[0] != (byte)'E'
                || signatureRecord[1] != (byte)'D')
            {
                throw new InvalidDataException("Only modern pre-hashed Minisign signatures are supported.");
            }

            byte[] globalSignature = DecodeBase64(lines[3], "global signature");
            if (globalSignature.Length != Ed25519SignatureLength)
                throw new InvalidDataException("The Minisign global signature has an invalid length.");

            string trustedComment = lines[2].Substring(TrustedCommentPrefix.Length);
            return new ParsedSignature(
                signatureRecord[2..10],
                signatureRecord[10..74],
                trustedComment,
                globalSignature);
        }

        private static byte[] DecodeBase64(string value, string description)
        {
            try
            {
                return Convert.FromBase64String(value.Trim());
            }
            catch (FormatException ex)
            {
                throw new InvalidDataException($"The Minisign {description} is not valid Base64.", ex);
            }
        }

        private static byte[] ComputeBlake2b512(string filePath)
        {
            var digest = new Blake2bDigest(512);
            byte[] buffer = new byte[64 * 1024];
            using FileStream input = new(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            int read;
            while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
            {
                digest.BlockUpdate(buffer, 0, read);
            }

            byte[] hash = new byte[Blake2bHashLength];
            digest.DoFinal(hash, 0);
            return hash;
        }

        private static bool VerifyEd25519(byte[] publicKey, byte[] message, byte[] signature)
        {
            var verifier = new Ed25519Signer();
            verifier.Init(false, new Ed25519PublicKeyParameters(publicKey, 0));
            verifier.BlockUpdate(message, 0, message.Length);
            return verifier.VerifySignature(signature);
        }

        private sealed record ParsedPublicKey(byte[] KeyId, byte[] PublicKey);
        private sealed record ParsedSignature(byte[] KeyId, byte[] Signature, string TrustedComment, byte[] GlobalSignature);
    }
}
