namespace LARGA.MobileApp.Models;

public sealed class ReceiptScanPayload
{
    public decimal? Amount { get; init; }
    public DateTime? Date { get; init; }
    public string ReferenceNumber { get; init; } = string.Empty;
}
