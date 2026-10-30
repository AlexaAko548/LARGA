using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Messaging;
using LARGA.MobileApp.Controls;
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
    private string _amountPreviewText = string.Empty;
    private string _errorText = string.Empty;
    private string _allocationText = string.Empty;
    private string _receiptDateText = NotScanned;
    private string _referenceText = NotScanned;
    private string _targetAccountText = NotScanned;
    private string _targetAccountWarning = string.Empty;
    private int _selectedDriverIndex;
    private int _selectedCategoryIndex;

    private const string NotScanned = "---";
    private const int MaxNotesLength = 500;
    // Same cap and message as ManagerWeb's Record Payment / Settle Debt (FinancialLedger.razor), via the
    // shared InputValidator.ValidateAmount - was missing here, so the mobile form let an amount of any size
    // through instead of rejecting it before it became advance credit.
    private const decimal MaxPayment = 100_000m;
    private string _notesText = string.Empty;

    /// <summary>Raised after a payment is saved, so the ledger can reload.</summary>
    public event EventHandler? Saved;

    public ObservableCollection<string> DriverOptions { get; } = new();
    public IReadOnlyList<string> CategoryOptions { get; } = QuickLedgerCalculator.OtherPaymentCategories;

    public ICommand CloseCommand { get; }
    public ICommand CashCommand { get; }
    public ICommand EWalletCommand { get; }
    public ICommand ScanCommand { get; }
    public ICommand ConfirmCommand { get; }
    public ICommand PickDriverCommand { get; }
    public ICommand PickCategoryCommand { get; }

    public RecordPaymentViewModel(QuickLedgerService service)
    {
        _service = service;

        CloseCommand = new Command(() => IsOpen = false);
        CashCommand = new Command(() => IsEWallet = false);
        EWalletCommand = new Command(() => IsEWallet = true);
        ScanCommand = new Command(async () => await OpenScannerAsync());
        ConfirmCommand = new Command(async () => await ConfirmAsync());
        PickDriverCommand = new Command(async () => await PickAsync("Select Driver", DriverOptions, SelectedDriverIndex, i => SelectedDriverIndex = i));
        PickCategoryCommand = new Command(async () => await PickAsync("Category", CategoryOptions, SelectedCategoryIndex, i => SelectedCategoryIndex = i));

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
            // Only ever strips characters here (a leading minus, digits + at most one dot) - never
            // inserts any. Growing the string (e.g. adding thousands commas) while it's the live text of a
            // two-way-bound Entry crashed the native Android EditText mid-keystroke (its cursor/selection
            // logic doesn't expect its own TextChanged callback to come back with a longer string) - that
            // was the "more than 3 digits crashes the app" bug, since 4 digits is where a first comma would
            // have been inserted. AmountPreviewText below shows the comma-grouped amount instead, in its own
            // read-only label that never feeds back into this Entry.
            string sanitized = SanitizeAmountInput(value);
            _amountReceivedText = sanitized;
            // Not Set(): when a stripped character leaves the same string already held, the usual equality
            // check would skip notifying and the Entry would keep showing it. Always notifying re-pushes the
            // sanitized text back into the Entry even then.
            OnPropertyChanged();
            AmountPreviewText = FormatAmountPreview(_amountReceivedText);
            RefreshAllocation();
        }
    }

    /// <summary>₱ 1,234.56 preview of <see cref="AmountReceivedText"/> for clarity on large amounts - shown in
    /// its own label next to the input field, never written back into it (see AmountReceivedText).</summary>
    public string AmountPreviewText
    {
        get => _amountPreviewText;
        private set
        {
            Set(ref _amountPreviewText, value);
            OnPropertyChanged(nameof(HasAmountPreview));
        }
    }
    public bool HasAmountPreview => !string.IsNullOrEmpty(AmountPreviewText);

    /// <summary>Strips anything but a leading minus, digits and one decimal point. A minus and extra decimals are
    /// kept rather than dropped, so "-100" and "800.555" reach InputValidator and get its message ("must be more
    /// than ₱0", "at most 2 decimal places") instead of quietly turning into ₱100 / ₱800.55.</summary>
    private static string SanitizeAmountInput(string raw)
    {
        if (string.IsNullOrEmpty(raw)) return raw;

        var digits = new StringBuilder();
        bool sawDot = false;
        foreach (char c in raw.TrimStart())
        {
            if (char.IsDigit(c))
            {
                digits.Append(c);
            }
            else if (c == '-' && digits.Length == 0)
            {
                digits.Append(c);
            }
            else if (c == '.' && !sawDot)
            {
                digits.Append(c);
                sawDot = true;
            }
        }

        return digits.ToString();
    }

    /// <summary>The sanitized amount, grouped with thousands commas, for the read-only preview label.</summary>
    private static string FormatAmountPreview(string sanitized)
    {
        if (string.IsNullOrEmpty(sanitized)) return string.Empty;

        string sign = sanitized.StartsWith('-') ? "-" : string.Empty;
        string unsigned = sanitized[sign.Length..];
        int dotIndex = unsigned.IndexOf('.');
        string integerPart = dotIndex < 0 ? unsigned : unsigned[..dotIndex];
        string? decimalPart = dotIndex < 0 ? null : unsigned[(dotIndex + 1)..];

        string grouped = integerPart.Length > 0 && long.TryParse(integerPart, NumberStyles.None, CultureInfo.InvariantCulture, out long whole)
            ? whole.ToString("#,##0", CultureInfo.InvariantCulture)
            : integerPart;

        return decimalPart is null ? $"₱ {sign}{grouped}" : $"₱ {sign}{grouped}.{decimalPart}";
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
    public string TargetAccountText { get => _targetAccountText; private set => Set(ref _targetAccountText, value); }

    /// <summary>Shown under the receipt details when the receipt wasn't sent to an authorized payout account.</summary>
    public string TargetAccountWarning
    {
        get => _targetAccountWarning;
        private set
        {
            Set(ref _targetAccountWarning, value);
            OnPropertyChanged(nameof(HasTargetAccountWarning));
        }
    }
    public bool HasTargetAccountWarning => !string.IsNullOrEmpty(TargetAccountWarning);

    public int SelectedDriverIndex
    {
        get => _selectedDriverIndex;
        set
        {
            Set(ref _selectedDriverIndex, value);
            OnPropertyChanged(nameof(SelectedDriverText));
            if (IsOtherMode) RefreshOtherSelection();
        }
    }

    public int SelectedCategoryIndex
    {
        get => _selectedCategoryIndex;
        set
        {
            Set(ref _selectedCategoryIndex, value);
            OnPropertyChanged(nameof(SelectedCategoryText));
        }
    }

    // What the dropdown fields show: the current choice, or the prompt when there's nothing to pick.
    public string SelectedDriverText =>
        SelectedDriverIndex >= 0 && SelectedDriverIndex < DriverOptions.Count ? DriverOptions[SelectedDriverIndex] : "Select Driver";
    public string SelectedCategoryText => SelectedCategory;

    // Debt when nothing valid is picked, so an Other Payment always carries a category.
    private string SelectedCategory =>
        SelectedCategoryIndex >= 0 && SelectedCategoryIndex < CategoryOptions.Count
            ? CategoryOptions[SelectedCategoryIndex]
            : QuickLedgerCalculator.DebtCategory;

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
        OnPropertyChanged(nameof(SelectedDriverText));
        SelectedCategoryIndex = 0;

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
        TargetAccountText = NotScanned;
        TargetAccountWarning = string.Empty;
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
        TargetAccountText = scan.AccountCheck?.Target?.Display ?? NotScanned;
        TargetAccountWarning = scan.AccountCheck is { NeedsWarning: true } check ? "⚠ " + check.Message : string.Empty;
        ErrorText = string.Empty;
    }

    private static async Task PickAsync(string title, IReadOnlyList<string> options, int selectedIndex, Action<int> select)
    {
        if (options.Count == 0 || Shell.Current is null) return;

        int? picked = await OptionPickerPopup.ShowAsync(Shell.Current, title, options, selectedIndex);
        if (picked is int index) select(index);
    }

    private async Task OpenScannerAsync()
    {
        await LARGA.MobileApp.Services.NavigationGate.PushModalAsync(() =>
            Application.Current!.Handler.MauiContext!.Services.GetRequiredService<ScanEReceiptPage>());
    }

    // Covers the whole confirm, popups included - IsBusy is only set once the payment is being written.
    private readonly BusyGate _confirmGate = new();

    private Task ConfirmAsync() => _confirmGate.RunAsync(ConfirmCoreAsync, showLoading: false);

    private async Task ConfirmCoreAsync()
    {
        if (_snapshot is null || IsBusy) return;

        ErrorText = string.Empty;

        string? amountError = ValidateAmount(out decimal amount);
        if (amountError is not null)
        {
            ErrorText = amountError;
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

            // Target account verification: a receipt sent to an account that isn't one of the authorized payout
            // accounts may be a misrouted payment. Warn and let the manager decide - it's still recorded, flagged.
            if (_receipt.AccountCheck is { NeedsWarning: true } check)
            {
                Page? page = Shell.Current?.CurrentPage;
                bool proceed = page is not null && await page.DisplayAlert(
                    "Check the receiving account",
                    check.Message + "\n\nRecord this payment anyway? It will be flagged in the ledger.",
                    "Record anyway",
                    "Cancel");
                if (!proceed)
                {
                    ErrorText = "Payment not recorded - the receipt wasn't sent to an authorized account.";
                    return;
                }
            }
        }

        bool confirmed = await ConfirmationPopup.ShowAsync(Shell.Current,
            "Confirm This Payment?",
            $"Recording {Money(amount)} ({(IsEWallet ? "E-Wallet / GCash" : "Cash")}) " +
            $"{(IsOtherMode ? SelectedCategory.ToLowerInvariant() + " payment " : string.Empty)}for {DriverName}.",
            confirmText: "Confirm Payment", isDangerous: false);
        if (!confirmed) return;

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

                PaymentEvidence evidence = await BuildEvidenceAsync(note.Length > 0 ? note : null, nowUtc, debtor.DriverId)
                    with { Category = SelectedCategory };
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
        // Named by unit, not the shift's document ID - the audit log is read by people.
        string unit = string.IsNullOrWhiteSpace(row.TaxiPlate) ? row.TaxiId : row.TaxiPlate;
        return $"Recorded {Money(amount)} {method} payment against {row.DriverName}'s boundary ({unit}). " +
               $"Shift status: {status}." +
               (split.Length > 0 ? " " + split : string.Empty) + EvidenceSuffix(evidence);
    }

    private static string DescribeSettlement(
        string driverName, decimal amount, string method, PaymentPlan plan, PaymentEvidence? evidence)
    {
        string credit = plan.AdjustmentCredit > 0
            ? $", with {Money(plan.AdjustmentCredit)} credited against manual debt"
            : string.Empty;
        string advance = plan.Advance > 0 ? $", and {Money(plan.Advance)} kept as advance credit" : string.Empty;

        string category = (evidence?.Category ??QuickLedgerCalculator.DebtCategory).ToLowerInvariant();
        return $"Recorded {Money(amount)} {method} {category} payment from {driverName}, " +
               $"applied to {plan.PaymentUpdates.Count} shift(s){credit}{advance}." + EvidenceSuffix(evidence);
    }

    private static string EvidenceSuffix(PaymentEvidence? evidence)
    {
        if (evidence is null) return string.Empty;

        string note = string.IsNullOrEmpty(evidence.Notes) ? string.Empty : $" Note: {evidence.Notes}";
        string receipt = string.IsNullOrEmpty(evidence.ReferenceNumber)
            ? string.Empty
            : $" GCash reference {evidence.ReferenceNumber}; receipt photo stored.";
        string account = PayoutAccountStatus.NeedsWarning(evidence.TargetAccountStatus)
            ? $" WARNING: target account {evidence.TargetAccount ?? "unreadable"} is not an authorized payout account ({evidence.TargetAccountStatus}) - recorded anyway."
            : string.Empty;
        return note + receipt + account;
    }

    private async Task<PaymentEvidence> BuildEvidenceAsync(string? notes, DateTime nowUtc, string label)
    {
        if (!IsEWallet || _receipt is null)
        {
            return new PaymentEvidence(notes, null, null, null, null);
        }

        // BRP-008: refuse a GCash reference that's already on a payment before uploading its photo.
        if (await _service.IsReferenceRecordedAsync(_receipt.ReferenceNumber))
        {
            throw new DuplicatePaymentReferenceException();
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
            ReceiptPhotoUrl: photoUrl,
            TargetAccount: _receipt.AccountCheck?.Target?.Display,
            TargetAccountStatus: _receipt.AccountCheck?.Status);
    }

    private decimal? ParseAmount()
    {
        string text = AmountReceivedText.Trim().Replace(",", string.Empty);
        // Leading sign allowed, so "-100" is "must be more than ₱0" rather than read as blank.
        return decimal.TryParse(text, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out decimal value)
            ? value
            : null;
    }

    /// <summary>Null when the typed amount is usable (more than ₱0, at most 2 decimals, at most <see cref="MaxPayment"/>);
    /// otherwise the specific reason, same wording as ManagerWeb's Record Payment / Settle Debt.</summary>
    private string? ValidateAmount(out decimal amount)
    {
        decimal? parsed = ParseAmount();
        amount = parsed ?? 0m;
        return InputValidator.ValidateAmount(parsed, "the amount received", MaxPayment);
    }

    private bool TryReadAmount(out decimal amount) => ValidateAmount(out amount) is null;

    private static string Money(decimal amount) =>
        "₱ " + amount.ToString("#,##0.00", CultureInfo.InvariantCulture);

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        OnPropertyChanged(name);
    }
}
