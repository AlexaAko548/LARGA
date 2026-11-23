using System.Collections.Concurrent;
using FirebaseAdmin.Auth;
using Google.Cloud.Firestore;
using LARGA.Shared.Models.Entities;
using LARGA.SharedCore;
using LARGA.SharedCore.Alerts;

namespace LARGA.ManagerWeb.Services;

/// <summary>
/// Requests for Assistant Manager access, made from the public Request Access page (/register) and
/// decided by the owner in Team &amp; Access (profile menu).
///
/// The requester chooses their own password on the form. It's handed straight to Firebase Auth, which
/// keeps only a hash - LARGA never stores it - on a login created <b>disabled</b> and with no users/{uid}
/// profile, so it can't sign in to anything (audit B-01: public sign-up must not grant access). The
/// owner's approval enables it and gives it the Assistant Manager role; declining deletes it.
/// Requests are stored in access_requests, which firestore.rules give no client access to (only this
/// server, through the Admin SDK, reads and writes it).
/// </summary>
public class AccessRequestService
{
    public const string Collection = "access_requests";
    public const string PendingStatus = "Pending";
    public const string ApprovedStatus = "Approved";
    public const string DeclinedStatus = "Declined";

    public const int MaxMessageLength = 300;
    public const int MaxReasonLength = 200;

    // A public form: limits so it can't be used to flood the owner's list (or Firebase Auth). Past
    // MaxPending open requests, new ones are turned away until the owner works through the list;
    // each connection (IP) can only send a few in a short time.
    private const int MaxPending = 25;
    private const int MaxPerIpPerWindow = 3;
    private static readonly TimeSpan IpWindow = TimeSpan.FromMinutes(15);
    private static readonly ConcurrentDictionary<string, List<DateTime>> RecentByIp = new();

    private readonly Lazy<FirestoreDb> _db;
    private readonly Lazy<FirebaseAuth> _auth;
    private readonly ILogger<AccessRequestService> _logger;

    public AccessRequestService(Lazy<FirestoreDb> db, Lazy<FirebaseAuth> auth, ILogger<AccessRequestService> logger)
    {
        _db = db;
        _auth = auth;
        _logger = logger;
    }

    private FirestoreDb Db => _db.Value;

    public record AccessRequest(string Id, string FullName, string Email, string PhoneNumber, string Message, DateTime RequestedAt);

    public record StaffMember(string Uid, string FullName, string Email, string PhoneNumber, DateTime? DateJoined);

    /// <summary>
    /// Files a request and its (disabled) login. Null when it was accepted - including when the email
    /// already has an open request or an account (nothing new is filed then), so the page never
    /// reveals whether an address is known. Otherwise a message for the requester.
    /// </summary>
    public async Task<string?> SubmitAsync(string fullName, string email, string phoneNumber, string password, string? message, string? clientIp)
    {
        fullName = InputValidator.NormalizeSpaces(fullName);
        string? error = InputValidator.ValidateFullName(fullName)
            ?? InputValidator.ValidateEmail(email)
            ?? InputValidator.ValidatePhilippineMobile(phoneNumber)
            ?? InputValidator.ValidatePassword(password)
            ?? InputValidator.ValidateText(message, "the note", MaxMessageLength);
        if (error is not null)
        {
            return error;
        }

        if (!TryCountSubmission(clientIp))
        {
            return "Too many requests from this connection. Please wait a few minutes, then try again.";
        }

        string normalizedEmail = email.Trim().ToLowerInvariant();
        UserRecord? login = null;
        try
        {
            QuerySnapshot pending = await Db.Collection(Collection).WhereEqualTo("status", PendingStatus).GetSnapshotAsync();
            if (pending.Documents.Any(d => d.TryGetValue("email", out string e) && e == normalizedEmail))
            {
                return null; // already waiting for the owner
            }
            if (pending.Count >= MaxPending)
            {
                return "We can't take new requests right now. Please contact BLM Taxi's owner directly.";
            }

            try
            {
                login = await _auth.Value.CreateUserAsync(new UserRecordArgs
                {
                    Email = normalizedEmail,
                    Password = password,
                    DisplayName = fullName,
                    Disabled = true,
                });
            }
            catch (FirebaseAuthException ex) when (ex.AuthErrorCode == AuthErrorCode.EmailAlreadyExists)
            {
                return null; // already has an account - same answer, nothing filed
            }

            DateTime now = DateTime.UtcNow;
            DocumentReference requestDoc = Db.Collection(Collection).Document();
            WriteBatch batch = Db.StartBatch();
            batch.Create(requestDoc, new Dictionary<string, object>
            {
                ["fullName"] = fullName,
                ["email"] = normalizedEmail,
                ["phoneNumber"] = InputValidator.NormalizePhilippineMobile(phoneNumber)!,
                ["message"] = InputValidator.NormalizeSpaces(message),
                ["requestedRole"] = StaffRoles.AssistantManager,
                ["status"] = PendingStatus,
                ["requestedAt"] = now,
                ["staffUid"] = login.Uid,
            });
            // The owner's notification bell (web only - see AlertFeed.ShownOnPhone).
            batch.Set(Db.Collection(AlertFeed.Collection).Document(BellId(requestDoc.Id)), new SystemAlert
            {
                Type = AlertFeed.AccessRequestType,
                DriverName = fullName,
                Message = $"{fullName} ({normalizedEmail}) is requesting Assistant Manager access. Review it in Team & Access, from your profile menu.",
                Timestamp = now,
                IsRead = false,
            });
            await batch.CommitAsync();
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to file an access request for {Email}", normalizedEmail);
            if (login is not null)
            {
                await DeleteLoginQuietlyAsync(login.Uid);
            }
            return "Your request couldn't be sent. Please try again in a moment.";
        }
    }

    /// <summary>How many requests are waiting - the badge on the profile menu.</summary>
    public async Task<int> CountPendingAsync()
    {
        AggregateQuerySnapshot count = await Db.Collection(Collection).WhereEqualTo("status", PendingStatus).Count().GetSnapshotAsync();
        return (int)(count.Count ?? 0);
    }

    /// <summary>Waiting requests, oldest first (sorted here, so no composite index is needed).</summary>
    public async Task<List<AccessRequest>> GetPendingAsync()
    {
        QuerySnapshot snapshot = await Db.Collection(Collection).WhereEqualTo("status", PendingStatus).GetSnapshotAsync();
        return snapshot.Documents
            .Select(d => new AccessRequest(
                d.Id,
                Field(d, "fullName"),
                Field(d, "email"),
                Field(d, "phoneNumber"),
                Field(d, "message"),
                d.TryGetValue("requestedAt", out DateTime at) ? at : DateTime.MinValue))
            .OrderBy(r => r.RequestedAt)
            .ToList();
    }

    /// <summary>Everyone who currently has Assistant Manager access.</summary>
    public async Task<List<StaffMember>> GetAssistantManagersAsync()
    {
        QuerySnapshot snapshot = await Db.Collection("users").WhereEqualTo("role", StaffRoles.AssistantManager).GetSnapshotAsync();
        return snapshot.Documents
            .Select(d =>
            {
                UserProfile p = d.ConvertTo<UserProfile>();
                return new StaffMember(d.Id, p.FullName, p.Email, p.PhoneNumber, p.DateJoined);
            })
            .OrderBy(s => s.FullName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Gives a waiting request Assistant Manager access: enables the login it was filed with and
    /// writes its users/{uid} profile, request status and audit entry. They sign in with the
    /// password they chose. Null when done, otherwise why not.
    /// </summary>
    public async Task<string?> ApproveAsync(string requestId, string actorUserId)
    {
        DocumentReference requestDoc = Db.Collection(Collection).Document(requestId);
        DocumentSnapshot? request = await TryGetAsync(requestDoc);
        if (request is null)
        {
            return CouldNotLoadMessage;
        }
        if (!request.Exists || Field(request, "status") != PendingStatus)
        {
            return "This request was already handled. Reopen Team & Access to see the latest list.";
        }

        string uid = Field(request, "staffUid");
        string fullName = Field(request, "fullName");
        string email = Field(request, "email");
        if (uid.Length == 0)
        {
            return "This request has no login attached (it was sent before passwords were added to the form). Decline it and ask them to send a new one.";
        }

        try
        {
            await _auth.Value.UpdateUserAsync(new UserRecordArgs { Uid = uid, Disabled = false });
        }
        catch (FirebaseAuthException ex) when (ex.AuthErrorCode == AuthErrorCode.UserNotFound)
        {
            return "This request's login no longer exists. Decline it and ask them to send a new request.";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to enable the login of access request {RequestId}", requestId);
            return "The account couldn't be enabled. Please try again.";
        }

        DateTime now = DateTime.UtcNow;
        try
        {
            WriteBatch batch = Db.StartBatch();
            batch.Set(Db.Collection("users").Document(uid), new UserProfile
            {
                FullName = fullName,
                Email = email,
                PhoneNumber = Field(request, "phoneNumber"),
                Role = StaffRoles.AssistantManager,
                DateJoined = now,
            }, SetOptions.Overwrite);
            batch.Update(requestDoc, new Dictionary<string, object>
            {
                ["status"] = ApprovedStatus,
                ["decidedAt"] = now,
                ["decidedBy"] = actorUserId,
            }, Precondition.LastUpdated(request.UpdateTime!.Value));
            batch.Create(Db.Collection("audit_logs").Document(), new AuditLog
            {
                UserId = actorUserId,
                ActionType = "StaffAccessApproved",
                AuditLogDetails = $"Approved Assistant Manager access for '{fullName}' ({email}).",
                Timestamp = now,
            });
            batch.Set(Db.Collection(AlertFeed.Collection).Document(BellId(requestId)), new Dictionary<string, object> { ["isRead"] = true }, SetOptions.MergeAll);
            await batch.CommitAsync();
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Saving the approval of request {RequestId} failed; disabling its login again", requestId);
            try
            {
                await _auth.Value.UpdateUserAsync(new UserRecordArgs { Uid = uid, Disabled = true });
            }
            catch (Exception cleanupEx)
            {
                _logger.LogWarning(cleanupEx, "Also failed to disable login {Uid} again", uid);
            }
            return "The approval couldn't be saved (it may have been handled at the same time). Please reopen Team & Access and try again.";
        }
    }

    /// <summary>Turns a request down and deletes the disabled login it was filed with. Null when saved,
    /// otherwise why not.</summary>
    public async Task<string?> DeclineAsync(string requestId, string? reason, string actorUserId)
    {
        if (InputValidator.ValidateText(reason, "the reason", MaxReasonLength) is string error)
        {
            return error;
        }

        DocumentReference requestDoc = Db.Collection(Collection).Document(requestId);
        DocumentSnapshot? request = await TryGetAsync(requestDoc);
        if (request is null)
        {
            return CouldNotLoadMessage;
        }
        if (!request.Exists || Field(request, "status") != PendingStatus)
        {
            return "This request was already handled. Reopen Team & Access to see the latest list.";
        }

        string trimmed = InputValidator.NormalizeSpaces(reason);
        DateTime now = DateTime.UtcNow;
        WriteBatch batch = Db.StartBatch();
        batch.Update(requestDoc, new Dictionary<string, object>
        {
            ["status"] = DeclinedStatus,
            ["decidedAt"] = now,
            ["decidedBy"] = actorUserId,
            ["declineReason"] = trimmed,
        }, Precondition.LastUpdated(request.UpdateTime!.Value));
        batch.Create(Db.Collection("audit_logs").Document(), new AuditLog
        {
            UserId = actorUserId,
            ActionType = "StaffAccessDeclined",
            AuditLogDetails = $"Declined Assistant Manager access for '{Field(request, "fullName")}' ({Field(request, "email")})"
                + (trimmed.Length > 0 ? $". Reason: {trimmed}" : "."),
            Timestamp = now,
        });
        batch.Set(Db.Collection(AlertFeed.Collection).Document(BellId(requestId)), new Dictionary<string, object> { ["isRead"] = true }, SetOptions.MergeAll);
        try
        {
            await batch.CommitAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to decline access request {RequestId}", requestId);
            return "The request couldn't be updated (it may have been handled at the same time). Please reopen Team & Access.";
        }

        // The declined person can request again later with the same email.
        if (Field(request, "staffUid") is { Length: > 0 } uid)
        {
            await DeleteLoginQuietlyAsync(uid);
        }
        return null;
    }

    /// <summary>
    /// Takes away an Assistant Manager's access: their profile keeps its name (so Audit Logs still
    /// show who did what) but loses the role, and their login is deleted - any open session ends on
    /// its next check (ManagerSignInService), and the email can be used for a new request later.
    /// Null when done, otherwise why not.
    /// </summary>
    public async Task<string?> RemoveAssistantManagerAsync(string uid, string actorUserId)
    {
        DocumentReference profileDoc = Db.Collection("users").Document(uid);
        DocumentSnapshot? profile = await TryGetAsync(profileDoc);
        if (profile is null)
        {
            return CouldNotLoadMessage;
        }
        if (!profile.Exists || !StaffRoles.IsAssistantManager(Field(profile, "role")))
        {
            return "This person no longer has Assistant Manager access.";
        }

        string fullName = Field(profile, "fullName");
        string email = Field(profile, "email");
        DateTime now = DateTime.UtcNow;
        try
        {
            WriteBatch batch = Db.StartBatch();
            batch.Update(profileDoc, new Dictionary<string, object>
            {
                ["role"] = string.Empty,
                ["accessRemovedAt"] = now,
                ["accessRemovedBy"] = actorUserId,
            }, Precondition.LastUpdated(profile.UpdateTime!.Value));
            batch.Create(Db.Collection("audit_logs").Document(), new AuditLog
            {
                UserId = actorUserId,
                ActionType = "StaffAccessRemoved",
                AuditLogDetails = $"Removed Assistant Manager access for '{fullName}' ({email}).",
                Timestamp = now,
            });
            await batch.CommitAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to remove Assistant Manager access for {Uid}", uid);
            return "Access couldn't be removed. Please try again.";
        }

        await DeleteLoginQuietlyAsync(uid);
        return null;
    }

    private async Task DeleteLoginQuietlyAsync(string uid)
    {
        try
        {
            await _auth.Value.DeleteUserAsync(uid);
        }
        catch (FirebaseAuthException ex) when (ex.AuthErrorCode == AuthErrorCode.UserNotFound)
        {
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to delete login {Uid}", uid);
        }
    }

    private const string CouldNotLoadMessage = "Couldn't reach the server. Check your connection, then try again.";

    /// <summary>The document, or null when Firestore couldn't be reached - so a network blip shows a
    /// message in Team &amp; Access instead of breaking the page.</summary>
    private async Task<DocumentSnapshot?> TryGetAsync(DocumentReference doc)
    {
        try
        {
            return await doc.GetSnapshotAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read {Path}", doc.Path);
            return null;
        }
    }

    private static string BellId(string requestId) => $"{requestId}_ACCESS";

    private static string Field(DocumentSnapshot doc, string name) =>
        doc.TryGetValue(name, out string value) ? value ?? string.Empty : string.Empty;

    /// <summary>At most <see cref="MaxPerIpPerWindow"/> requests per IP per <see cref="IpWindow"/>.</summary>
    private static bool TryCountSubmission(string? clientIp)
    {
        string key = string.IsNullOrWhiteSpace(clientIp) ? "unknown" : clientIp;
        DateTime now = DateTime.UtcNow;
        List<DateTime> times = RecentByIp.GetOrAdd(key, _ => new List<DateTime>());
        lock (times)
        {
            times.RemoveAll(t => now - t > IpWindow);
            if (times.Count >= MaxPerIpPerWindow)
            {
                return false;
            }
            times.Add(now);
            return true;
        }
    }
}
