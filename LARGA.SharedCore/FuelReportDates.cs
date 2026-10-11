using System;
using System.Globalization;

namespace LARGA.SharedCore;

/// <summary>
/// The two dates on a fuel_logs document: receiptTimestamp is the calendar date printed on the
/// receipt (OCR or typed by the driver), stored as midnight UTC of that date - it has no real
/// time of day. submittedAt is the Firestore server time the report was sent. A receipt dated
/// on a different day than it was submitted is worth a second look, so the driver app and the
/// Fuel Verification page both show a note when they don't match.
/// </summary>
public static class FuelReportDates
{
    /// <summary>The receipt's calendar date as stored. Read as a UTC date on purpose - converting
    /// midnight UTC to local time would move it to the previous day west of UTC.</summary>
    public static DateTime ReceiptDate(DateTime receiptTimestamp)
    {
        DateTime asUtc = receiptTimestamp.Kind == DateTimeKind.Local
            ? receiptTimestamp.ToUniversalTime()
            : receiptTimestamp;
        return asUtc.Date;
    }

    /// <summary>The day (Philippine time) the report was submitted.</summary>
    public static DateTime SubmittedDatePh(DateTime submittedUtc) => submittedUtc.ToPhilippineTime().Date;

    /// <summary>Null when either date is missing (older reports have no submittedAt) or both
    /// fall on the same day; otherwise a short note describing the difference.</summary>
    public static string? MismatchNote(DateTime? receiptTimestamp, DateTime? submittedUtc)
    {
        if (!receiptTimestamp.HasValue || !submittedUtc.HasValue)
        {
            return null;
        }

        DateTime receipt = ReceiptDate(receiptTimestamp.Value);
        DateTime submitted = SubmittedDatePh(submittedUtc.Value);
        int days = (submitted - receipt).Days;
        if (days == 0)
        {
            return null;
        }

        string receiptText = receipt.ToString("MMM d, yyyy", CultureInfo.InvariantCulture);
        string submittedText = submitted.ToString("MMM d, yyyy", CultureInfo.InvariantCulture);
        return days > 0
            ? $"Receipt dated {receiptText}, submitted {days} day{(days == 1 ? "" : "s")} later ({submittedText})."
            : $"Receipt dated {receiptText} is after the submission date ({submittedText}).";
    }
}
