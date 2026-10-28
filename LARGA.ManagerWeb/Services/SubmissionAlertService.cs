using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FirebaseAdmin;
using FirebaseAdmin.Auth;
using FirebaseAdmin.Messaging;
using Google.Cloud.Firestore;
using LARGA.Shared.Models.Entities;
using LARGA.SharedCore;
using LARGA.SharedCore.Alerts;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LARGA.ManagerWeb.Services;

/// <summary>
/// SRS REQ-4.3-7 / REQ-4.4-8: one system_alerts row (SubmissionAlerts) for every new driver defect
/// report and every fuel submission, so the manager hears about each - not only the flagged ones.
/// Two live listeners, same pattern as FuelAutoCheckService / SosPushService:
///  - maintenance_logs with status "Reported": a new one gets a "DefectReported" alert and an FCM push
///    to every Manager / Assistant Manager phone; when it leaves "Reported" (ticket created, dismissed)
///    its alert is marked read.
///  - fuel_logs submitted since <see cref="SubmissionAlerts.Lookback"/> before startup: each gets a
///    "FuelSubmitted" alert (bell only, no push); once a manager has verified, flagged or resolved it,
///    the alert is marked read (FuelVerificationService and the phone's Alert Center also do that).
/// Alerts are created with Create (deterministic IDs), so a restart or a second instance never
/// duplicates one or pushes twice. Only items from the last 24 hours get an alert on the first
/// snapshot. Never throws out of the host - failures are logged and the listeners restarted.
/// </summary>
public class SubmissionAlertService : BackgroundService
{
    // Must match the FirebaseApp name created with the FirebaseAuth registration in Program.cs.
    private const string FirebaseAppName = "LargaManagerWeb";
    private static readonly TimeSpan RestartDelay = TimeSpan.FromSeconds(15);

    private readonly Lazy<FirestoreDb> _db;
    private readonly Lazy<FirebaseAuth> _auth;
    private readonly ILogger<SubmissionAlertService> _logger;
    private DateTime _startedUtc;

    public SubmissionAlertService(Lazy<FirestoreDb> db, Lazy<FirebaseAuth> auth, ILogger<SubmissionAlertService> logger)
    {
        _db = db;
        _auth = auth;
        _logger = logger;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _startedUtc = DateTime.UtcNow;
        return Task.WhenAll(
            RunListenerAsync("defect report", () => _db.Value.Collection("maintenance_logs")
                .WhereEqualTo("status", "Reported"), OnDefectsAsync, stoppingToken),
            RunListenerAsync("fuel submission", () => _db.Value.Collection("fuel_logs")
                .WhereGreaterThanOrEqualTo("submittedAt", _startedUtc - SubmissionAlerts.Lookback), OnFuelLogsAsync, stoppingToken));
    }

    private async Task RunListenerAsync(string name, Func<Query> query, Func<QuerySnapshot, CancellationToken, Task> onSnapshot,
        CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            FirestoreChangeListener? listener = null;
            try
            {
                listener = query().Listen(onSnapshot, stoppingToken);
                await listener.ListenerTask;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "The {Name} alert listener stopped; restarting in {Delay}.", name, RestartDelay);
            }
            finally
            {
                if (listener is not null)
                {
                    try { await listener.StopAsync(); } catch { /* already stopped */ }
                }
            }

            try
            {
                await Task.Delay(RestartDelay, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task OnDefectsAsync(QuerySnapshot snapshot, CancellationToken token)
    {
        foreach (DocumentChange change in snapshot.Changes)
        {
            if (token.IsCancellationRequested) return;
            string id = change.Document.Id;
            try
            {
                if (change.ChangeType == DocumentChange.Type.Removed)
                {
                    // No longer "Reported": the Garage made a ticket or dismissed it.
                    await SubmissionAlerts.MarkReviewedAsync(_db.Value, SubmissionAlerts.DefectAlertId(id));
                    continue;
                }

                MaintenanceRecord report = change.Document.ConvertTo<MaintenanceRecord>();
                if (!SubmissionAlerts.IsRecent(report.DateLogged, DateTime.UtcNow))
                {
                    continue;
                }

                string driverId = report.ReportedByDriverId ?? string.Empty;
                string driverName = await DriverNameAsync(driverId);
                string priority = new PriorityLevelConverter().ToFirestore(report.PriorityLevel)?.ToString() ?? string.Empty;
                string message = SubmissionAlerts.DefectMessage(driverName, report.TaxiId, report.IssueTitle, priority);

                if (await CreateAlertAsync(SubmissionAlerts.DefectAlertId(id), SubmissionAlerts.DefectReportedType,
                        driverId, driverName, report.TaxiId, report.ShiftId, message, report.DateLogged))
                {
                    await PushToStaffAsync($"Defect report - {report.TaxiId}", message, id, token);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Defect report alert for maintenance_logs/{Id} failed", id);
            }
        }
    }

    private async Task OnFuelLogsAsync(QuerySnapshot snapshot, CancellationToken token)
    {
        foreach (DocumentChange change in snapshot.Changes)
        {
            if (token.IsCancellationRequested) return;
            if (change.ChangeType == DocumentChange.Type.Removed) continue;
            string id = change.Document.Id;
            try
            {
                FuelLog log = change.Document.ConvertTo<FuelLog>();
                string alertId = SubmissionAlerts.FuelSubmittedAlertId(id);

                if (change.ChangeType == DocumentChange.Type.Added)
                {
                    string driverName = await DriverNameAsync(log.DriverId);
                    string taxiId = await TaxiOfShiftAsync(log.ShiftId);
                    await CreateAlertAsync(alertId, SubmissionAlerts.FuelSubmittedType, log.DriverId, driverName, taxiId, log.ShiftId,
                        SubmissionAlerts.FuelMessage(driverName, taxiId, (double)log.LitersRefueled, (double)log.FuelCost, log.FuelStation),
                        log.SubmittedAt ?? DateTime.UtcNow);
                }

                // Reviewed: verified, or flagged by a manager (not the automatic flag on submit).
                if (log.VerificationStatus == FuelVerificationStatus.Verified
                    || (log.VerificationStatus == FuelVerificationStatus.Flagged && !log.AutoFlagged))
                {
                    await SubmissionAlerts.MarkReviewedAsync(_db.Value, alertId);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Fuel submission alert for fuel_logs/{Id} failed", id);
            }
        }
    }

    /// <summary>Creates the alert; false when it already exists (alerted before a restart).</summary>
    private async Task<bool> CreateAlertAsync(string alertId, string type, string? driverId, string driverName, string taxiId,
        string? shiftId, string message, DateTime timestampUtc)
    {
        try
        {
            await _db.Value.Collection(AlertFeed.Collection).Document(alertId).CreateAsync(new Dictionary<string, object>
            {
                ["type"] = type,
                ["driverId"] = driverId ?? string.Empty,
                ["driverName"] = driverName,
                ["taxiId"] = taxiId,
                ["unitLabel"] = taxiId,
                ["shiftId"] = shiftId ?? string.Empty,
                ["message"] = message,
                ["timestamp"] = DateTime.SpecifyKind(timestampUtc, DateTimeKind.Utc),
                ["isRead"] = false,
            });
            return true;
        }
        catch (Grpc.Core.RpcException ex) when (ex.StatusCode == Grpc.Core.StatusCode.AlreadyExists)
        {
            return false;
        }
    }

    private async Task<string> DriverNameAsync(string? driverId)
    {
        if (string.IsNullOrWhiteSpace(driverId)) return "A driver";
        DocumentSnapshot user = await _db.Value.Collection("users").Document(driverId).GetSnapshotAsync();
        return user.Exists && user.TryGetValue("fullName", out string? name) && !string.IsNullOrWhiteSpace(name) ? name : "A driver";
    }

    private async Task<string> TaxiOfShiftAsync(string? shiftId)
    {
        if (string.IsNullOrWhiteSpace(shiftId)) return "unknown unit";
        DocumentSnapshot shift = await _db.Value.Collection("shifts").Document(shiftId).GetSnapshotAsync();
        return shift.Exists && shift.TryGetValue("taxiId", out string? taxi) && !string.IsNullOrWhiteSpace(taxi) ? taxi : "unknown unit";
    }

    /// <summary>A defect report push to every Manager / Assistant Manager phone (users.fcmToken, saved by
    /// the manager app's dashboard). Best effort: a failure is only logged - the bell row is there.</summary>
    private async Task PushToStaffAsync(string title, string body, string maintenanceId, CancellationToken token)
    {
        try
        {
            QuerySnapshot users = await _db.Value.Collection("users").GetSnapshotAsync(token);
            List<string> tokens = users.Documents
                .Where(doc => doc.TryGetValue("role", out object? role) && StaffRoles.IsStaff(role?.ToString()))
                .Select(doc => doc.TryGetValue("fcmToken", out string? fcm) ? fcm : null)
                .Where(fcm => !string.IsNullOrWhiteSpace(fcm))
                .Select(fcm => fcm!)
                .Distinct()
                .Take(500)
                .ToList();
            if (tokens.Count == 0) return;

            _ = _auth.Value; // creates the shared FirebaseApp on first use
            FirebaseMessaging messaging = FirebaseMessaging.GetMessaging(FirebaseApp.GetInstance(FirebaseAppName));
            BatchResponse response = await messaging.SendEachForMulticastAsync(new MulticastMessage
            {
                // users/{id}.fcmToken is a registration token, so Tokens (not Fids) - see SosPushService.
#pragma warning disable CS0618
                Tokens = tokens,
#pragma warning restore CS0618
                Notification = new Notification { Title = title, Body = body },
                Data = new Dictionary<string, string>
                {
                    ["type"] = SubmissionAlerts.DefectPushType,
                    ["maintenanceId"] = maintenanceId,
                },
            }, token);

            if (response.FailureCount > 0)
            {
                _logger.LogWarning("Defect report push failed for {Failed}/{Total} staff device(s) (stale tokens?).",
                    response.FailureCount, tokens.Count);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Defect report push for maintenance_logs/{Id} failed", maintenanceId);
        }
    }
}
