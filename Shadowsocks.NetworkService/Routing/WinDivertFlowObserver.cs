#nullable enable
using System.ComponentModel;
using System.Runtime.InteropServices;
using Shadowsocks.NetworkService.WinDivert;

namespace Shadowsocks.NetworkService.Routing;

/// <summary>
/// Observes WinDivert FLOW events to learn endpoint ownership before NETWORK-layer
/// packets are classified. This removes most PID-attribution races; IP Helper table
/// lookup remains a fallback for flows that existed before this observer was opened.
/// </summary>
internal sealed class WinDivertFlowObserver : IAsyncDisposable
{
    private readonly CancellationTokenSource _shutdown = new();
    private nint _handle;
    private Task? _loop;

    public Task Completion => _loop ?? Task.CompletedTask;

    public void Start()
    {
        _handle = WinDivertNative.Open(
            "outbound and (tcp or udp)",
            WinDivertLayer.Flow,
            101,
            WinDivertFlags.Sniff | WinDivertFlags.ReceiveOnly);
        if (WinDivertNative.IsInvalidHandle(_handle))
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "WinDivert FLOW observer open failed.");

        _loop = Task.Run(ObserveLoop);
    }

    private unsafe void ObserveLoop()
    {
        while (!_shutdown.IsCancellationRequested)
        {
            if (!WinDivertNative.Receive(_handle, null, 0, out _, out WinDivertAddress address))
            {
                int error = Marshal.GetLastPInvokeError();
                if (_shutdown.IsCancellationRequested && error is 6 or 232 or 995)
                    return;
                throw new Win32Exception(error, "WinDivert FLOW observer receive failed.");
            }

            WinDivertDataFlow flow = address.Flow;
            int processId = unchecked((int)flow.ProcessId);
            if (processId <= 0 || flow.LocalPort == 0 || flow.Protocol is not 6 and not 17)
                continue;

            switch (address.Event)
            {
                case WinDivertEvent.FlowEstablished:
                    ProcessAttributionCache.Observe(flow.Protocol, flow.LocalPort, processId, DateTime.UtcNow);
                    break;
                case WinDivertEvent.FlowDeleted:
                    ProcessAttributionCache.Remove(flow.Protocol, flow.LocalPort, processId);
                    break;
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _shutdown.Cancel();
        if (!WinDivertNative.IsInvalidHandle(_handle))
        {
            WinDivertNative.Shutdown(_handle, WinDivertShutdown.Receive);
            WinDivertNative.Close(_handle);
            _handle = 0;
        }

        if (_loop is not null)
        {
            try
            {
                await _loop.ConfigureAwait(false);
            }
            catch
            {
            }
        }
        _shutdown.Dispose();
    }
}
