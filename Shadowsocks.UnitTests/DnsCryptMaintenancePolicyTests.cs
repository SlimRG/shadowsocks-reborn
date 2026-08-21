#nullable enable
using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Shadowsocks.Controller.Service;
using Shadowsocks.Controller.Traffic;

namespace Shadowsocks.UnitTests
{
    [TestClass]
    public class DnsCryptMaintenancePolicyTests
    {
        private static readonly DateTimeOffset Now = new(2026, 8, 18, 12, 0, 0, TimeSpan.Zero);

        [TestMethod]
        public void AutomaticCheckRequiresInstalledComponentAndEnabledSetting()
        {
            DnsCryptConfig enabled = new() { autoUpdate = true };
            DnsCryptConfig disabled = new() { autoUpdate = false };
            DnsCryptComponentStatus installed = Status(lastCheck: null);
            DnsCryptComponentStatus missing = new(false, null, null, null, true, null);

            Assert.IsTrue(DnsCryptMaintenancePolicy.IsAutomaticCheckDue(installed, enabled, Now));
            Assert.IsFalse(DnsCryptMaintenancePolicy.IsAutomaticCheckDue(installed, disabled, Now));
            Assert.IsFalse(DnsCryptMaintenancePolicy.IsAutomaticCheckDue(missing, enabled, Now));
        }

        [TestMethod]
        public void AutomaticCheckIsNeverMoreFrequentThanTwentyFourHours()
        {
            DnsCryptConfig config = new() { autoUpdate = true };
            Assert.IsFalse(DnsCryptMaintenancePolicy.IsAutomaticCheckDue(
                Status(Now - TimeSpan.FromHours(23) - TimeSpan.FromMinutes(59)), config, Now));
            Assert.IsTrue(DnsCryptMaintenancePolicy.IsAutomaticCheckDue(
                Status(Now - TimeSpan.FromHours(24)), config, Now));
        }

        [TestMethod]
        public void SmallFutureClockSkewDoesNotTriggerImmediateAutomaticCheck()
        {
            DnsCryptConfig config = new() { autoUpdate = true };
            DnsCryptComponentStatus status = Status(Now + TimeSpan.FromMinutes(5));

            Assert.IsFalse(DnsCryptMaintenancePolicy.IsAutomaticCheckDue(status, config, Now));
            Assert.AreEqual(
                DnsCryptMaintenancePolicy.IdleRecheckInterval,
                DnsCryptMaintenancePolicy.GetDelayUntilNextAutomaticCheck(status, config, Now));
        }

        [TestMethod]
        public void LargeFutureClockSkewIsRepairedByRunningOneCheckNow()
        {
            DnsCryptConfig config = new() { autoUpdate = true };
            DnsCryptComponentStatus status = Status(Now + TimeSpan.FromDays(365));

            Assert.IsTrue(DnsCryptMaintenancePolicy.IsAutomaticCheckDue(status, config, Now));
            Assert.AreEqual(
                TimeSpan.Zero,
                DnsCryptMaintenancePolicy.GetDelayUntilNextAutomaticCheck(status, config, Now));
        }

        [TestMethod]
        public void DueCheckReturnsZeroDelayAndDisabledModeOnlyPollsLocalState()
        {
            Assert.AreEqual(
                TimeSpan.Zero,
                DnsCryptMaintenancePolicy.GetDelayUntilNextAutomaticCheck(
                    Status(null), new DnsCryptConfig { autoUpdate = true }, Now));
            Assert.AreEqual(
                DnsCryptMaintenancePolicy.IdleRecheckInterval,
                DnsCryptMaintenancePolicy.GetDelayUntilNextAutomaticCheck(
                    Status(null), new DnsCryptConfig { autoUpdate = false }, Now));
        }

        private static DnsCryptComponentStatus Status(DateTimeOffset? lastCheck)
        {
            return new DnsCryptComponentStatus(
                true,
                new Version(2, 1, 18),
                null,
                @"C:\DNSCrypt\dnscrypt-proxy.exe",
                true,
                lastCheck);
        }
    }
}
