using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Shadowsocks.Controller.Service;
using Shadowsocks.Controller.Traffic;

namespace Shadowsocks.UnitTests
{
    [TestClass]
    [DoNotParallelize]
    public class DnsCryptRuntimeManagerTests
    {
        private string root;
        private string runtimeRoot;
        private string executablePath;

        [TestInitialize]
        public void Initialize()
        {
            root = Path.Combine(Path.GetTempPath(), "Shadowsocks.UnitTests", "DnsCryptRuntime", Guid.NewGuid().ToString("N"));
            runtimeRoot = Path.Combine(root, "Runtime", "DNSCryptProxy");
            executablePath = Path.Combine(root, "Components", "DNSCryptProxy", "2.1.18", "dnscrypt-proxy.exe");
            Directory.CreateDirectory(Path.GetDirectoryName(executablePath));
            File.WriteAllBytes(executablePath, new byte[] { 0x4D, 0x5A });
        }

        [TestCleanup]
        public void Cleanup()
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }

        [TestMethod]
        public async Task StartValidatesVersionStartsAndHealthChecksWithoutBlockingPreflight()
        {
            var platform = new FakeRuntimePlatform();
            using var manager = CreateManager(platform, health: true, port: 38471);

            DnsCryptRuntimeStatus status = await manager.StartAsync(CreateConcreteRuntimeOptions());

            Assert.AreEqual(DnsCryptRuntimeState.Running, status.State);
            Assert.AreEqual(new Version(2, 1, 18), status.Version);
            Assert.AreEqual(38471, status.Port);
            Assert.AreEqual(7000, status.ProcessId);
            Assert.IsTrue(File.Exists(status.ConfigPath));
            Assert.AreEqual(1, platform.Commands.Count);
            CollectionAssert.AreEqual(new[] { "-version" }, platform.Commands[0].ToArray());
            Assert.IsFalse(platform.Commands.Any(command => command.Contains("-check")),
                "Normal runtime startup must not block on dnscrypt-proxy -check network/source initialization.");
            CollectionAssert.AreEqual(new[] { "-config", status.ConfigPath }, platform.StartArguments.Single().ToArray());

            await manager.StopAsync();
            Assert.AreEqual(DnsCryptRuntimeState.Stopped, manager.GetStatus().State);
            Assert.IsTrue(platform.Processes[0].WasKilled);
        }

        [TestMethod]
        public async Task StartRejectsMissingComponent()
        {
            var platform = new FakeRuntimePlatform();
            using var manager = new DnsCryptRuntimeManager(
                () => new DnsCryptComponentStatus(false, null, null, null, true, null),
                runtimeRoot,
                platform,
                () => 38471,
                (_, _, _) => Task.FromResult(true),
                [TimeSpan.Zero],
                startupTimeout: TimeSpan.FromMilliseconds(40));

            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
                manager.StartAsync(CreateConcreteRuntimeOptions()));
            Assert.AreEqual(DnsCryptRuntimeState.NotInstalled, manager.GetStatus().State);
            Assert.AreEqual(0, platform.Commands.Count);
        }

        [TestMethod]
        public async Task StartRejectsExecutableVersionMismatch()
        {
            var platform = new FakeRuntimePlatform { VersionOutput = "2.1.17" };
            using var manager = CreateManager(platform, health: true, port: 38471);

            await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
                manager.StartAsync(CreateConcreteRuntimeOptions()));

            Assert.AreEqual(DnsCryptRuntimeState.Failed, manager.GetStatus().State);
            Assert.AreEqual(0, platform.Processes.Count);
        }

        [TestMethod]
        public async Task PreparedAutomaticValidationUsesSignedCatalogBeforeRuntimeHealthCheck()
        {
            var platform = new FakeRuntimePlatform();
            using var manager = CreateManager(platform, health: true, port: 38471);
            DnsCryptPreparedComponent prepared = CreatePreparedComponent();

            await manager.ValidatePreparedAsync(
                prepared,
                new DnsCryptRuntimeStartOptions(CreateDirectDnsCryptConfig()));

            int versionCommandIndex = platform.Commands.FindIndex(command => command.Contains("-version"));
            int listCommandIndex = platform.Commands.FindIndex(command => command.Contains("-list-all") && command.Contains("-json"));
            Assert.IsTrue(versionCommandIndex >= 0);
            Assert.IsTrue(listCommandIndex > versionCommandIndex, "Prepared binary version must be verified before signed-catalog discovery.");
            Assert.IsTrue(platform.StartedConfigText.Contains("server_names = ['test-resolver']", StringComparison.Ordinal));
            Assert.IsTrue(platform.StartedConfigText.Contains("[static.'test-resolver']", StringComparison.Ordinal));
            Assert.IsTrue(platform.StartedConfigText.Contains("stamp = 'sdns://test'", StringComparison.Ordinal));
            Assert.IsFalse(platform.StartedConfigText.Contains("[sources.public-resolvers]", StringComparison.Ordinal));
        }

        [TestMethod]
        public async Task PreparedValidationRejectsVersionMismatchBeforeCatalogDiscovery()
        {
            var platform = new FakeRuntimePlatform { VersionOutput = "2.1.17" };
            using var manager = CreateManager(platform, health: true, port: 38471);
            DnsCryptPreparedComponent prepared = CreatePreparedComponent();

            await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
                manager.ValidatePreparedAsync(prepared, new DnsCryptRuntimeStartOptions(CreateDirectDnsCryptConfig())));

            Assert.IsTrue(platform.Commands.Any(command => command.Contains("-version")));
            Assert.IsFalse(platform.Commands.Any(command => command.Contains("-list-all")),
                "A mismatched prepared binary must fail before it is allowed to perform catalog/network operations.");
            Assert.AreEqual(0, platform.Processes.Count);
        }

        [TestMethod]
        public async Task PreparedValidationRejectsInvalidGeneratedConfiguration()
        {
            var platform = new FakeRuntimePlatform { CheckExitCode = 2, CheckError = "bad config" };
            using var manager = CreateManager(platform, health: true, port: 38471);
            DnsCryptPreparedComponent prepared = CreatePreparedComponent();

            await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
                manager.ValidatePreparedAsync(prepared, new DnsCryptRuntimeStartOptions(CreateDirectDnsCryptConfig())));

            Assert.AreEqual(0, platform.Processes.Count);
        }

        [TestMethod]
        public async Task PreparedValidationCheckTimeoutFallsBackToRuntimeHealthCheck()
        {
            var platform = new FakeRuntimePlatform { CheckThrowsTimeout = true };
            using var manager = CreateManager(platform, health: true, port: 38471);
            DnsCryptPreparedComponent prepared = CreatePreparedComponent();

            await manager.ValidatePreparedAsync(prepared, new DnsCryptRuntimeStartOptions(CreateDirectDnsCryptConfig()));

            Assert.IsTrue(platform.Commands.Any(command => command.Contains("-check")));
            Assert.AreEqual(1, platform.Processes.Count);
            Assert.IsTrue(platform.Processes[0].WasKilled, "Validation candidate must be stopped after a successful DNS health probe.");
        }

        [TestMethod]
        public async Task FailedHealthCheckTerminatesStartedProcess()
        {
            var platform = new FakeRuntimePlatform();
            using var manager = CreateManager(platform, health: false, port: 38471);

            await Assert.ThrowsExactlyAsync<DnsCryptRuntimeStartupException>(() =>
                manager.StartAsync(CreateConcreteRuntimeOptions()));

            Assert.AreEqual(DnsCryptRuntimeState.Failed, manager.GetStatus().State);
            Assert.AreEqual(1, platform.Processes.Count);
            Assert.IsTrue(platform.Processes[0].WasKilled);
        }

        [TestMethod]
        public async Task UnexpectedExitRestartsRuntimeWithSavedConfig()
        {
            var platform = new FakeRuntimePlatform();
            using var manager = CreateManager(platform, health: true, port: 38471, restartDelays: [TimeSpan.Zero]);
            await manager.StartAsync(CreateConcreteRuntimeOptions());

            platform.Processes[0].TriggerUnexpectedExit();
            await WaitUntilAsync(() => platform.Processes.Count >= 2 && manager.GetStatus().State == DnsCryptRuntimeState.Running);

            Assert.AreEqual(2, platform.Processes.Count);
            Assert.AreEqual(7001, manager.GetStatus().ProcessId);
        }

        [TestMethod]
        public async Task ExitDuringRunningPromotionNeverLeavesStaleRunningStatus()
        {
            var platform = new FakeRuntimePlatform
            {
                ExitFirstProcessOnExitedSubscription = true,
            };
            using var manager = CreateManager(
                platform,
                health: true,
                port: 38471,
                restartDelays: [TimeSpan.FromSeconds(1)]);

            DnsCryptRuntimeStatus initial = await manager.StartAsync(
                CreateConcreteRuntimeOptions());
            Assert.AreEqual(7000, initial.ProcessId);

            await WaitUntilAsync(() => manager.GetStatus().State == DnsCryptRuntimeState.Failed);
            DnsCryptRuntimeStatus status = manager.GetStatus();
            Assert.AreEqual(0, status.ProcessId);
        }

        [TestMethod]
        public async Task ExplicitStopDoesNotTriggerCrashRecovery()
        {
            var platform = new FakeRuntimePlatform();
            using var manager = CreateManager(platform, health: true, port: 38471, restartDelays: [TimeSpan.Zero]);
            await manager.StartAsync(CreateConcreteRuntimeOptions());

            await manager.StopAsync();
            await Task.Delay(50);

            Assert.AreEqual(1, platform.Processes.Count);
            Assert.AreEqual(DnsCryptRuntimeState.Stopped, manager.GetStatus().State);
        }

        [TestMethod]
        public async Task UnexpectedExitStopsAfterConfiguredRestartLimit()
        {
            var platform = new FakeRuntimePlatform();
            int healthCalls = 0;
            using var manager = new DnsCryptRuntimeManager(
                () => new DnsCryptComponentStatus(true, new Version(2, 1, 18), null, executablePath, true, null),
                runtimeRoot,
                platform,
                () => 38471,
                (_, _, _) => Task.FromResult(Interlocked.Increment(ref healthCalls) == 1),
                [TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero],
                startupTimeout: TimeSpan.FromMilliseconds(40));

            await manager.StartAsync(CreateConcreteRuntimeOptions());
            platform.Processes[0].TriggerUnexpectedExit();

            await WaitUntilAsync(() => platform.Processes.Count >= 4 && manager.GetStatus().State == DnsCryptRuntimeState.Failed);

            Assert.AreEqual(4, platform.Processes.Count);
            StringAssert.Contains(manager.GetStatus().LastError, "3 automatic restart attempts");
        }

        [TestMethod]
        public void DnsPacketValidationRequiresMatchingResponseId()
        {
            byte[] query = DnsCryptRuntimeManager.BuildDnsAQuery(0x1234, "example.com");
            Assert.IsTrue(query.Length > 20);
            Assert.AreEqual(0x12, query[0]);
            Assert.AreEqual(0x34, query[1]);

            byte[] response = query.ToArray();
            response[2] = 0x81;
            response[3] = 0x80;

            Assert.IsTrue(DnsCryptRuntimeManager.IsValidDnsResponse(response, 0x1234));
            Assert.IsFalse(DnsCryptRuntimeManager.IsValidDnsResponse(response, 0x9999));
            Assert.IsFalse(DnsCryptRuntimeManager.IsValidDnsResponse(response.Take(12).ToArray(), 0x1234));
        }

        [TestMethod]
        public void DnsPacketValidationRejectsNxDomainAndWrongQuestion()
        {
            byte[] query = DnsCryptRuntimeManager.BuildDnsAQuery(0x4321, "example.com");
            byte[] nxDomain = query.ToArray();
            nxDomain[2] = 0x81;
            nxDomain[3] = 0x83;
            Assert.IsFalse(DnsCryptRuntimeManager.IsValidDnsResponse(nxDomain, 0x4321));

            byte[] wrongQuestion = DnsCryptRuntimeManager.BuildDnsAQuery(0x4321, "example.net");
            wrongQuestion[2] = 0x81;
            wrongQuestion[3] = 0x80;
            Assert.IsFalse(DnsCryptRuntimeManager.IsValidDnsResponse(wrongQuestion, 0x4321, "example.com"));
        }

        [TestMethod]
        public async Task ValidatePreparedRunsCandidateWithoutReplacingActiveRuntime()
        {
            var platform = new FakeRuntimePlatform();
            using var manager = CreateManager(platform, health: true, port: 38471);
            DnsCryptRuntimeStatus running = await manager.StartAsync(CreateConcreteRuntimeOptions());

            DnsCryptPreparedComponent prepared = CreatePreparedComponent();

            await manager.ValidatePreparedAsync(prepared, new DnsCryptRuntimeStartOptions(CreateDirectDnsCryptConfig()));

            DnsCryptRuntimeStatus after = manager.GetStatus();
            Assert.AreEqual(DnsCryptRuntimeState.Running, after.State);
            Assert.AreEqual(running.ProcessId, after.ProcessId);
            Assert.AreEqual(2, platform.Processes.Count);
            Assert.IsTrue(platform.Processes[1].WasKilled, "Validation candidate must always be terminated.");
            await manager.StopAsync();
        }

        [TestMethod]
        public async Task UdpHealthCheckAcceptsMatchingDnsResponse()
        {
            using var server = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            int port = ((IPEndPoint)server.Client.LocalEndPoint).Port;
            Task responder = Task.Run(async () =>
            {
                UdpReceiveResult request = await server.ReceiveAsync();
                byte[] response = request.Buffer.ToArray();
                response[2] = 0x81;
                response[3] = 0x80;
                await server.SendAsync(response, response.Length, request.RemoteEndPoint);
            });

            bool healthy = await DnsCryptRuntimeManager.DnsHealthCheckAsync(port, TimeSpan.FromSeconds(1), CancellationToken.None);
            await responder;

            Assert.IsTrue(healthy);
        }

        [TestMethod]
        public void AllocatedLoopbackPortIsInUsableRange()
        {
            int port = DnsCryptRuntimeManager.AllocateLoopbackPort();
            Assert.IsTrue(port is > 0 and <= 65535);
        }

        [TestMethod]
        public void VersionParserAcceptsPlainAndVPrefixedOutput()
        {
            Assert.AreEqual(new Version(2, 1, 18), DnsCryptRuntimeManager.ParseReportedVersion("2.1.18\r\n"));
            Assert.AreEqual(new Version(2, 1, 18), DnsCryptRuntimeManager.ParseReportedVersion("v2.1.18\n"));
            Assert.IsNull(DnsCryptRuntimeManager.ParseReportedVersion("not-a-version"));
        }

        [TestMethod]
        public void ResolverListParserReadsCurrentDnsCryptJsonShape()
        {
            const string json =
                "[" +
                "{\"name\":\"cloudflare\",\"proto\":\"DNSCrypt\",\"ipv6\":false,\"addrs\":[\"1.1.1.1\"],\"ports\":[443],\"dnssec\":true,\"nolog\":true,\"nofilter\":true,\"description\":\"Cloudflare\",\"stamp\":\"sdns://test\"}," +
                "{\"name\":\"quad9-doh\",\"proto\":\"DoH\",\"ipv6\":true,\"addrs\":[\"[2620:fe::fe]\"],\"ports\":[443],\"dnssec\":true,\"nolog\":true,\"nofilter\":false,\"description\":\"Quad9\",\"stamp\":\"sdns://test2\"}" +
                "]";

            IReadOnlyList<DnsCryptResolverInfo> resolvers = DnsCryptRuntimeManager.ParseResolverList(json);

            Assert.AreEqual(2, resolvers.Count);
            Assert.AreEqual("cloudflare", resolvers[0].Name);
            Assert.AreEqual("DNSCrypt", resolvers[0].Protocol);
            Assert.IsTrue(resolvers[0].Dnssec == true);
            Assert.IsTrue(resolvers[0].NoLog);
            Assert.AreEqual(443, resolvers[0].Ports[0]);
            Assert.AreEqual("1.1.1.1", resolvers[0].Addresses[0]);
            Assert.AreEqual("sdns://test", resolvers[0].Stamp);
            Assert.AreEqual("quad9-doh", resolvers[1].Name);
            Assert.IsTrue(resolvers[1].IPv6);
        }

        [TestMethod]
        public void ResolverLatencyProbeBuildsEndpointFromCatalogAddressAndPort()
        {
            var resolver = new DnsCryptResolverInfo(
                "test", "DoH", false, true, true, true, "", new[] { "resolver.example", "1.1.1.1" })
            {
                Ports = new[] { 8443 },
            };

            Assert.IsTrue(DnsCryptResolverLatencyProbe.TryGetEndpoint(resolver, out IPEndPoint endpoint));
            Assert.AreEqual(IPAddress.Parse("1.1.1.1"), endpoint.Address);
            Assert.AreEqual(8443, endpoint.Port);
        }

        [TestMethod]
        public void ResolverLatencyParserReadsRttAndKeepsFastestProbe()
        {
            const string output = "[2026-08-18 18:07:32] [NOTICE] [de-one] OK (DNSCrypt) - rtt: 41ms\n"
                + "[2026-08-18 18:07:32] [NOTICE] [de-one] OK (DNSCrypt) - rtt: 35ms - additional certificate\n"
                + "[2026-08-18 18:07:32] [NOTICE] [us-one] TIMEOUT";

            IReadOnlyDictionary<string, int> latencies = DnsCryptRuntimeManager.ParseResolverLatencies(output);

            Assert.AreEqual(35, latencies["de-one"]);
            Assert.IsFalse(latencies.ContainsKey("us-one"));
        }

        [TestMethod]
        public void SelectedResolverParserReadsDnsCryptLowestLatencyLine()
        {
            Assert.AreEqual(
                "cs-dc",
                DnsCryptRuntimeManager.ParseSelectedResolverName(
                    "[2026-08-19 07:15:12] [NOTICE] Server with the lowest initial latency: cs-dc (rtt: 189ms), live servers: 1"));
            Assert.AreEqual(string.Empty, DnsCryptRuntimeManager.ParseSelectedResolverName("Source [public-resolvers] loaded"));
        }

        [TestMethod]
        public async Task RuntimeOutputPopulatesActiveResolverAndActualLatency()
        {
            var platform = new FakeRuntimePlatform
            {
                RuntimeOutputLines =
                [
                    "[NOTICE] [cs-dc] OK (DNSCrypt) - rtt: 189ms",
                    "[NOTICE] Server with the lowest initial latency: cs-dc (rtt: 189ms), live servers: 1",
                ],
            };
            using var manager = CreateManager(platform, health: true, port: 38471);

            await manager.StartAsync(CreateConcreteRuntimeOptions());

            CollectionAssert.AreEqual(new[] { "cs-dc" }, manager.GetActiveResolverNames().ToArray());
            Assert.AreEqual(189, manager.GetResolverLatencies()["cs-dc"]);
        }

        [TestMethod]
        public async Task ActiveRuntimeUsesPinnedStaticResolverWithoutRemoteSources()
        {
            var platform = new FakeRuntimePlatform();
            using var manager = CreateManager(platform, health: true, port: 38471);

            await manager.StartAsync(CreateConcreteRuntimeOptions());

            StringAssert.Contains(platform.StartedConfigText, "[static.'test-resolver']");
            StringAssert.Contains(platform.StartedConfigText, "stamp = 'sdns://test'");
            Assert.IsFalse(platform.StartedConfigText.Contains("[sources.public-resolvers]", StringComparison.Ordinal));
            StringAssert.Contains(platform.StartedConfigText, "bootstrap_resolvers = []");
        }

        [TestMethod]
        public void ResolverProbeDiagnosticsAreVerboseOnly()
        {
            Assert.IsTrue(DnsCryptRuntimeManager.IsResolverProbeDiagnostic(
                "[2026-08-18 18:07:32] [NOTICE] [de-one] OK (DNSCrypt) - rtt: 35ms"));
            Assert.IsTrue(DnsCryptRuntimeManager.IsResolverProbeDiagnostic(
                "[2026-08-18 18:07:32] [NOTICE] [de-one] using the post-quantum X-Wing key exchange"));
            Assert.IsTrue(DnsCryptRuntimeManager.IsResolverProbeDiagnostic(
                "[2026-08-18 18:07:32] [WARNING] [us-one] TIMEOUT"));
            Assert.IsTrue(DnsCryptRuntimeManager.IsResolverProbeDiagnostic(
                "[2026-08-18 18:07:32] [NOTICE] Server with the lowest initial latency: de-one (rtt: 35ms), live servers: 24"));
            Assert.IsFalse(DnsCryptRuntimeManager.IsResolverProbeDiagnostic(
                "[2026-08-18 18:07:32] [NOTICE] Now listening to 127.0.0.1:53000 [UDP]"));
            Assert.IsFalse(DnsCryptRuntimeManager.IsResolverProbeDiagnostic(
                "[2026-08-18 18:07:32] [NOTICE] Source [public-resolvers] loaded"));
        }

        [TestMethod]
        public async Task ListResolversUsesDnsCryptSignedCatalogCommand()
        {
            var platform = new FakeRuntimePlatform
            {
                ListOutput = "[{\"name\":\"cloudflare\",\"proto\":\"DNSCrypt\",\"ipv6\":false,\"dnssec\":true,\"nolog\":true,\"nofilter\":true,\"description\":\"Cloudflare\",\"stamp\":\"sdns://test\"}]",
            };
            using var manager = CreateManager(platform, health: true, port: 38471);

            IReadOnlyList<DnsCryptResolverInfo> resolvers = await manager.ListResolversAsync(
                new DnsCryptRuntimeStartOptions(CreateDirectDnsCryptConfig()));

            Assert.AreEqual(1, resolvers.Count);
            Assert.AreEqual("cloudflare", resolvers[0].Name);
            Assert.IsTrue(platform.Commands.Any(command => command.Contains("-list-all") && command.Contains("-json")));
            Assert.IsFalse(Directory.EnumerateDirectories(runtimeRoot, ".list-*", SearchOption.TopDirectoryOnly).Any());
        }

        [TestMethod]
        public async Task ResolverCatalogBootstrapRunsBeforeDnscryptListCommand()
        {
            var platform = new FakeRuntimePlatform { ListOutput = "[]" };
            var bootstrapper = new FakeCatalogBootstrapper();
            using var manager = CreateManager(platform, health: true, port: 38471, bootstrapper: bootstrapper);

            await manager.ListResolversAsync(new DnsCryptRuntimeStartOptions(new DnsCryptConfig(), 1080)
            {
                ShadowsocksSocks5Host = "127.0.0.1",
            });

            Assert.AreEqual(1, bootstrapper.CallCount);
            Assert.AreEqual("127.0.0.1", bootstrapper.LastHost);
            Assert.AreEqual(1080, bootstrapper.LastPort);
            Assert.IsTrue(platform.ListCommandHadResolverCache);
        }

        [TestMethod]
        public async Task ResolverCatalogRefreshPersistsSignedCacheForFutureMaintenance()
        {
            var platform = new FakeRuntimePlatform
            {
                ListOutput = "[]",
                WriteResolverCacheOnList = true,
            };
            using var manager = CreateManager(platform, health: true, port: 38471);

            await manager.ListResolversAsync(new DnsCryptRuntimeStartOptions(CreateDirectDnsCryptConfig()));

            string persisted = Path.Combine(runtimeRoot, "public-resolvers.md");
            string persistedSignature = Path.Combine(runtimeRoot, "public-resolvers.md.minisig");
            Assert.IsTrue(File.Exists(persisted));
            Assert.IsTrue(File.Exists(persistedSignature));
            Assert.AreEqual("signed resolver cache", File.ReadAllText(persisted));
            Assert.AreEqual("signed resolver signature", File.ReadAllText(persistedSignature));
        }

        [TestMethod]
        public async Task ResolverCatalogListingReusesExistingSignedSourceCache()
        {
            Directory.CreateDirectory(runtimeRoot);
            File.WriteAllText(Path.Combine(runtimeRoot, "public-resolvers.md"), "cached signed source");
            File.WriteAllText(Path.Combine(runtimeRoot, "public-resolvers.md.minisig"), "cached signed signature");
            var platform = new FakeRuntimePlatform
            {
                ListOutput = "[]",
            };
            using var manager = CreateManager(platform, health: true, port: 38471);

            await manager.ListResolversAsync(new DnsCryptRuntimeStartOptions(CreateDirectDnsCryptConfig()));

            Assert.IsTrue(platform.ListCommandHadResolverCache);
        }

        [TestMethod]
        public async Task UpdatingStatePreservesRunningProcessAndRestoresRunning()
        {
            var platform = new FakeRuntimePlatform();
            using var manager = CreateManager(platform, health: true, port: 38471);
            DnsCryptRuntimeStatus running = await manager.StartAsync(CreateConcreteRuntimeOptions());

            DnsCryptRuntimeStatus snapshot = manager.BeginUpdate();
            DnsCryptRuntimeStatus updating = manager.GetStatus();
            Assert.AreEqual(DnsCryptRuntimeState.Updating, updating.State);
            Assert.AreEqual(running.ProcessId, updating.ProcessId);
            Assert.AreEqual(running.Port, updating.Port);
            Assert.IsTrue(updating.IsServing);
            Assert.IsFalse(updating.IsRunning);

            manager.EndUpdate(snapshot, succeeded: false);
            Assert.AreEqual(DnsCryptRuntimeState.Running, manager.GetStatus().State);
            Assert.AreEqual(running.ProcessId, manager.GetStatus().ProcessId);
            await manager.StopAsync();
        }

        [TestMethod]
        public void UpdatingStoppedRuntimeCompletesAsStopped()
        {
            var platform = new FakeRuntimePlatform();
            using var manager = CreateManager(platform, health: true, port: 38471);

            DnsCryptRuntimeStatus snapshot = manager.BeginUpdate();
            Assert.AreEqual(DnsCryptRuntimeState.Updating, manager.GetStatus().State);
            manager.EndUpdate(snapshot, succeeded: true);
            Assert.AreEqual(DnsCryptRuntimeState.Stopped, manager.GetStatus().State);
        }

        [TestMethod]
        public void ConstructorCleansStaleRuntimeOperationDirectories()
        {
            Directory.CreateDirectory(Path.Combine(runtimeRoot, ".validate-stale"));
            Directory.CreateDirectory(Path.Combine(runtimeRoot, ".list-stale"));
            Directory.CreateDirectory(Path.Combine(runtimeRoot, "keep"));

            var platform = new FakeRuntimePlatform();
            using var manager = CreateManager(platform, health: true, port: 38471);

            Assert.IsFalse(Directory.Exists(Path.Combine(runtimeRoot, ".validate-stale")));
            Assert.IsFalse(Directory.Exists(Path.Combine(runtimeRoot, ".list-stale")));
            Assert.IsTrue(Directory.Exists(Path.Combine(runtimeRoot, "keep")));
        }

        [TestMethod]
        public void DnsPolicyModeValuesRemainBackwardCompatible()
        {
            int[] expectedValues = [0, 1, 2, 3, 4];
            int[] actualValues = Enum.GetValues<DnsPolicyMode>()
                .Select(mode => (int)mode)
                .ToArray();

            CollectionAssert.AreEqual(expectedValues, actualValues);
        }

        private DnsCryptPreparedComponent CreatePreparedComponent()
        {
            string preparedDirectory = Path.Combine(root, "prepared-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(preparedDirectory);
            string preparedExecutable = Path.Combine(preparedDirectory, "dnscrypt-proxy.exe");
            File.WriteAllBytes(preparedExecutable, new byte[] { 0x4D, 0x5A });
            return new DnsCryptPreparedComponent(new Version(2, 1, 18), preparedDirectory, preparedExecutable);
        }

        private static DnsCryptConfig CreateDirectDnsCryptConfig()
            => new()
            {
                routeThroughShadowsocks = false,
            };

        private static DnsCryptRuntimeStartOptions CreateConcreteRuntimeOptions()
            => new(new DnsCryptConfig
            {
                routeThroughShadowsocks = false,
                automaticResolvers = false,
                serverNames = new List<string> { "test-resolver" },
            })
            {
                StaticResolverStamps = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["test-resolver"] = "sdns://test",
                },
            };

        private DnsCryptRuntimeManager CreateManager(
            FakeRuntimePlatform platform,
            bool health,
            int port,
            IReadOnlyList<TimeSpan> restartDelays = null,
            IDnsCryptResolverCatalogBootstrapper bootstrapper = null)
        {
            return new DnsCryptRuntimeManager(
                () => new DnsCryptComponentStatus(true, new Version(2, 1, 18), null, executablePath, true, null),
                runtimeRoot,
                platform,
                () => port,
                (_, _, _) => Task.FromResult(health),
                restartDelays ?? [TimeSpan.Zero],
                startupTimeout: TimeSpan.FromMilliseconds(40),
                resolverCatalogBootstrapper: bootstrapper);
        }

        private static async Task WaitUntilAsync(Func<bool> condition)
        {
            DateTime deadline = DateTime.UtcNow.AddSeconds(2);
            while (!condition())
            {
                if (DateTime.UtcNow >= deadline)
                    Assert.Fail("Timed out waiting for DNSCrypt runtime state change.");
                await Task.Delay(10);
            }
        }

        private sealed class FakeCatalogBootstrapper : IDnsCryptResolverCatalogBootstrapper
        {
            public int CallCount { get; private set; }
            public string LastHost { get; private set; } = string.Empty;
            public int LastPort { get; private set; }

            public Task EnsureFreshAsync(
                string cacheDirectory,
                string shadowsocksHost,
                int shadowsocksPort,
                CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                CallCount++;
                LastHost = shadowsocksHost;
                LastPort = shadowsocksPort;
                Directory.CreateDirectory(cacheDirectory);
                File.WriteAllText(Path.Combine(cacheDirectory, "public-resolvers.md"), "verified resolver cache");
                File.WriteAllText(Path.Combine(cacheDirectory, "public-resolvers.md.minisig"), "verified resolver signature");
                return Task.CompletedTask;
            }
        }

        private sealed class FakeRuntimePlatform : IDnsCryptRuntimePlatform
        {
            public string VersionOutput { get; set; } = "2.1.18";
            public int CheckExitCode { get; set; }
            public string CheckError { get; set; } = string.Empty;
            public bool CheckThrowsTimeout { get; set; }
            public string ListOutput { get; set; } = "[{\"name\":\"test-resolver\",\"proto\":\"DNSCrypt\",\"ipv6\":false,\"dnssec\":true,\"nolog\":true,\"nofilter\":true,\"description\":\"Test\",\"stamp\":\"sdns://test\"}]";
            public IReadOnlyList<string> RuntimeOutputLines { get; set; } = Array.Empty<string>();
            public bool ExitFirstProcessOnExitedSubscription { get; set; }
            public bool WriteResolverCacheOnList { get; set; }
            public List<IReadOnlyList<string>> Commands { get; } = new();
            public List<IReadOnlyList<string>> StartArguments { get; } = new();
            public List<FakeRunningProcess> Processes { get; } = new();
            public bool ListCommandHadResolverCache { get; private set; }
            public bool StartedWithResolverCache { get; private set; }
            public string StartedConfigText { get; private set; } = string.Empty;

            public Task<DnsCryptCommandResult> ExecuteAsync(
                string executablePath,
                string workingDirectory,
                IReadOnlyList<string> arguments,
                TimeSpan timeout,
                CancellationToken cancellationToken)
            {
                Commands.Add(arguments.ToArray());
                if (arguments.Contains("-list-all"))
                {
                    ListCommandHadResolverCache |=
                        File.Exists(Path.Combine(workingDirectory, "public-resolvers.md"))
                        && File.Exists(Path.Combine(workingDirectory, "public-resolvers.md.minisig"));
                    if (WriteResolverCacheOnList)
                    {
                        File.WriteAllText(Path.Combine(workingDirectory, "public-resolvers.md"), "signed resolver cache");
                        File.WriteAllText(Path.Combine(workingDirectory, "public-resolvers.md.minisig"), "signed resolver signature");
                    }
                }
                if (arguments.Contains("-version"))
                    return Task.FromResult(new DnsCryptCommandResult(0, VersionOutput, string.Empty));
                if (arguments.Contains("-list-all"))
                    return Task.FromResult(new DnsCryptCommandResult(0, ListOutput, string.Empty));
                if (arguments.Contains("-check") && CheckThrowsTimeout)
                    throw new TimeoutException("Synthetic dnscrypt-proxy -check timeout.");
                return Task.FromResult(new DnsCryptCommandResult(CheckExitCode, "Configuration successfully checked", CheckError));
            }

            public IDnsCryptRunningProcess Start(
                string executablePath,
                string workingDirectory,
                IReadOnlyList<string> arguments,
                Action<string> standardOutput,
                Action<string> standardError)
            {
                StartArguments.Add(arguments.ToArray());
                StartedWithResolverCache |=
                    File.Exists(Path.Combine(workingDirectory, "public-resolvers.md"))
                    && File.Exists(Path.Combine(workingDirectory, "public-resolvers.md.minisig"));
                int configIndex = arguments.ToList().FindIndex(argument => string.Equals(argument, "-config", StringComparison.Ordinal));
                if (configIndex >= 0 && configIndex + 1 < arguments.Count && File.Exists(arguments[configIndex + 1]))
                    StartedConfigText = File.ReadAllText(arguments[configIndex + 1]);
                var process = new FakeRunningProcess(
                    7000 + Processes.Count,
                    ExitFirstProcessOnExitedSubscription && Processes.Count == 0);
                Processes.Add(process);
                standardOutput?.Invoke("[NOTICE] dnscrypt-proxy test fixture");
                foreach (string line in RuntimeOutputLines)
                    standardOutput?.Invoke(line);
                return process;
            }
        }

        private sealed class FakeRunningProcess : IDnsCryptRunningProcess
        {
            private readonly bool exitOnExitedSubscription;
            private EventHandler exited;

            public FakeRunningProcess(int id, bool exitOnExitedSubscription = false)
            {
                Id = id;
                this.exitOnExitedSubscription = exitOnExitedSubscription;
            }

            public int Id { get; }
            public bool HasExited { get; private set; }
            public bool WasKilled { get; private set; }

            public event EventHandler Exited
            {
                add
                {
                    exited += value;
                    if (exitOnExitedSubscription && !HasExited)
                    {
                        HasExited = true;
                        value?.Invoke(this, EventArgs.Empty);
                    }
                }
                remove => exited -= value;
            }

            public void TriggerUnexpectedExit()
            {
                if (HasExited)
                    return;
                HasExited = true;
                exited?.Invoke(this, EventArgs.Empty);
            }

            public void Kill()
            {
                WasKilled = true;
                if (!HasExited)
                {
                    HasExited = true;
                    exited?.Invoke(this, EventArgs.Empty);
                }
            }

            public Task WaitForExitAsync(CancellationToken cancellationToken) => Task.CompletedTask;
            public void Dispose() { }
        }
    }
}
