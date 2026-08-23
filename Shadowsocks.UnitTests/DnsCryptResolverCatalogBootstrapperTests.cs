using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Shadowsocks.Controller.Service;

namespace Shadowsocks.UnitTests
{
    [TestClass]
    public class DnsCryptResolverCatalogBootstrapperTests
    {
        private string root;

        [TestInitialize]
        public void Initialize()
        {
            root = Path.Combine(Path.GetTempPath(), "Shadowsocks.UnitTests", "DnsCryptCatalog", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
        }

        [TestCleanup]
        public void Cleanup()
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }

        [TestMethod]
        public async Task DohResolverFallsBackFromCloudflareToGoogle()
        {
            var providers = new List<string>();
            using var resolver = new ShadowsocksDohResolver(
                "127.0.0.1",
                1080,
                (provider, query, _) =>
                {
                    providers.Add(provider.Name);
                    if (string.Equals(provider.Name, "Cloudflare", StringComparison.Ordinal))
                        throw new HttpRequestException("Synthetic Cloudflare failure.");
                    return Task.FromResult(BuildAddressResponse(query, IPAddress.Parse("203.0.113.7")));
                });

            IPAddress[] addresses = await resolver.ResolveAsync("example.test", CancellationToken.None);

            CollectionAssert.AreEqual(new[] { "Cloudflare", "Google" }, providers.ToArray());
            CollectionAssert.AreEqual(new[] { IPAddress.Parse("203.0.113.7") }, addresses);
        }

        [TestMethod]
        public void DohWireParserAcceptsIpv4AndRejectsMismatchedTransaction()
        {
            const ushort transactionId = 0x1234;
            byte[] query = ShadowsocksDohResolver.BuildQuery("example.test", 1, transactionId);
            byte[] response = BuildAddressResponse(query, IPAddress.Parse("203.0.113.7"));

            CollectionAssert.AreEqual(
                new[] { IPAddress.Parse("203.0.113.7") },
                ShadowsocksDohResolver.ParseAddresses(response, transactionId, 1));

            Assert.ThrowsExactly<InvalidDataException>(() =>
                ShadowsocksDohResolver.ParseAddresses(response, 0x4321, 1));
        }

        [TestMethod]
        public async Task CatalogBootstrapDownloadsCatalogAndSignatureBeforePublishing()
        {
            byte[] catalog = Encoding.UTF8.GetBytes("## resolver\n\nsdns://signed\n");
            byte[] signature = Encoding.UTF8.GetBytes("synthetic minisign");
            var requests = new List<string>();
            var bootstrapper = new DnsCryptResolverCatalogBootstrapper(
                (uri, _) =>
                {
                    requests.Add(uri.AbsoluteUri);
                    return Task.FromResult(uri.AbsolutePath.EndsWith(".minisig", StringComparison.OrdinalIgnoreCase)
                        ? signature
                        : catalog);
                },
                (path, signatureText, key) =>
                    File.ReadAllBytes(path).SequenceEqual(catalog)
                    && signatureText == Encoding.UTF8.GetString(signature)
                    && key == DnsCryptTomlGenerator.PublicResolversMinisignKey);

            await bootstrapper.EnsureFreshAsync(root, "127.0.0.1", 1080, CancellationToken.None);

            CollectionAssert.AreEqual(catalog, File.ReadAllBytes(Path.Combine(root, "public-resolvers.md")));
            CollectionAssert.AreEqual(signature, File.ReadAllBytes(Path.Combine(root, "public-resolvers.md.minisig")));
            Assert.IsTrue(requests.Any(url => url.EndsWith("public-resolvers.md", StringComparison.Ordinal)));
            Assert.IsTrue(requests.Any(url => url.EndsWith("public-resolvers.md.minisig", StringComparison.Ordinal)));
        }

        [TestMethod]
        public async Task CatalogBootstrapRejectsInvalidSignatureWithoutPublishing()
        {
            byte[] catalog = Encoding.UTF8.GetBytes("untrusted catalog");
            byte[] signature = Encoding.UTF8.GetBytes("untrusted signature");
            var bootstrapper = new DnsCryptResolverCatalogBootstrapper(
                (uri, _) => Task.FromResult(uri.AbsolutePath.EndsWith(".minisig", StringComparison.OrdinalIgnoreCase)
                    ? signature
                    : catalog),
                (_, _, _) => false);

            await Assert.ThrowsExactlyAsync<DnsCryptBootstrapException>(() =>
                bootstrapper.EnsureFreshAsync(root, "127.0.0.1", 1080, CancellationToken.None));

            Assert.IsFalse(File.Exists(Path.Combine(root, "public-resolvers.md")));
            Assert.IsFalse(File.Exists(Path.Combine(root, "public-resolvers.md.minisig")));
        }

        [TestMethod]
        public async Task FreshVerifiedCatalogAvoidsNetwork()
        {
            string catalogPath = Path.Combine(root, "public-resolvers.md");
            string signaturePath = Path.Combine(root, "public-resolvers.md.minisig");
            File.WriteAllText(catalogPath, "cached catalog");
            File.WriteAllText(signaturePath, "cached signature");
            var bootstrapper = new DnsCryptResolverCatalogBootstrapper(
                (_, _) => throw new AssertFailedException("Fresh verified cache must not use the network."),
                (_, _, _) => true);

            await bootstrapper.EnsureFreshAsync(root, "127.0.0.1", 1080, CancellationToken.None);
        }

        [TestMethod]
        public async Task VerifiedStaleCatalogIsFallbackWhenDohRefreshFails()
        {
            string catalogPath = Path.Combine(root, "public-resolvers.md");
            string signaturePath = Path.Combine(root, "public-resolvers.md.minisig");
            File.WriteAllText(catalogPath, "cached catalog");
            File.WriteAllText(signaturePath, "cached signature");
            DateTime stale = DateTime.UtcNow.AddDays(-2);
            File.SetLastWriteTimeUtc(catalogPath, stale);
            File.SetLastWriteTimeUtc(signaturePath, stale);
            var bootstrapper = new DnsCryptResolverCatalogBootstrapper(
                (_, _) => throw new HttpRequestException("Synthetic DoH/source failure."),
                (_, _, _) => true);

            await bootstrapper.EnsureFreshAsync(root, "127.0.0.1", 1080, CancellationToken.None);

            Assert.AreEqual("cached catalog", File.ReadAllText(catalogPath));
            Assert.AreEqual("cached signature", File.ReadAllText(signaturePath));
        }

        private static byte[] BuildAddressResponse(byte[] query, IPAddress address)
        {
            int questionLength = query.Length - 12;
            byte[] addressBytes = address.GetAddressBytes();
            ushort type = address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? (ushort)1 : (ushort)28;
            byte[] response = new byte[12 + questionLength + 12 + addressBytes.Length];
            query.AsSpan(0, 2).CopyTo(response);
            BinaryPrimitives.WriteUInt16BigEndian(response.AsSpan(2, 2), 0x8180);
            BinaryPrimitives.WriteUInt16BigEndian(response.AsSpan(4, 2), 1);
            BinaryPrimitives.WriteUInt16BigEndian(response.AsSpan(6, 2), 1);
            query.AsSpan(12).CopyTo(response.AsSpan(12));
            int offset = 12 + questionLength;
            response[offset++] = 0xC0;
            response[offset++] = 0x0C;
            BinaryPrimitives.WriteUInt16BigEndian(response.AsSpan(offset, 2), type);
            BinaryPrimitives.WriteUInt16BigEndian(response.AsSpan(offset + 2, 2), 1);
            BinaryPrimitives.WriteUInt32BigEndian(response.AsSpan(offset + 4, 4), 60);
            BinaryPrimitives.WriteUInt16BigEndian(response.AsSpan(offset + 8, 2), checked((ushort)addressBytes.Length));
            offset += 10;
            addressBytes.CopyTo(response, offset);
            return response;
        }
    }
}
