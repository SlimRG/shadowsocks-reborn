#nullable enable
using System;

namespace Shadowsocks.Controller.Service
{
    public sealed record DnsCryptTrayPresentation(string LocalizationKey, Version? Version = null);

    public static class DnsCryptPresentation
    {
        public static DnsCryptTrayPresentation GetTrayPresentation(DnsCryptManagementStatus status)
        {
            ArgumentNullException.ThrowIfNull(status);
            if (!status.Component.IsInstalled)
                return new DnsCryptTrayPresentation("DNSCrypt: Not installed");

            DnsCryptRuntimeState runtimeState = status.Runtime.State;
            if (runtimeState == DnsCryptRuntimeState.Updating)
                return new DnsCryptTrayPresentation("DNSCrypt: Updating…");
            if (runtimeState == DnsCryptRuntimeState.Starting)
                return new DnsCryptTrayPresentation("DNSCrypt: Starting…");
            if (runtimeState == DnsCryptRuntimeState.Failed)
                return new DnsCryptTrayPresentation("DNSCrypt: Failed");
            if (status.UpdateAvailable)
                return new DnsCryptTrayPresentation("DNSCrypt: Update available · {0}", status.LatestVersion);

            return runtimeState == DnsCryptRuntimeState.Running
                ? new DnsCryptTrayPresentation("DNSCrypt: Running · {0}", status.Component.ActiveVersion)
                : new DnsCryptTrayPresentation("DNSCrypt: Stopped · {0}", status.Component.ActiveVersion);
        }
    }
}
