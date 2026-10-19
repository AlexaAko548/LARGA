using System;

namespace LARGA.SharedCore;

/// <summary>
/// How long personal data is kept (audit H-01, RA 10173 storage limitation). New documents carry an
/// <see cref="ExpireAtField"/> timestamp; a Firestore TTL policy on that field (set up in the Firebase
/// Console - see SECURITY_AUDIT.md, H-01) deletes them after that date. Nothing is deleted until that
/// policy is switched on, and documents written before this change have no expireAt and are kept.
///
/// PROPOSED PERIODS - confirm with BLM Taxi before enabling the TTL policy. firestore.rules checks the
/// same numbers (expiresAfterDays), so change both together.
/// </summary>
public static class RetentionRules
{
    public const string ExpireAtField = "expireAt";

    /// <summary>gps_telemetry: the 30-second location trail of a shift.</summary>
    public static readonly TimeSpan GpsTelemetry = TimeSpan.FromDays(90);

    /// <summary>emergency_alerts: SOS location, address and caller details. Stamped by ManagerWeb's
    /// SosPushService (server time), not by the phone.</summary>
    public static readonly TimeSpan EmergencyAlert = TimeSpan.FromDays(365);

    /// <summary>chats/{driverId}/messages.</summary>
    public static readonly TimeSpan ChatMessage = TimeSpan.FromDays(365);

    public static DateTime ExpireAt(DateTime createdUtc, TimeSpan keep) =>
        DateTime.SpecifyKind(createdUtc, DateTimeKind.Utc) + keep;
}
