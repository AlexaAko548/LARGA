using System;
using System.Collections.Generic;

namespace LARGA.SharedCore.Emergency;

/// <summary>
/// Audit C-03: one driver's repeated SOS within <see cref="Window"/> is saved and shown (SOS
/// Dispatch, Alert Center) but not pushed again to every manager's phone - unless it's more severe
/// than the one already pushed (e.g. a Standard SOS followed by a Crash). Used by ManagerWeb's
/// SosPushService; one instance runs, so this memory is the whole picture.
/// </summary>
public sealed class SosRepeatPushFilter
{
    public static readonly TimeSpan Window = TimeSpan.FromSeconds(60);

    private readonly Dictionary<string, (DateTime At, int Severity)> _lastPushByDriver = new();
    private readonly object _lock = new();

    /// <summary>True when this driver had an SOS pushed within <see cref="Window"/> at the same or
    /// higher severity. An alert with no driverId is always pushed.</summary>
    public bool IsRepeat(string driverId, int severity, DateTime nowUtc)
    {
        if (string.IsNullOrEmpty(driverId)) return false;

        lock (_lock)
        {
            return _lastPushByDriver.TryGetValue(driverId, out var last)
                && nowUtc - last.At < Window
                && severity <= last.Severity;
        }
    }

    /// <summary>Remembers a push that reached at least one phone - a failed one never holds back the next SOS.</summary>
    public void Remember(string driverId, int severity, DateTime nowUtc)
    {
        if (string.IsNullOrEmpty(driverId)) return;

        lock (_lock)
        {
            _lastPushByDriver[driverId] = (nowUtc, severity);
        }
    }
}
