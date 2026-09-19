using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LARGA.MobileApp.Models;
using LARGA.MobileApp.Services;
using LARGA.MobileApp.Views.Manager;
using LARGA.Shared.Models.Entities;
using LARGA.SharedCore.Models.FinancialLedger;

namespace LARGA.MobileApp.ViewModels.Manager;

public partial class RecordPaymentViewModel : ObservableObject, IQueryAttributable
{
    private readonly MobileFinancialLedgerService _ledgerService;

    public Func<Task>? OnPaymentRecorded { get; set; }

    [ObservableProperty]
    private bool isVisible;

    [ObservableProperty]
    private bool isOtherPayment;

    [ObservableProperty]
    private LedgerItemModel? currentItem;

    [ObservableProperty]
    private ObservableCollection<UserProfile> availableDrivers = new();

    [ObservableProperty]
    private UserProfile? selectedDriver;

    [ObservableProperty]
    private ObservableCollection<string> categories = new() { "Boundary", "Debt" };

    [ObservableProperty]
    private string selectedCategory = "Boundary";

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

    [ObservableProperty]
    private string validationMessage = string.Empty;

    public string CurrentDriverName => IsOtherPayment
        ? (SelectedDriver?.FullName ?? "Other Payment")
        : (CurrentItem?.DriverName ?? string.Empty);

    public decimal CurrentExpectedAmount => CurrentItem?.ExpectedAmount ?? 0m;
    public string ModalTitle => IsOtherPayment ? "Record Other Payment" : "Record Payment";
    public bool ShowExpectedAmount => !IsOtherPayment;
    public bool HasValidationMessage => !string.IsNullOrWhiteSpace(ValidationMessage);

    public bool IsEWallet => SelectedMethod == PaymentMethodOption.EWalletGCash;

    public RecordPaymentViewModel(MobileFinancialLedgerService ledgerService)
    {
        _ledgerService = ledgerService;
    }

    public void ApplyQueryAttributes(IDictionary<string, object> query)
    {
        if (query.TryGetValue("Amount", out object? amt) && amt is decimal parsedAmt && parsedAmt > 0)
        {
            AmountReceived = parsedAmt;
            ReceiptAmount = parsedAmt.ToString("F2");
        }
        if (query.TryGetValue("Date", out object? dt) && dt is DateTime parsedDate)
        {
            ReceiptDate = parsedDate;
        }
        if (query.TryGetValue("Reference", out object? rf) && rf is string parsedRef)
        {
            ReferenceNumber = parsedRef;
        }
    }

    public void InitializeStandard(LedgerItemModel item)
    {
        IsOtherPayment = false;
        CurrentItem = item;
        ValidationMessage = string.Empty;
        AmountReceived = Math.Max(0, item.ExpectedAmount - item.AmountPaid);
        SelectedMethod = PaymentMethodOption.Cash;
        ReferenceNumber = string.Empty;
        ReceiptAmount = AmountReceived.ToString("F2");
        ReceiptDate = DateTime.Today;
        IsVisible = true;
        OnPropertyChanged(nameof(IsEWallet));
        OnPropertyChanged(nameof(ModalTitle));
        OnPropertyChanged(nameof(ShowExpectedAmount));
        OnPropertyChanged(nameof(HasValidationMessage));
    }

    public void InitializeOtherPayment(IEnumerable<UserProfile> drivers)
    {
        IsOtherPayment = true;
        CurrentItem = null;
        AvailableDrivers = new ObservableCollection<UserProfile>(drivers);
        SelectedDriver = AvailableDrivers.FirstOrDefault();
        SelectedCategory = "Boundary";
        ValidationMessage = string.Empty;
        AmountReceived = 0;
        SelectedMethod = PaymentMethodOption.Cash;
        ReferenceNumber = string.Empty;
        ReceiptAmount = string.Empty;
        ReceiptDate = DateTime.Today;
        IsVisible = true;
        OnPropertyChanged(nameof(IsEWallet));
        OnPropertyChanged(nameof(CurrentDriverName));
        OnPropertyChanged(nameof(CurrentExpectedAmount));
        OnPropertyChanged(nameof(ModalTitle));
        OnPropertyChanged(nameof(ShowExpectedAmount));
        OnPropertyChanged(nameof(HasValidationMessage));
    }

    public void Initialize(LedgerItemModel item) => InitializeStandard(item);

    public void EnsureClosed() => IsVisible = false;

    [RelayCommand]
    private void SelectMethod(string method)
    {
        SelectedMethod = method.Equals("GCASH", StringComparison.OrdinalIgnoreCase)
            ? PaymentMethodOption.EWalletGCash
            : PaymentMethodOption.Cash;

        OnPropertyChanged(nameof(IsEWallet));
    }

    [RelayCommand]
    private async Task OpenScanReceiptAsync()
    {
        await Shell.Current.GoToAsync(nameof(ScanReceiptPage));
    }

    [RelayCommand]
    private void Close()
    {
        IsVisible = false;
        ValidationMessage = string.Empty;
    }

    [RelayCommand]
    private async Task ConfirmPaymentAsync()
    {
        if (IsSubmitting) return;

        ValidationMessage = string.Empty;
        if (AmountReceived <= 0)
        {
            ValidationMessage = "Amount received must be greater than zero.";
            OnPropertyChanged(nameof(HasValidationMessage));
            return;
        }

        IsSubmitting = true;
        try
        {
            string methodString = SelectedMethod == PaymentMethodOption.EWalletGCash ? "EWallet" : "Cash";

            if (IsOtherPayment && SelectedDriver != null)
            {
                if (SelectedCategory.Equals("Boundary", StringComparison.OrdinalIgnoreCase))
                {
                    string? shiftId = await _ledgerService.GetTodayPendingShiftForDriverAsync(SelectedDriver.UserId, DateTime.UtcNow);
                    if (string.IsNullOrWhiteSpace(shiftId))
                    {
                        ValidationMessage = "Selected driver has no pending boundary shift for today.";
                        OnPropertyChanged(nameof(HasValidationMessage));
                        return;
                    }

                    RecordPaymentResult boundaryResult = await _ledgerService.RecordPaymentAsync(
                        shiftId,
                        AmountReceived,
                        methodString,
                        ReferenceNumber,
                        null,
                        "Boundary");

                    if (boundaryResult.Ok)
                    {
                        IsVisible = false;
                        if (OnPaymentRecorded != null) await OnPaymentRecorded.Invoke();
                    }
                    else
                    {
                        ValidationMessage = boundaryResult.ErrorMessage;
                        OnPropertyChanged(nameof(HasValidationMessage));
                    }
                }
                else
                {
                    SettleDebtResult debtResult = await _ledgerService.SettleDebtAsync(
                        SelectedDriver.UserId,
                        AmountReceived,
                        methodString
                    );

                    if (debtResult.Ok)
                    {
                        IsVisible = false;
                        if (OnPaymentRecorded != null) await OnPaymentRecorded.Invoke();
                    }
                    else
                    {
                        ValidationMessage = debtResult.ErrorMessage;
                        OnPropertyChanged(nameof(HasValidationMessage));
                    }
                }
            }
            else
            {
                string shiftId = CurrentItem?.ShiftId ?? string.Empty;
                RecordPaymentResult result = await _ledgerService.RecordPaymentAsync(
                    shiftId,
                    AmountReceived,
                    methodString,
                    ReferenceNumber,
                    null,
                    "Boundary");

                if (result.Ok)
                {
                    IsVisible = false;
                    if (OnPaymentRecorded != null) await OnPaymentRecorded.Invoke();
                }
                else
                {
                    ValidationMessage = result.ErrorMessage;
                    OnPropertyChanged(nameof(HasValidationMessage));
                }
            }
        }
        finally
        {
            IsSubmitting = false;
        }
    }

    partial void OnCurrentItemChanged(LedgerItemModel? value)
    {
        OnPropertyChanged(nameof(CurrentDriverName));
        OnPropertyChanged(nameof(CurrentExpectedAmount));
    }

    partial void OnSelectedDriverChanged(UserProfile? value)
    {
        OnPropertyChanged(nameof(CurrentDriverName));
    }

    partial void OnIsOtherPaymentChanged(bool value)
    {
        OnPropertyChanged(nameof(CurrentDriverName));
        OnPropertyChanged(nameof(CurrentExpectedAmount));
        OnPropertyChanged(nameof(ModalTitle));
        OnPropertyChanged(nameof(ShowExpectedAmount));
    }

    partial void OnValidationMessageChanged(string value)
    {
        OnPropertyChanged(nameof(HasValidationMessage));
    }
}