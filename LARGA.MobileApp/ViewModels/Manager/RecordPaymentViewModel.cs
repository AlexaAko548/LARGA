using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Messaging;
using LARGA.MobileApp.Services;
using LARGA.MobileApp.Views.Manager;
using LARGA.SharedCore;
using LARGA.SharedCore.Ledger;
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
    // Unselected toggle colors follow the light/dark palette (read when the toggle repaints).
    private static Color LightBlue => ThemeColors.Get("LargaLightBlue", Color.FromArgb("#A6DCEE"));
    private static Color DarkText => ThemeColors.Get("LargaDarkText", Color.FromArgb("#1D1D1D"));
    private static readonly Color White = Color.FromArgb("#FFFFFF");

    private readonly QuickLedgerService _service;
    private QuickLedgerSnapshot? _snapshot;
    private PendingBoundaryRow? _boundaryRow;
    private IReadOnlyList<OtherPaymentRow> _drivers = Array.Empty<OtherPaymentRow>();
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
    private string _allocationText = string.Empty;
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
    public string AmountReceivedText
    {
        get => _amountReceivedText;
        set
        {
            Set(ref _amountReceivedText, value);
            RefreshAllocation();
        }
    }

    /// <summary>Where the amount entered will go when it's more than this shift / debt (empty otherwise). Same split
    /// the web's Record Payment and Settle Debt show.</summary>
    public string AllocationText { get => _allocationText; private set => Set(ref _allocationText, value); }

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
        _drivers = Array.Empty<OtherPaymentRow>();
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
        // Every driver in users, not only those who owe money. Each one's expected amount is their current debt (0 if none).
        _drivers = snapshot.Result.OtherPayments;
        IsOtherMode = true;

        Title = "Record Other Payment";
        DriverOptions.Clear();
        foreach (OtherPaymentRow driver in _drivers)
        {
            DriverOptions.Add(driver.DriverName);
        }

        int index = driverId is null ? -1 : IndexOfDriver(driverId);
        _selectedDriverIndex = index >= 0 ? index : 0;
        OnPropertyChanged(nameof(SelectedDriverIndex));

        ResetForm();
        RefreshOtherSelection();
        IsOpen = true;
    }

    private int IndexOfDriver(string driverId)
    {
        for (int i = 0; i < _drivers.Count; i++)
        {
            if (_drivers[i].DriverId == driverId) return i;
        }
        return -1;
    }

    private OtherPaymentRow? SelectedDriver =>
        SelectedDriverIndex >= 0 && SelectedDriverIndex < _drivers.Count ? _drivers[SelectedDriverIndex] : null;

    private void RefreshOtherSelection()
    {
        if (_snapshot is null || SelectedDriver is null)
        {
            DriverName = string.Empty;
            PlateText = "No drivers found";
            ExpectedText = Money(0m);
            return;
        }

        DriverName = SelectedDriver.DriverName;
        PlateText = SelectedDriver.AmountOwed > 0 ? "Debt" : "No outstanding debt";
        ExpectedText = Money(QuickLedgerCalculator.TotalOwedBy(_snapshot.Input, PhilippineTime.Now, SelectedDriver.DriverId));
        RefreshAllocation();
    }

    // The plan for the amount typed so far (no evidence - that's only built on Confirm).
    private PaymentPlan? PlanFor(decimal amount, string method, PaymentEvidence? evidence, DateTime nowUtc)
    {
        if (_snapshot is null) return null;

        if (IsOtherMode)
        {
            return SelectedDriver is null
                ? null
                : QuickLedgerCalculator.PlanDebtSettlement(_snapshot.Input, SelectedDriver.DriverId, amount, method, evidence, nowUtc);
        }

        return _boundaryRow is null
            ? null
            : QuickLedgerCalculator.PlanBoundaryPayment(_snapshot.Input, _boundaryRow, amount, method, evidence, nowUtc);
    }

    private void RefreshAllocation()
    {
        PaymentPlan? plan = TryReadAmount(out decimal amount)
            ? PlanFor(amount, QuickLedgerCalculator.CashMethod, null, DateTime.UtcNow)
            : null;
        AllocationText = plan is null ? string.Empty : DescribeAllocation(plan, IsOtherMode);
    }

    private static string DescribeAllocation(PaymentPlan plan, bool isOtherMode)
    {
        // Nothing to explain when it all goes to the one thing on screen.
        bool onlyMain = isOtherMode
            ? plan.Advance == 0 && plan.Unallocated == 0
            : plan.ToOthers == 0 && plan.AdjustmentCredit == 0 && plan.Advance == 0;
        if (onlyMain) return string.Empty;

        var parts = new List<string>();
        if (plan.ToFirst > 0) parts.Add($"{Money(plan.ToFirst)} to this shift");
        if (plan.ToOthers > 0) parts.Add($"{Money(plan.ToOthers)} to {(isOtherMode ? "unpaid shifts" : "older unpaid shifts")}");
        if (plan.AdjustmentCredit > 0) parts.Add($"{Money(plan.AdjustmentCredit)} to manual debt");
        if (plan.Advance > 0) parts.Add($"{Money(plan.Advance)} kept as advance credit");
        return parts.Count == 0 ? string.Empty : "Goes to: " + string.Join(", ", parts) + ".";
    }

    private void ResetForm()
    {
        _receipt = null;
        AmountReceivedText = string.Empty;
        NotesText = string.Empty;
        ReceiptDateText = NotScanned;
        ReferenceText = NotScanned;
        ErrorText = string.Empty;
        AllocationText = string.Empty;
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
                OtherPaymentRow? debtor = SelectedDriver;
                if (debtor is null)
                {
                    ErrorText = "Select a driver.";
                    return;
                }

                // Check the allocation before uploading the photo, so a refused payment doesn't leave a stray file behind.
                // Money above the debt is kept as advance credit on the driver's latest shift (same as the web's Settle
                // Debt); it's only refused when the driver has no shift to hold it.
                PaymentPlan? preview = PlanFor(amount, method, null, nowUtc);
                if (preview is null || preview.Unallocated > 0)
                {
                    ErrorText = "This driver has no shifts yet to hold an advance payment - record only what they owe.";
                    return;
                }

                PaymentEvidence evidence = await BuildEvidenceAsync(note.Length > 0 ? note : null, nowUtc, debtor.DriverId);
                PaymentPlan plan = PlanFor(amount, method, evidence, nowUtc)!;

                await _service.SavePaymentPlanAsync(plan, debtor.DriverId, nowUtc, _snapshot.LedgerVersionFor(debtor.DriverId));
                auditAction = "QuickLedgerDebtPaymentRecorded";
                auditDetails = DescribeSettlement(debtor.DriverName, amount, method, plan, evidence);
            }
            else
            {
                // Money above this shift's balance goes to the driver's older debt, then advance credit - same as the
                // web's Record Payment.
                PendingBoundaryRow row = _boundaryRow!;
                PaymentEvidence? evidence = IsEWallet ? await BuildEvidenceAsync(null, nowUtc, row.ShiftKey) : null;
                PaymentPlan? plan = PlanFor(amount, method, evidence, nowUtc);
                if (plan is null || plan.PaymentUpdates.Count == 0)
                {
                    ErrorText = "This shift could not be found. Pull to refresh and try again.";
                    return;
                }

                await _service.SavePaymentPlanAsync(plan, row.DriverId, nowUtc, _snapshot.LedgerVersionFor(row.DriverId));
                auditAction = "QuickLedgerPaymentRecorded";
                auditDetails = DescribeBoundaryPayment(row, amount, method, plan, evidence);
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
        PendingBoundaryRow row, decimal amount, string method, PaymentPlan plan, PaymentEvidence? evidence)
    {
        string status = plan.PaymentUpdates.FirstOrDefault(w => w.ShiftId == row.ShiftKey)?.PaymentStatus ?? "Partial";
        string split = DescribeAllocation(plan, isOtherMode: false);
        return $"Recorded {Money(amount)} {method} payment against {row.DriverName}'s boundary for shift {row.ShiftKey} " +
               $"(transaction {plan.TransactionId}). Shift status: {status}." +
               (split.Length > 0 ? " " + split : string.Empty) + EvidenceSuffix(evidence);
    }

    private static string DescribeSettlement(
        string driverName, decimal amount, string method, PaymentPlan plan, PaymentEvidence? evidence)
    {
        string credit = plan.AdjustmentCredit > 0
            ? $", with {Money(plan.AdjustmentCredit)} credited against manual debt"
            : string.Empty;
        string advance = plan.Advance > 0 ? $", and {Money(plan.Advance)} kept as advance credit" : string.Empty;

        return $"Recorded {Money(amount)} {method} debt payment from {driverName} (transaction {plan.TransactionId}), " +
               $"applied to {plan.PaymentUpdates.Count} shift(s){credit}{advance}." + EvidenceSuffix(evidence);
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
