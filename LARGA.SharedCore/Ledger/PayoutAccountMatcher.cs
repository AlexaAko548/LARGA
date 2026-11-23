using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace LARGA.SharedCore.Ledger;

/// <summary>The recipient a boundary payment receipt says the money went to.</summary>
/// <param name="Display">As printed on the receipt, tidied up - e.g. "+63 917 *** 4567".</param>
/// <param name="Pattern">Digits, with '?' for each masked character - e.g. "0917???4567". Philippine
/// mobile numbers are brought to the 11-digit 09XXXXXXXXX form so they compare however they're printed.</param>
public sealed record TargetAccount(string Display, string Pattern)
{
    public bool IsMasked => Pattern.Contains('?');
}

/// <summary>Outcome of checking a receipt's recipient against system_configs/global.authorizedPayoutAccounts.</summary>
public static class PayoutAccountStatus
{
    /// <summary>The recipient is one of the authorized accounts.</summary>
    public const string Verified = "Verified";

    /// <summary>The recipient was read but isn't an authorized account - possibly a misrouted payment.</summary>
    public const string Mismatch = "Mismatch";

    /// <summary>OCR couldn't find a recipient number on the receipt.</summary>
    public const string NotFound = "NotFound";

    /// <summary>No authorized accounts are set up yet, so nothing could be checked.</summary>
    public const string NotConfigured = "NotConfigured";

    /// <summary>Whether the manager should be warned before recording the payment.</summary>
    public static bool NeedsWarning(string? status) => status is Mismatch or NotFound;
}

public sealed record PayoutAccountCheck(string Status, TargetAccount? Target, string? MatchedAccount)
{
    public bool NeedsWarning => PayoutAccountStatus.NeedsWarning(Status);

    /// <summary>One-line explanation for the scan / payment screens.</summary>
    public string Message => Status switch
    {
        PayoutAccountStatus.Verified => $"Sent to {Target?.Display} - an authorized account.",
        PayoutAccountStatus.Mismatch => $"Sent to {Target?.Display}, which is NOT an authorized payout account. Check the payment wasn't misrouted.",
        PayoutAccountStatus.NotFound => "The receipt's recipient number couldn't be read - check it was sent to an authorized account.",
        _ => Target is null ? "No authorized payout accounts are set up yet." : $"Sent to {Target.Display} (no authorized payout accounts set up to compare with).",
    };
}

/// <summary>
/// Quick Ledger / Financial Ledger target-account verification: finds the GCash number or bank
/// account a boundary e-receipt was sent TO, and checks it against the manager's authorized
/// payout accounts, so a payment sent to the wrong account is flagged before it's booked. Pure
/// text rules shared by the mobile ML Kit scanner (EReceiptOcrService) and the web's
/// Tesseract scan (GcashReceiptParser).
///
/// GCash receipts usually print the recipient's number masked, e.g. "+63 917 *** 4567", so a
/// masked number matches an authorized one when every digit it does show lines up.
/// </summary>
public static class PayoutAccountMatcher
{
    // Characters receipts (and OCR of them) use to mask digits.
    private const string MaskChars = "*•●xX#";

    // A label naming the recipient, with whatever follows it on the line.
    private static readonly Regex RecipientLabel = new(
        @"\b(?:sent\s+to|send\s+to|transfer(?:red)?\s+to|recipient(?:'s)?(?:\s+(?:number|no\.?|account))?|receiver|account\s*(?:no\.?|number|#)|acct\.?\s*(?:no\.?|#)?|mobile\s*(?:no\.?|number)|gcash\s*(?:no\.?|number)|to)\b\s*:?(.*)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    // Lines about the sender or the transaction, never the recipient.
    private static readonly Regex NotRecipient = new(
        @"\b(?:from|sender|ref(?:erence)?|transaction\s*(?:id|no)|trace|invoice)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    // A run of digits/mask characters, with spaces or dashes between groups, optionally with +.
    private static readonly Regex AccountToken = new(
        @"\+?[0-9*•●xX#](?:[\s\-]?[0-9*•●xX#]){3,24}",
        RegexOptions.CultureInvariant);

    // A Philippine mobile number, possibly masked, anywhere on a line (no label needed).
    private static readonly Regex MobileToken = new(
        @"(?<![0-9])(?:\+?\s?63|0)\s?9[0-9*•●xX#]{2}[\s\-]?[0-9*•●xX#]{3}[\s\-]?[0-9*•●xX#]{4}(?![0-9])",
        RegexOptions.CultureInvariant);

    /// <summary>
    /// The recipient number on the receipt, or null. Looks after a recipient label first (same
    /// line, then the next line), then for any Philippine mobile number on a line that isn't
    /// about the sender or the reference number.
    /// </summary>
    public static TargetAccount? ExtractTarget(IEnumerable<string?> ocrLines, string? referenceNumber = null)
    {
        List<string> lines = ocrLines
            .SelectMany(l => (l ?? string.Empty).Split('\n'))
            .Select(l => l.Trim())
            .Where(l => l.Length > 0)
            .ToList();
        string? referenceDigits = referenceNumber is null ? null : new string(referenceNumber.Where(char.IsDigit).ToArray());

        for (int i = 0; i < lines.Count; i++)
        {
            if (NotRecipient.IsMatch(lines[i]))
            {
                continue;
            }

            Match label = RecipientLabel.Match(lines[i]);
            if (!label.Success)
            {
                continue;
            }

            TargetAccount? found = FirstAccount(label.Groups[1].Value, referenceDigits);
            if (found is null && i + 1 < lines.Count && !NotRecipient.IsMatch(lines[i + 1]))
            {
                found = FirstAccount(lines[i + 1], referenceDigits);
            }
            if (found is not null)
            {
                return found;
            }
        }

        foreach (string line in lines.Where(l => !NotRecipient.IsMatch(l)))
        {
            Match mobile = MobileToken.Match(line);
            if (mobile.Success && ToTarget(mobile.Value, referenceDigits) is TargetAccount target)
            {
                return target;
            }
        }

        return null;
    }

    /// <summary>Checks <paramref name="target"/> against the authorized accounts (free text such as
    /// "0917 123 4567" or "BDO 001234567890" - only the digits count).</summary>
    public static PayoutAccountCheck Check(TargetAccount? target, IEnumerable<string>? authorizedAccounts)
    {
        List<(string Original, string Digits)> authorized = (authorizedAccounts ?? Enumerable.Empty<string>())
            .Where(a => !string.IsNullOrWhiteSpace(a))
            .Select(a => (a.Trim(), CanonicalDigits(a)))
            .Where(a => a.Item2.Length >= 4)
            .ToList();

        if (authorized.Count == 0)
        {
            return new PayoutAccountCheck(PayoutAccountStatus.NotConfigured, target, null);
        }
        if (target is null)
        {
            return new PayoutAccountCheck(PayoutAccountStatus.NotFound, null, null);
        }

        foreach ((string original, string digits) in authorized)
        {
            if (Matches(target.Pattern, digits))
            {
                return new PayoutAccountCheck(PayoutAccountStatus.Verified, target, original);
            }
        }
        return new PayoutAccountCheck(PayoutAccountStatus.Mismatch, target, null);
    }

    /// <summary>Whether a receipt pattern ('?' = masked digit) can be the authorized number.</summary>
    public static bool Matches(string pattern, string authorizedDigits)
    {
        if (string.IsNullOrEmpty(pattern) || string.IsNullOrEmpty(authorizedDigits))
        {
            return false;
        }

        if (!pattern.Contains('?'))
        {
            return pattern == authorizedDigits;
        }

        if (pattern.Length == authorizedDigits.Length)
        {
            return pattern.Zip(authorizedDigits).All(p => p.First == '?' || p.First == p.Second);
        }

        // Masks don't always keep the length ("****1234" for a 12-digit account): the digits
        // shown before and after the masked part must start and end the authorized number, and
        // enough of them must be shown to mean something.
        string prefix = pattern[..pattern.IndexOf('?')];
        string suffix = pattern[(pattern.LastIndexOf('?') + 1)..];
        return prefix.Length + suffix.Length >= 4
               && authorizedDigits.Length >= prefix.Length + suffix.Length
               && authorizedDigits.StartsWith(prefix, StringComparison.Ordinal)
               && authorizedDigits.EndsWith(suffix, StringComparison.Ordinal);
    }

    /// <summary>Digits of an account number, Philippine mobiles as 09XXXXXXXXX.</summary>
    public static string CanonicalDigits(string? account)
    {
        string? mobile = InputValidator.NormalizePhilippineMobile(
            Regex.Replace(account ?? string.Empty, @"[^\d+\s\-().]", string.Empty));
        return mobile is not null ? "0" + mobile[3..] : new string((account ?? string.Empty).Where(char.IsDigit).ToArray());
    }

    private static TargetAccount? FirstAccount(string text, string? referenceDigits)
    {
        foreach (Match token in AccountToken.Matches(text))
        {
            if (ToTarget(token.Value, referenceDigits) is TargetAccount target)
            {
                return target;
            }
        }
        return null;
    }

    private static TargetAccount? ToTarget(string raw, string? referenceDigits)
    {
        string display = Regex.Replace(raw.Trim(), @"\s+", " ");
        string pattern = new string(display
            .Where(c => char.IsDigit(c) || MaskChars.Contains(c))
            .Select(c => char.IsDigit(c) ? c : '?')
            .ToArray());

        int visibleDigits = pattern.Count(char.IsDigit);
        // Too short to be an account, all mask, or an amount-like/reference-number read.
        if (pattern.Length < 6 || visibleDigits < 4 || (referenceDigits is { Length: > 0 } && pattern == referenceDigits))
        {
            return null;
        }

        return new TargetAccount(display, NormalizeMobilePattern(pattern));
    }

    // +63 / 63 / 9 prefixed mobile patterns → 11 characters starting 09.
    private static string NormalizeMobilePattern(string pattern) => pattern switch
    {
        { Length: 12 } when pattern.StartsWith("639") => "0" + pattern[2..],
        { Length: 10 } when pattern.StartsWith('9') => "0" + pattern,
        _ => pattern,
    };
}
