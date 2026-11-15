using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace LARGA.SharedCore.Push;

/// <summary>
/// Where push tokens live and which device a push may go to. Every signed-in device has one doc,
/// users/{uid}/fcmTokens/{DocId(token)} = { token, platform, deviceId, lastSeen }, which only that
/// user can read or write (firestore.rules) - no one else can read another user's tokens, and the
/// tokens can't be listed across users from a client.
///
/// A token belongs to exactly one account: the one that registered it last (lastSeen). Taxi phones
/// are shared, so when driver B signs in on driver A's old phone the token moves to B -
/// ManagerWeb's PushTokenOwnershipService deletes A's copy, and until it has, the send side
/// (<see cref="Deliverable"/>) already ignores it. Two accounts that registered the same token at
/// the same moment both lose it until one registers again.
/// </summary>
public static class PushTokenRules
{
    public const string Collection = "fcmTokens";
    public const string TokenField = "token";
    public const string PlatformField = "platform";
    public const string DeviceIdField = "deviceId";
    public const string LastSeenField = "lastSeen";

    public const string AndroidPlatform = "android";
    public const string WebPlatform = "web";

    /// <summary>Data key every push carries: the uid the push is for. The phone and the browser
    /// drop a push whose recipientUid isn't the account signed in there (<see cref="IsForSignedInUser"/>).</summary>
    public const string RecipientUidKey = "recipientUid";

    /// <summary>Phone: the uid this device registered its token for (set at registration, cleared
    /// at sign-out). The notification presenter checks pushes against it - it also runs when the app
    /// was killed, before Firebase Auth is up.</summary>
    public const string SignedInUidPreference = "push_signed_in_uid";

    /// <summary>Phone: a random ID for this install (not per user), stored as the token doc's deviceId.</summary>
    public const string DeviceIdPreference = "push_device_id";

    /// <summary>Phone: the token last registered, so a refreshed token's old doc can be deleted.</summary>
    public const string LastTokenPreference = "push_last_token";

    /// <summary>FCM registration tokens can hold ':' and run to ~160+ characters, so the doc ID is
    /// the token's SHA-256 in upper-case hex - the same value firestore.rules computes with
    /// hashing.sha256(token).toHexString(), which ties every doc to the token inside it.</summary>
    public static string DocId(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token.Trim())));

    /// <summary>A users/{Uid}/fcmTokens doc as the send side reads it.</summary>
    public readonly record struct DeviceToken(string Uid, string Token, string Platform, DateTime? LastSeenUtc);

    /// <summary>
    /// The devices a push for <paramref name="recipientUids"/> goes to: each recipient's tokens that
    /// the recipient owns (registered last), never a token that also sits under
    /// <paramref name="excludeUid"/> (the sender - their phone must not buzz for their own message,
    /// even if a stale copy says someone else had it), and only platforms
    /// <paramref name="platforms"/> allows (all when null). One entry per token.
    /// </summary>
    public static IReadOnlyList<DeviceToken> Deliverable(
        IEnumerable<DeviceToken> all,
        IEnumerable<string> recipientUids,
        string? excludeUid = null,
        Func<string, bool>? platforms = null)
    {
        List<DeviceToken> tokens = Clean(all).ToList();
        var recipients = recipientUids.Where(u => !string.IsNullOrWhiteSpace(u)).ToHashSet(StringComparer.Ordinal);
        if (recipients.Count == 0 || tokens.Count == 0)
        {
            return Array.Empty<DeviceToken>();
        }

        Dictionary<string, string?> owners = Owners(tokens);
        var excluded = string.IsNullOrWhiteSpace(excludeUid)
            ? new HashSet<string>(StringComparer.Ordinal)
            : tokens.Where(t => t.Uid == excludeUid).Select(t => t.Token).ToHashSet(StringComparer.Ordinal);

        return tokens
            .Where(t => recipients.Contains(t.Uid)
                        && owners.TryGetValue(t.Token, out string? owner) && owner == t.Uid
                        && !excluded.Contains(t.Token)
                        && (platforms is null || platforms(t.Platform)))
            .GroupBy(t => t.Token, StringComparer.Ordinal)
            .Select(g => g.First())
            .ToList();
    }

    /// <summary>
    /// The token docs to delete because the same token was registered later by another account (a
    /// shared phone or browser that changed hands). A tie deletes nothing - neither account gets
    /// pushes on that token (<see cref="Deliverable"/>) until one of them registers again.
    /// </summary>
    public static IReadOnlyList<DeviceToken> Superseded(IEnumerable<DeviceToken> all)
    {
        List<DeviceToken> tokens = Clean(all).ToList();
        Dictionary<string, string?> owners = Owners(tokens);
        return tokens
            .Where(t => owners.TryGetValue(t.Token, out string? owner) && owner is not null && owner != t.Uid)
            .ToList();
    }

    /// <summary>True only when the push names a recipient and it's the account signed in on this
    /// device. A push with no recipientUid, or with no one signed in, is dropped.</summary>
    public static bool IsForSignedInUser(IDictionary<string, string>? data, string? signedInUid) =>
        !string.IsNullOrWhiteSpace(signedInUid)
        && data is not null
        && data.TryGetValue(RecipientUidKey, out string? recipient)
        && string.Equals(recipient?.Trim(), signedInUid.Trim(), StringComparison.Ordinal);

    private static IEnumerable<DeviceToken> Clean(IEnumerable<DeviceToken> all) =>
        all.Where(t => !string.IsNullOrWhiteSpace(t.Uid) && !string.IsNullOrWhiteSpace(t.Token))
           .Select(t => t with { Token = t.Token.Trim(), Platform = (t.Platform ?? string.Empty).Trim().ToLowerInvariant() });

    // token -> the uid that registered it last, or null when two accounts tie.
    private static Dictionary<string, string?> Owners(IEnumerable<DeviceToken> tokens)
    {
        var owners = new Dictionary<string, (string? Uid, DateTime At)>(StringComparer.Ordinal);
        foreach (DeviceToken t in tokens)
        {
            DateTime at = t.LastSeenUtc ?? DateTime.MinValue;
            if (!owners.TryGetValue(t.Token, out var current) || at > current.At)
            {
                owners[t.Token] = (t.Uid, at);
            }
            else if (at == current.At && current.Uid != t.Uid)
            {
                owners[t.Token] = (null, at);
            }
        }
        return owners.ToDictionary(o => o.Key, o => o.Value.Uid, StringComparer.Ordinal);
    }
}
