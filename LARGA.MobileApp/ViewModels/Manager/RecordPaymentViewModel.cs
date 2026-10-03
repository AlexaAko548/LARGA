using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Messaging;
using LARGA.MobileApp.Services;
using LARGA.MobileApp.Views.Manager;
using LARGA.SharedCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Graphics;

namespace LARGA.MobileApp.ViewModels.Manager;

/// <summary>
/// The Record Payment and Record Other Payment forms. One form serves both: a shift's boundary, or a
/// driver's "Other Payment" debt. E-Wallet payments need a scanned receipt whose amount matches the
/// amount received. Receipt details are read-only and come only from the scan.
/// </summary>
public class RecordPaymentViewModel : BindableObject
{
    private static readonly Color PrimaryBlue = Color.FromArgb("#019BCF");
    private static readonly Color LightBlue = Color.FromArgb("#A6DCEE");
    private static readonly Color DarkText = Color.FromArgb("#1D1D1D");
    private static readonly Color White = Color.FromArgb("#FFFFFF");

    private readonly QuickLedgerService _service;
    private QuickLedgerSnapshot? _snapshot;
    private PendingBoundaryRow? _boundaryRow;
    private IReadOnlyList<OtherPaymentRow> _debtors = Array.Empty<OtherPaymentRow>();
    private EReceiptScanResult? _receipt;

    private bool _isOpen;
    private bool _isOtherMode;
    private bool _isEWallet;
    private bool _isBusy;
    private string _title = string.Empty;
    private string _driverName = string.Empty;
    private string _plateText = string.Empty;
    private string _expectedText = string.Empty;
    private string _amountReceivedText = string.Empty;
    private string _errorText = string.Empty;
    private string _receiptDateText = NotScanned;
    private string _referenceText = NotScanned;
    private int _selectedDriverIndex;
    private int _selectedCategoryIndex;

    private const string NotScanned = "---";
    private const int MaxNotesLength = 500;
    private string _notesText = string.Empty;

    /// <summary>Raised after a payment is saved, so the ledger can reload.</summary>
    public event EventHandler? Saved;

    public ObservableCollection<string> DriverOptions { get; } = new();
    public IReadOnlyList<string> CategoryOptions { get; } = new[] { "Debt" };

    public ICommand CloseCommand { get; }
    public ICommand CashCommand { get; }
    public ICommand EWalletCommand { get; }
    public ICommand ScanCommand { get; }
    public ICommand ConfirmCommand { get; }

    public RecordPaymentViewModel(QuickLedgerService service)
    {
        _service = service;

        CloseCommand = new Command(() => IsOpen = false);
        CashCommand = new Command(() => IsEWallet = false);
        EWalletCommand = new Command(() => IsEWallet = true);
        ScanCommand = new Command(async () => await OpenScannerAsync());
        ConfirmCommand = new Command(async () => await ConfirmAsync());

        WeakReferenceMessenger.Default.Register<RecordPaymentViewModel, EReceiptScanResult>(this, static (r, scan) => r.ApplyScan(scan));
    }

    public bool IsOpen { get => _isOpen; private set => Set(ref _isOpen, value); }
    public bool IsOtherMode { get => _isOtherMode; private set => Set(ref _isOtherMode, value); }
    public bool IsEWallet
    {
        get => _isEWallet;
        set
        {
            Set(ref _isEWallet, value);

            // E-Wallet amounts come only from the receipt scan, so the typed amount is cleared. It is filled from the
            // scan (or stays empty until one is done).
            if (value)
            {
                AmountReceivedText = _receipt is null ? string.Empty : _receipt.Amount.ToString("0.00", CultureInfo.InvariantCulture);
            }

            OnPropertyChanged(nameof(CashBackground));
            OnPropertyChanged(nameof(CashTextColor));
            OnPropertyChanged(nameof(EWalletBackground));
            OnPropertyChanged(nameof(EWalletTextColor));
        }
    }
    public bool IsBusy { get => _isBusy; private set => Set(ref _isBusy, value); }
    public string Title { get => _title; private set => Set(ref _title, value); }
    public string DriverName { get => _driverName; private set => Set(ref _driverName, value); }
    public string PlateText { get => _plateText; private set => Set(ref _plateText, value); }
    public string ExpectedText { get => _expectedText; private set => Set(ref _expectedText, value); }
    public string AmountReceivedText { get => _amountReceivedText; set => Set(ref _amountReceivedText, value); }

    /// <summary>The manager's note on an Other Payment, kept on the payment records for audit. Optional.</summary>
    public string NotesText { get => _notesText; set => Set(ref _notesText, value); }

    /// <summary>Set when a payment saved but its audit entry didn't. Null otherwise.</summary>
    public string? AuditWarning { get; private set; }
    public string ErrorText { get => _errorText; private set => Set(ref _errorText, value); }
    public string ReceiptDateText { get => _receiptDateText; private set => Set(ref _receiptDateText, value); }
    public string ReferenceText { get => _referenceText; private set => Set(ref _referenceText, value); }

    public int SelectedDriverIndex
    {
        get => _selectedDriverIndex;
        set
        {
            Set(ref _selectedDriverIndex, value);
            if (IsOtherMode) RefreshOtherSelection();
        }
    }

    public int SelectedCategoryIndex { get => _selectedCategoryIndex; set => Set(ref _selectedCategoryIndex, value); }

    public Color CashBackground => IsEWallet ? LightBlue : PrimaryBlue;
    public Color CashTextColor => IsEWallet ? DarkText : White;
    public Color EWalletBackground => IsEWallet ? PrimaryBlue : LightBlue;
    public Color EWalletTextColor => IsEWallet ? White : DarkText;

    public void OpenForBoundary(QuickLedgerSnapshot snapshot, PendingBoundaryRow row)
    {
        _snapshot = snapshot;
        _boundaryRow = row;
        _debtors = Array.Empty<OtherPaymentRow>();
        IsOtherMode = false;

        Title = "Record Payment";
        DriverName = row.DriverName;
        PlateText = row.TaxiPlate;
        ExpectedText = Money(row.Remaining);

        ResetForm();
        IsOpen = true;
    }

    public void OpenForOther(QuickLedgerSnapshot snapshot, string? driverId = null)
    {
        _snapshot = snapshot;
        _boundaryRow = null;
        _debtors = snapshot.Result.OtherPayments;
        IsOtherMode = true;

        Title = "Record Other Payment";
        DriverOptions.Clear();
        foreach (OtherPaymentRow debtor in _debtors)
        {
            DriverOptions.Add(debtor.DriverName);
        }

        int index = driverId is null ? -1 : IndexOfDebtor(driverId);
        _selectedDriverIndex = index >= 0 ? index : 0;
        OnPropertyChanged(nameof(SelectedDriverIndex));

        ResetForm();
        RefreshOtherSelection();
        IsOpen = true;
    }

    private int IndexOfDebtor(string driverId)
    {
        for (int i = 0; i < _debtors.Count; i++)
        {
            if (_debtors[i].DriverId == driverId) return i;
        }
        return -1;
    }

    private OtherPaymentRow? SelectedDebtor =>
        SelectedDriverIndex >= 0 && SelectedDriverIndex < _debtors.Count ? _debtors[SelectedDriverIndex] : null;

    private void RefreshOtherSelection()
    {
        if (_snapshot is null || SelectedDebtor is null)
        {
            DriverName = string.Empty;
            PlateText = "No drivers owe payment";
            ExpectedText = Money(0m);
            return;
        }

        DriverName = SelectedDebtor.DriverName;
        PlateText = "Debt";
        ExpectedText = Money(QuickLedgerCalculator.TotalOwedBy(_snapshot.Input, PhilippineTime.Now, SelectedDebtor.DriverId));
    }

    private void ResetForm()
    {
        _receipt = null;
        AmountReceivedText = string.Empty;
        NotesText = string.Empty;
        ReceiptDateText = NotScanned;
        ReferenceText = NotScanned;
        ErrorText = string.Empty;
        IsEWallet = false;
    }

    private void ApplyScan(EReceiptScanResult scan)
    {
        _receipt = scan;
        // The receipt is the evidence for the payment, so its amount goes straight into Amount Received.
        AmountReceivedText = scan.Amount.ToString("0.00", CultureInfo.InvariantCulture);
        ReceiptDateText = scan.Date.ToString("MMM d, yyyy", CultureInfo.InvariantCulture);
        ReferenceText = scan.ReferenceNumber;
        ErrorText = string.Empty;
    }

    private async Task OpenScannerAsync()
    {
        var page = Application.Current!.Handler.MauiContext!.Services.GetRequiredService<ScanEReceiptPage>();
        await Shell.Current.Navigation.PushModalAsync(page);
    }

    private async Task ConfirmAsync()
    {
        if (_snapshot is null || IsBusy) return;

        ErrorText = string.Empty;

        if (!TryReadAmount(out decimal amount))
        {
            ErrorText = "Enter the amount received.";
            return;
        }

        string note = NotesText.Trim();
        if (IsOtherMode && note.Length > MaxNotesLength)
        {
            ErrorText = $"Notes can be at most {MaxNotesLength} characters.";
            return;
        }

        string method = IsEWallet ? QuickLedgerCalculator.EWalletMethod : QuickLedgerCalculator.CashMethod;

        if (IsEWallet)
        {
            if (_receipt is null)
            {
                ErrorText = "Scan the e-receipt before recording an e-wallet payment.";
                return;
            }

            if (_receipt.Amount != amount)
            {
                ErrorText = $"The receipt shows {Money(_receipt.Amount)}, which doesn't match the amount received.";
                return;
            }
        }

        IsBusy = true;
        try
        {
            DateTime nowUtc = DateTime.UtcNow;
            string auditAction = string.Empty;
            string auditDetails = string.Empty;

            if (IsOtherMode)
            {
                OtherPaymentRow? debtor = SelectedDebtor;
                if (debtor is null)
                {
                    ErrorText = "Select a driver.";
                    return;
                }

                decimal owed = QuickLedgerCalculator.TotalOwedBy(_snapshot.Input, PhilippineTime.Now, debtor.DriverId);
                if (amount > owed)
                {
                    ErrorText = $"Amount is more than the {Money(owed)} owed.";
                    return;
                }

                // Check the allocation before uploading the photo, so a refused payment doesn't leave a stray file behind.
                DebtSettlementPlan preview = QuickLedgerCalculator.PlanDebtSettlement(
                    _snapshot.Input, PhilippineTime.Now, debtor.DriverId, amount, method, null, nowUtc);
                if (preview.Unallocated > 0)
                {
                    ErrorText = $"Amount is more than the {Money(owed)} owed.";
                    return;
                }

                PaymentEvidence evidence = await BuildEvidenceAsync(note.Length > 0 ? note : null, nowUtc, debtor.DriverId);
                DebtSettlementPlan plan = QuickLedgerCalculator.PlanDebtSettlement(
                    _snapshot.Input, PhilippineTime.Now, debtor.DriverId, amount, method, evidence, nowUtc);

                await _service.SaveDebtSettlementAsync(plan, debtor.DriverId, nowUtc);
                auditAction = "QuickLedgerDebtPaymentRecorded";
                auditDetails = DescribeSettlement(debtor.DriverName, amount, method, plan, evidence);
            }
            else
            {
                PendingBoundaryRow row = _boundaryRow!;
                if (amount > row.Remaining)
                {
                    ErrorText = $"Amount is more than the {Money(row.Remaining)} still due.";
                    return;
                }

                PaymentEvidence? evidence = IsEWallet ? await BuildEvidenceAsync(null, nowUtc, row.ShiftKey) : null;
                BoundaryPaymentWrite write = QuickLedgerCalculator.PlanBoundaryPayment(row, amount, method, evidence, nowUtc);
                await _service.SaveBoundaryPaymentAsync(write, nowUtc);
                auditAction = "QuickLedgerPaymentRecorded";
                auditDetails = DescribeBoundaryPayment(row, amount, method, write, evidence);
            }

            IsOpen = false;
            await WriteAuditSafelyAsync(auditAction, auditDetails);
            Saved?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Record payment error: {ex}");
            // Verification and upload failures carry a specific message; show it.
            ErrorText = ex is InvalidOperationException or TimeoutException
                ? ex.Message
                : "Could not save this payment. Please try again.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// The audit record for a payment: the note, and for E-Wallet the GCash details plus the receipt photo, uploaded to
    /// Firebase Storage. Throws if the upload fails, so the payment is not saved without its evidence.
    /// </summary>
    /// <summary>
    /// The audit entry is written after the payment is saved. If it fails, the payment still stands, so the form closes
    /// and the failure is reported on the ledger page. The form isn't kept open, because a retry would pay twice.
    /// </summary>
    private async Task WriteAuditSafelyAsync(string action, string details)
    {
        AuditWarning = null;
        try
        {
            await _service.WriteAuditAsync(action, details);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Audit write error: {ex}");
            AuditWarning = $"Payment saved, but its audit entry could not be written: {ex.Message}";
        }
    }

    private static string DescribeBoundaryPayment(
        PendingBoundaryRow row, decimal amount, string method, BoundaryPaymentWrite write, PaymentEvidence? evidence) =>
        $"Recorded {Money(amount)} {method} payment against {row.DriverName}'s boundary for shift {row.ShiftKey} " +
        $"(transaction {write.TransactionId}). Shift status: {write.PaymentStatus}." + EvidenceSuffix(evidence);

    private static string DescribeSettlement(
        string driverName, decimal amount, string method, DebtSettlementPlan plan, PaymentEvidence? evidence)
    {
        string txn = plan.PaymentUpdates.FirstOrDefault()?.TransactionId ?? string.Empty;
        string credit = plan.AdjustmentCredit > 0
            ? $", with {Money(plan.AdjustmentCredit)} credited against manual debt"
            : string.Empty;

        return $"Recorded {Money(amount)} {method} debt payment from {driverName} (transaction {txn}), " +
               $"applied to {plan.PaymentUpdates.Count} shift(s){credit}." + EvidenceSuffix(evidence);
    }

    private static string EvidenceSuffix(PaymentEvidence? evidence)
    {
        if (evidence is null) return string.Empty;

        string note = string.IsNullOrEmpty(evidence.Notes) ? string.Empty : $" Note: {evidence.Notes}";
        string receipt = string.IsNullOrEmpty(evidence.ReferenceNumber)
            ? string.Empty
            : $" GCash reference {evidence.ReferenceNumber}; receipt photo stored.";
        return note + receipt;
    }

    private async Task<PaymentEvidence> BuildEvidenceAsync(string? notes, DateTime nowUtc, string label)
    {
        if (!IsEWallet || _receipt is null)
        {
            return new PaymentEvidence(notes, null, null, null, null);
        }

        string photoUrl;
        try
        {
            photoUrl = await _service.UploadReceiptPhotoAsync(_receipt.PhotoFilePath, nowUtc, label);
        }
        catch (Exception ex) when (ex is not InvalidOperationException)
        {
            throw new InvalidOperationException($"Could not upload the receipt photo, so the payment was not saved. {ex.Message}");
        }

        return new PaymentEvidence(
            Notes: notes,
            ReferenceNumber: _receipt.ReferenceNumber,
            ReceiptAmount: _receipt.Amount,
            ReceiptDate: _receipt.Date,
            ReceiptPhotoUrl: photoUrl);
    }

    private bool TryReadAmount(out decimal amount)
    {
        string text = AmountReceivedText.Trim().Replace(",", string.Empty);
        return decimal.TryParse(text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out amount)
               && amount > 0
               && decimal.Round(amount, 2) == amount;
    }

    private static string Money(decimal amount) =>
        "₱ " + amount.ToString("#,##0.00", CultureInfo.InvariantCulture);

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        OnPropertyChanged(name);
    }
}
