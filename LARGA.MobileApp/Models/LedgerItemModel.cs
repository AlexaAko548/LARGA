using CommunityToolkit.Mvvm.ComponentModel;
using LARGA.SharedCore.Models.FinancialLedger;

namespace LARGA.MobileApp.Models;

public enum PaymentMethodOption
{
    Cash,
    EWalletGCash
}

public partial class LedgerItemModel : ObservableObject
{
    public string ShiftId { get; set; } = string.Empty;
    public string DriverId { get; set; } = string.Empty;
    public string DriverName { get; set; } = string.Empty;
    public string TaxiId { get; set; } = string.Empty;
    public string PlateNumber { get; set; } = string.Empty;
    public DateTime? ShiftStart { get; set; }
    public DateTime? ShiftEnd { get; set; }
    public decimal ExpectedAmount { get; set; }
    public decimal AmountPaid { get; set; }
    public SettlementStatus Status { get; set; }
    public string PaymentMethodLabel { get; set; } = string.Empty;
    public string CategoryLabel { get; set; } = string.Empty;
    public bool IsOtherPaymentEntry { get; set; }

    public bool IsPending => Status == SettlementStatus.Waiting;
    public string FormattedExpected => $"₱ {ExpectedAmount:N2}";
    public string FormattedPaid => $"₱ {AmountPaid:N2}";
    public string CompletedMetaText
    {
        get
        {
            if (string.IsNullOrWhiteSpace(PaymentMethodLabel))
            {
                return $"Paid: {FormattedPaid}";
            }

            string category = string.IsNullOrWhiteSpace(CategoryLabel) ? string.Empty : $" ({CategoryLabel})";
            return $"{PaymentMethodLabel} · {FormattedPaid}{category}";
        }
    }

    public string StatusDisplayText => Status switch
    {
        SettlementStatus.Cleared => "Paid",
        SettlementStatus.Partial => "Partial",
        _ => "Pending"
    };
}