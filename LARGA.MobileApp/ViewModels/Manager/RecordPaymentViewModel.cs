using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LARGA.MobileApp.Models;
using LARGA.SharedCore.Models.FinancialLedger;
using LARGA.SharedCore.Services;

namespace LARGA.MobileApp.ViewModels.Manager;

public partial class RecordPaymentViewModel : ObservableObject
{
    private readonly FinancialLedgerService _ledgerService;

    public Func<Task>? OnPaymentRecorded { get; set; }

    [ObservableProperty]
private bool isVisible;

[ObservableProperty]
private LedgerItemModel? currentItem;

[ObservableProperty]
private decimal amountReceived;

[ObservableProperty]
private PaymentMethodOption selectedMethod = PaymentMethodOption.Cash;

[ObservableProperty]
private string referenceNumber = string.Empty;

[ObservableProperty]
private string receiptAmount = string.Empty;

[ObservableProperty]
private DateTime receiptDate = DateTime.Today;

[ObservableProperty]
private bool isSubmitting;
    public bool IsEWallet => SelectedMethod == PaymentMethodOption.EWalletGCash;

    public RecordPaymentViewModel(FinancialLedgerService ledgerService)
    {
        _ledgerService = ledgerService;
    }

    public void Initialize(LedgerItemModel item)
    {
        CurrentItem = item;
        AmountReceived = Math.Max(0, item.ExpectedAmount - item.AmountPaid);
        SelectedMethod = PaymentMethodOption.Cash;
        ReferenceNumber = string.Empty;
        ReceiptAmount = AmountReceived.ToString("F2");
        ReceiptDate = DateTime.Today;
        IsVisible = true;
        OnPropertyChanged(nameof(IsEWallet));
    }

    [RelayCommand]
    private void SelectMethod(string method)
    {
        SelectedMethod = method.Equals("GCASH", StringComparison.OrdinalIgnoreCase)
            ? PaymentMethodOption.EWalletGCash
            : PaymentMethodOption.Cash;

        OnPropertyChanged(nameof(IsEWallet));
    }

    [RelayCommand]
    private void Close()
    {
        IsVisible = false;
    }

    [RelayCommand]
    private async Task ConfirmPaymentAsync()
    {
        if (CurrentItem == null || IsSubmitting) return;

        IsSubmitting = true;
        try
        {
            string methodString = SelectedMethod == PaymentMethodOption.EWalletGCash ? "EWallet" : "Cash";
            RecordPaymentResult result = await _ledgerService.RecordPaymentAsync(
                CurrentItem.ShiftId,
                AmountReceived,
                methodString
            );

            if (result.Ok)
            {
                IsVisible = false;
                if (OnPaymentRecorded != null)
                {
                    await OnPaymentRecorded.Invoke();
                }
            }
        }
        finally
        {
            IsSubmitting = false;
        }
    }
}