#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Shadowsocks.Controller.Service;
using Shadowsocks.Controller.Traffic;

namespace Shadowsocks.UnitTests
{
    [TestClass]
    public class DnsCryptCleanModeIntegrationTests
    {
        [TestMethod]
        public async Task ComponentAndRuntimeStayInsideProvidedCleanSessionRoot()
        {
            string cleanRoot = Path.Combine(
                Path.GetTempPath(),
                "Shadowsocks.UnitTests",
                "Clean",
                Guid.NewGuid().ToString("N"));
            string componentRoot = Path.Combine(cleanRoot, "Components", "DNSCryptProxy");
            string updateRoot = Path.Combine(cleanRoot, "Temp", "Updates", "DNSCryptProxy");
            string runtimeRoot = Path.Combine(cleanRoot, "Runtime", "DNSCryptProxy");
            Directory.CreateDirectory(Path.Combine(componentRoot, "2.1.18"));
            string executable = Path.Combine(componentRoot, "2.1.18", "dnscrypt-proxy.exe");
            await File.WriteAllBytesAsync(executable, [0x4D, 0x5A]);

            using var httpClient = new HttpClient(new RejectNetworkHandler());
            using var component = new DnsCryptComponentManager(
                httpClient,
                componentRoot,
                updateRoot,
                [DnsCryptComponentManager.ReleaseSigningPublicKey]);
            component.ActivateVersion(new Version(2, 1, 18));
            component.SetAutoUpdate(false);

            var platform = new FakeRuntimePlatform();
            using var runtime = new DnsCryptRuntimeManager(
                component.GetStatus,
                runtimeRoot,
                platform,
                () => 55331,
                (_, _, _) => Task.FromResult(true),
                [TimeSpan.Zero],
                TimeSpan.FromSeconds(1));

            DnsCryptRuntimeStatus started = await runtime.StartAsync(
                new DnsCryptRuntimeStartOptions(new DnsCryptConfig
                {
                    routeThroughShadowsocks = false,
                    automaticResolvers = false,
                    serverNames = new System.Collections.Generic.List<string> { "test-resolver" },
                })
                {
                    StaticResolverStamps = new System.Collections.Generic.Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["test-resolver"] = "sdns://test",
                    },
                });
            Assert.IsTrue(started.IsRunning);
            string configPath = started.ConfigPath
                ?? throw new AssertFailedException("Running DNSCrypt status must expose its generated configuration path.");
            Assert.IsTrue(configPath.StartsWith(cleanRoot, StringComparison.OrdinalIgnoreCase));

            foreach (string path in Directory.EnumerateFileSystemEntries(cleanRoot, "*", SearchOption.AllDirectories))
            {
                Assert.IsTrue(
                    Path.GetFullPath(path).StartsWith(Path.GetFullPath(cleanRoot), StringComparison.OrdinalIgnoreCase),
                    path);
            }

            Assert.IsTrue(File.Exists(Path.Combine(componentRoot, "component.json")));
            Assert.IsTrue(File.Exists(Path.Combine(runtimeRoot, "dnscrypt-proxy.toml")));
            await runtime.StopAsync();
            try { Directory.Delete(cleanRoot, recursive: true); } catch { }
        }

        private sealed class RejectNetworkHandler : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request,
                CancellationToken cancellationToken)
            {
                throw new AssertFailedException("Clean Mode integration test must not use the network.");
            }
        }

        private sealed class FakeRuntimePlatform : IDnsCryptRuntimePlatform
        {
            public Task<DnsCryptCommandResult> ExecuteAsync(
                string executablePath,
                string workingDirectory,
                System.Collections.Generic.IReadOnlyList<string> arguments,
                TimeSpan timeout,
                CancellationToken cancellationToken)
            {
                string output = arguments.Contains("-version") ? "2.1.18" : "Configuration successfully checked";
                return Task.FromResult(new DnsCryptCommandResult(0, output, string.Empty));
            }

            public IDnsCryptRunningProcess Start(
                string executablePath,
                string workingDirectory,
                System.Collections.Generic.IReadOnlyList<string> arguments,
                Action<string> onStandardOutput,
                Action<string> onStandardError)
            {
                return new FakeRunningProcess();
            }
        }

        private sealed class FakeRunningProcess : IDnsCryptRunningProcess
        {
            public int Id => 4242;
            public bool HasExited { get; private set; }
            public event EventHandler? Exited;

            public void Kill()
            {
                if (HasExited)
                    return;
                HasExited = true;
                Exited?.Invoke(this, EventArgs.Empty);
            }

            public Task WaitForExitAsync(CancellationToken cancellationToken) => Task.CompletedTask;
            public void Dispose() { }
        }
    }
}
