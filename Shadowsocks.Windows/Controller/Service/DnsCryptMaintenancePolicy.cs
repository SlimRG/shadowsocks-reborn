#nullable enable
using System;
using Shadowsocks.Controller.Traffic;

namespace Shadowsocks.Controller.Service
{
    internal static class DnsCryptMaintenancePolicy
    {
        internal static readonly TimeSpan MinimumAutomaticCheckInterval = TimeSpan.FromHours(24);
        internal static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(30);
        internal static readonly TimeSpan IdleRecheckInterval = TimeSpan.FromMinutes(15);
        internal static readonly TimeSpan ClockSkewTolerance = TimeSpan.FromMinutes(10);

        internal static bool IsAutomaticUpdateEnabled(
            DnsCryptComponentStatus? component,
            DnsCryptConfig? config)
        {
            return component?.IsInstalled == true && config?.autoUpdate == true;
        }

        internal static bool IsAutomaticCheckDue(
            DnsCryptComponentStatus? component,
            DnsCryptConfig? config,
            DateTimeOffset nowUtc)
        {
            if (!IsAutomaticUpdateEnabled(component, config))
                return false;

            DateTimeOffset? lastCheckUtc = component!.LastUpdateCheckUtc;
            if (!lastCheckUtc.HasValue)
                return true;

            DateTimeOffset normalizedNow = nowUtc.ToUniversalTime();
            DateTimeOffset normalizedLast = lastCheckUtc.Value.ToUniversalTime();
            if (normalizedLast > normalizedNow + ClockSkewTolerance)
            {
                // A persisted timestamp far in the future indicates that the wall clock was
                // corrected backwards. Allow one check now; RecordUpdateCheck() will persist
                // the corrected timestamp before any network request is made.
                return true;
            }
            if (normalizedLast > normalizedNow)
                return false;

            return normalizedNow - normalizedLast >= MinimumAutomaticCheckInterval;
        }

        internal static TimeSpan GetDelayUntilNextAutomaticCheck(
            DnsCryptComponentStatus? component,
            DnsCryptConfig? config,
            DateTimeOffset nowUtc)
        {
            if (!IsAutomaticUpdateEnabled(component, config))
                return IdleRecheckInterval;

            DateTimeOffset? lastCheckUtc = component!.LastUpdateCheckUtc;
            if (!lastCheckUtc.HasValue)
                return TimeSpan.Zero;

            DateTimeOffset normalizedNow = nowUtc.ToUniversalTime();
            DateTimeOffset normalizedLast = lastCheckUtc.Value.ToUniversalTime();
            if (normalizedLast > normalizedNow + ClockSkewTolerance)
                return TimeSpan.Zero;
            if (normalizedLast > normalizedNow)
                normalizedLast = normalizedNow;

            TimeSpan remaining = MinimumAutomaticCheckInterval - (normalizedNow - normalizedLast);
            if (remaining <= TimeSpan.Zero)
                return TimeSpan.Zero;

            // Re-evaluate local settings periodically so toggling Auto Update does not require
            // restarting the application, while GitHub itself is still contacted at most once
            // per 24-hour metadata interval.
            return remaining < IdleRecheckInterval ? remaining : IdleRecheckInterval;
        }
    }
}
