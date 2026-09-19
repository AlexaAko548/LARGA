using System.Globalization;
using System.Text.RegularExpressions;

namespace LARGA.MobileApp.Services;

public class ReceiptScanResult
{
    public decimal? Amount { get; set; }
    public DateTime? Date { get; set; }
    public string ReferenceNumber { get; set; } = string.Empty;
}

public static class ReceiptOcrParser
{
    public static ReceiptScanResult Parse(string rawText)
    {
        var result = new ReceiptScanResult();
        if (string.IsNullOrWhiteSpace(rawText)) return result;

        // 1. Reference Number (GCash standard format: Ref No. followed by digits or spaces)
        var refMatch = Regex.Match(rawText, @"(?:Ref(?:\s*No\.?|erence\s*No\.?)[:\s]*)([0-9\s]{9,16})", RegexOptions.IgnoreCase);
        if (refMatch.Success)
        {
            result.ReferenceNumber = refMatch.Groups[1].Value.Replace(" ", "").Trim();
        }

        // 2. Amount (Look for Total Amount Sent, Amount, or PHP / ₱)
        var amountMatch = Regex.Match(rawText, @"(?:Total\s+Amount\s+Sent|Amount)[\s:₱PHP]*([0-9,]+\.[0-9]{2})", RegexOptions.IgnoreCase);
        if (!amountMatch.Success)
        {
            amountMatch = Regex.Match(rawText, @"[₱PHP]?\s*([0-9,]+\.[0-9]{2})");
        }

        if (amountMatch.Success && decimal.TryParse(amountMatch.Groups[1].Value.Replace(",", ""), NumberStyles.Any, CultureInfo.InvariantCulture, out decimal parsedAmount))
        {
            result.Amount = parsedAmount;
        }

        // 3. Date (Format: Jul 23, 2026 or MM/dd/yyyy)
        var dateMatch = Regex.Match(rawText, @"([A-Za-z]{3}\s+\d{1,2},?\s+\d{4})");
        if (dateMatch.Success && DateTime.TryParse(dateMatch.Groups[1].Value, out DateTime parsedDate))
        {
            result.Date = parsedDate;
        }

        return result;
    }
}