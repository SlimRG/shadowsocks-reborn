using System;
using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Shadowsocks.Controller.Service;
using Shadowsocks.Controller.Traffic;

namespace Shadowsocks.UnitTests
{
    [TestClass]
    public class DnsCryptTomlGeneratorTests
    {
        [TestMethod]
        public void RuntimeRequiresConcreteSelectedResolver()
        {
            Assert.ThrowsExactly<ArgumentException>(() =>
                DnsCryptTomlGenerator.Generate(38471, new DnsCryptConfig()));
        }

        [TestMethod]
        public void DnsCryptAndDohResolversAreEnabledWhileOdohRemainsDisabled()
        {
            var config = new DnsCryptConfig
            {
                serverNames = new List<string> { "cloudflare" },
            };
            string toml = DnsCryptTomlGenerator.Generate(5300, config);

            StringAssert.Contains(toml, "dnscrypt_servers = true");
            StringAssert.Contains(toml, "doh_servers = true");
            StringAssert.Contains(toml, "odoh_servers = false");
            string[] tomlLines = toml.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
            Assert.IsFalse(Array.Exists(tomlLines, static line => string.Equals(line, "doh_servers = false", StringComparison.Ordinal)));
        }

        [TestMethod]
        public void ProxyRoutingForcesTcpAndWritesLocalSocksEndpoint()
        {
            var config = new DnsCryptConfig
            {
                routeThroughShadowsocks = true,
                serverNames = new List<string> { "cloudflare" },
            };

            string toml = DnsCryptTomlGenerator.Generate(5300, config, 1080);

            StringAssert.Contains(toml, "force_tcp = true");
            StringAssert.Contains(toml, "proxy = 'socks5://127.0.0.1:1080'");
        }

        [TestMethod]
        public void SelectedServersAreTrimmedAndDeduplicated()
        {
            var config = new DnsCryptConfig
            {
                automaticResolvers = false,
                serverNames = new List<string> { " cloudflare ", "quad9-dnscrypt-ip4-filter-pri", "CLOUDFLARE" },
            };

            string toml = DnsCryptTomlGenerator.Generate(5300, config);

            StringAssert.Contains(toml, "server_names = ['cloudflare', 'quad9-dnscrypt-ip4-filter-pri']");
        }


        [TestMethod]
        public void AutomaticRuntimeNeverFallsBackToStaticCloudflare()
        {
            var config = new DnsCryptConfig
            {
                automaticResolvers = true,
                serverNames = new List<string> { "quad9-dnscrypt-ip4-filter-pri" },
            };

            string toml = DnsCryptTomlGenerator.Generate(5300, config);

            StringAssert.Contains(toml, "server_names = ['quad9-dnscrypt-ip4-filter-pri']");
            StringAssert.Contains(toml, "bootstrap_resolvers = []");
            StringAssert.Contains(toml, "[sources.public-resolvers]");
            Assert.IsFalse(toml.Contains("[static.cloudflare]", StringComparison.Ordinal));
            Assert.IsFalse(DnsCryptTomlGenerator.RuntimeUsesPlaintextBootstrap(toml));
        }


        [TestMethod]
        public void AutomaticRuntimeUsesSelectedFilteredNamesWhenProvided()
        {
            var config = new DnsCryptConfig
            {
                automaticResolvers = true,
                serverNames = new List<string> { "dnscry.pt-moscow-ipv4" },
            };

            string toml = DnsCryptTomlGenerator.Generate(5300, config);

            StringAssert.Contains(toml, "server_names = ['dnscry.pt-moscow-ipv4']");
            StringAssert.Contains(toml, "[sources.public-resolvers]");
            StringAssert.Contains(toml, "bootstrap_resolvers = []");
            Assert.IsFalse(toml.Contains("[static.cloudflare]", StringComparison.Ordinal));
            Assert.IsFalse(DnsCryptTomlGenerator.RuntimeUsesPlaintextBootstrap(toml));
        }

        [TestMethod]
        public void ManualRuntimeUsesSignedCacheWithoutPlaintextBootstrap()
        {
            var config = new DnsCryptConfig
            {
                automaticResolvers = false,
                serverNames = new List<string> { "cloudflare", "quad9-dnscrypt-ip4-filter-pri" },
            };

            string toml = DnsCryptTomlGenerator.Generate(5300, config);

            StringAssert.Contains(toml, "bootstrap_resolvers = []");
            StringAssert.Contains(toml, "[sources.public-resolvers]");
            Assert.IsFalse(DnsCryptTomlGenerator.RuntimeUsesPlaintextBootstrap(toml));
        }

        [TestMethod]
        public void ResolverCatalogRefreshIsTheOnlyProfileWithPlaintextBootstrap()
        {
            var config = new DnsCryptConfig { automaticResolvers = false };

            string toml = DnsCryptTomlGenerator.Generate(
                5300,
                config,
                purpose: DnsCryptTomlPurpose.ResolverCatalog);

            StringAssert.Contains(toml, "bootstrap_resolvers = ['9.9.9.11:53', '8.8.8.8:53']");
            StringAssert.Contains(toml, "[sources.public-resolvers]");
            Assert.IsTrue(DnsCryptTomlGenerator.RuntimeUsesPlaintextBootstrap(toml));
        }

        [TestMethod]
        public void InvalidServerNameIsRejectedInsteadOfBeingInjectedIntoToml()
        {
            var config = new DnsCryptConfig
            {
                automaticResolvers = false,
                serverNames = new List<string> { "good', proxy='evil" },
            };

            Assert.ThrowsExactly<ArgumentException>(() => DnsCryptTomlGenerator.Generate(5300, config));
        }

        [TestMethod]
        public void ProxyRoutingRequiresSocksPort()
        {
            var config = new DnsCryptConfig
            {
                routeThroughShadowsocks = true,
                serverNames = new List<string> { "cloudflare" },
            };

            Assert.ThrowsExactly<ArgumentException>(() => DnsCryptTomlGenerator.Generate(5300, config));
        }

        [TestMethod]
        public void AtLeastOneAddressFamilyMustBeEnabled()
        {
            var config = new DnsCryptConfig { ipv4Servers = false, ipv6Servers = false };

            Assert.ThrowsExactly<ArgumentException>(() => DnsCryptTomlGenerator.Generate(5300, config));
        }
    }
}
