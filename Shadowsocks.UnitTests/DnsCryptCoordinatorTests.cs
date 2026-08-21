using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Shadowsocks.Controller.Service;
using Shadowsocks.Controller.Traffic;

namespace Shadowsocks.UnitTests
{
    [TestClass]
    public class DnsCryptCoordinatorTests
    {
        [TestMethod]
        public async Task ManagementTransactionsAreSerialized()
        {
            var coordinator = new DnsCryptCoordinator();
            var order = new List<string>();
            var firstEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseFirst = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            Task first = coordinator.ExecuteExclusiveAsync(
                "first",
                async _ =>
                {
                    order.Add("first-enter");
                    firstEntered.SetResult(true);
                    await releaseFirst.Task;
                    order.Add("first-exit");
                });

            await firstEntered.Task;
            Task second = coordinator.ExecuteExclusiveAsync(
                "second",
                _ =>
                {
                    order.Add("second-enter");
                    return Task.CompletedTask;
                });

            await Task.Delay(25);
            CollectionAssert.AreEqual(new[] { "first-enter" }, order);
            Assert.IsTrue(coordinator.IsBusy);
            Assert.AreEqual("first", coordinator.CurrentOperation);

            releaseFirst.SetResult(true);
            await Task.WhenAll(first, second);

            CollectionAssert.AreEqual(
                new[] { "first-enter", "first-exit", "second-enter" },
                order);
            Assert.IsFalse(coordinator.IsBusy);
            Assert.IsNull(coordinator.CurrentOperation);
        }

        [TestMethod]
        public async Task WaitingOperationCanBeCancelledWithoutBreakingGate()
        {
            var coordinator = new DnsCryptCoordinator();
            var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            Task holder = coordinator.ExecuteExclusiveAsync(
                "holder",
                async _ =>
                {
                    entered.SetResult(true);
                    await release.Task;
                });
            await entered.Task;

            using var cancellation = new CancellationTokenSource();
            Task waiting = coordinator.ExecuteExclusiveAsync(
                "waiting",
                _ => Task.CompletedTask,
                cancellation.Token);
            cancellation.Cancel();

            await Assert.ThrowsAsync<OperationCanceledException>(async () => await waiting);
            release.SetResult(true);
            await holder;

            bool ran = false;
            await coordinator.ExecuteExclusiveAsync(
                "after",
                _ =>
                {
                    ran = true;
                    return Task.CompletedTask;
                });
            Assert.IsTrue(ran);
        }

        [TestMethod]
        public async Task LifecycleCancellationCancelsActiveTransactionAndGateRemainsReusable()
        {
            var coordinator = new DnsCryptCoordinator();
            var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            Task active = coordinator.ExecuteExclusiveAsync(
                "download",
                async token =>
                {
                    entered.SetResult(true);
                    await Task.Delay(Timeout.InfiniteTimeSpan, token);
                });
            await entered.Task;

            coordinator.CancelCurrentOperation();
            await Assert.ThrowsAsync<OperationCanceledException>(async () => await active);

            bool ran = false;
            await coordinator.ExecuteExclusiveAsync(
                "shutdown",
                _ =>
                {
                    ran = true;
                    return Task.CompletedTask;
                });
            Assert.IsTrue(ran);
            Assert.IsFalse(coordinator.IsBusy);
        }

        [TestMethod]
        public void DnsCryptRuntimeRunsWheneverDnsCryptPolicyIsSelected()
        {
            Assert.IsTrue(Shadowsocks.Controller.ShadowsocksController.ShouldRunDnsCryptRuntime(
                TrafficCaptureMode.User, DnsPolicyMode.DnsCrypt));
            Assert.IsTrue(Shadowsocks.Controller.ShadowsocksController.ShouldRunDnsCryptRuntime(
                TrafficCaptureMode.Admin, DnsPolicyMode.DnsCrypt));
            Assert.IsFalse(Shadowsocks.Controller.ShadowsocksController.ShouldRunDnsCryptRuntime(
                TrafficCaptureMode.Admin, DnsPolicyMode.System));
        }

        [TestMethod]
        public async Task SuspendCancelsActiveAndQueuedTransactionsUntilResume()
        {
            var coordinator = new DnsCryptCoordinator();
            var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            Task active = coordinator.ExecuteExclusiveAsync(
                "active",
                async token =>
                {
                    entered.SetResult(true);
                    await Task.Delay(Timeout.InfiniteTimeSpan, token);
                });
            await entered.Task;

            Task queued = coordinator.ExecuteExclusiveAsync(
                "queued",
                _ => Task.CompletedTask);
            bool cleanupRan = false;
            Task suspend = coordinator.SuspendAndExecuteAsync(
                "shutdown",
                _ =>
                {
                    cleanupRan = true;
                    return Task.CompletedTask;
                });

            await Assert.ThrowsAsync<OperationCanceledException>(async () => await active);
            await Assert.ThrowsAsync<OperationCanceledException>(async () => await queued);
            await suspend;
            Assert.IsTrue(cleanupRan);
            Assert.IsTrue(coordinator.IsSuspended);

            await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () =>
                await coordinator.ExecuteExclusiveAsync("blocked", _ => Task.CompletedTask));

            coordinator.Resume();
            bool resumed = false;
            await coordinator.ExecuteExclusiveAsync(
                "resumed",
                _ =>
                {
                    resumed = true;
                    return Task.CompletedTask;
                });
            Assert.IsTrue(resumed);
            Assert.IsFalse(coordinator.IsSuspended);
        }
    }
}
