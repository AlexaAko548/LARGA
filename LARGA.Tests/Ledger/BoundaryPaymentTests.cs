using Google.Cloud.Firestore;
using LARGA.SharedCore;
using LARGA.SharedCore.Ledger;
using LARGA.SharedCore.Models.FinancialLedger;
using LARGA.SharedCore.Services;
using LARGA.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;

namespace LARGA.Tests.Ledger;

/// <summary>Philippine wall-clock time -> UTC, for building shifts at exact PH times.</summary>
internal static class Ph
{
    public static DateTime Utc(DateTime phLocal) => DateTime.SpecifyKind(phLocal - PhilippineTime.Offset, DateTimeKind.Utc);

    public static DateTime Today => PhilippineTime.Now.Date;

    public static ShiftRecord Shift(string id, string driver, DateTime? startUtc, DateTime? endUtc, string status = "Completed",
        decimal? lateFee = null, decimal? fuelPenalty = null) =>
        new(id, id, driver, "TAXI_T", startUtc, status, lateFee, fuelPenalty, endUtc);

    public static BoundaryPaymentRecord Pay(string docId, string shiftId, decimal amount, string status = "Partial",
        decimal expected = 800m, string txn = "TXN", DateTime? at = null) =>
        new(docId, shiftId, expected, 0m, 0m, amount, status, "Cash", at ?? DateTime.UtcNow, txn);

    public static QuickLedgerInput Input(IEnumerable<ShiftRecord> shifts, IEnumerable<BoundaryPaymentRecord>? payments = null,
        IEnumerable<DebtAdjustmentRecord>? adjustments = null, decimal rate = 800m, params string[] drivers) =>
        new(shifts.ToList(), (payments ?? []).ToList(), (adjustments ?? []).ToList(),
            new Dictionary<string, string>(), drivers.Length == 0 ? ["d1"] : drivers, new Dictionary<string, string>(), rate);
}

/// <summary>M2 Boundary &amp; Payment (BRP-xxx) - the shared rules (ShiftRules, BoundaryPaymentRules, InputValidator) and
/// the phone apps' QuickLedgerCalculator. No Firestore.</summary>
public class BoundaryRuleTests
{
    private static readonly DateTime Start0630 = Ph.Utc(new DateTime(2026, 10, 20, 6, 30, 0));

    private static decimal FeeAt(int day, int hour, int minute, int second = 0) =>
        ShiftRules.LateReturnFee(Start0630, Ph.Utc(new DateTime(2026, 10, day, hour, minute, second)));

    // ---------- BRP-015 / 016 / 017: late fee ----------

    [Fact]
    public void Brp015_ReturnWithinGrace_NoFee()
    {
        Assert.Equal(0m, FeeAt(20, 22, 0));
        Assert.Equal(0m, FeeAt(20, 22, 29));
        Assert.Equal(0m, FeeAt(20, 22, 29, 59));
    }

    [Fact]
    public void Brp016_FirstHour_Php100_AndDue900()
    {
        Assert.Equal(100m, FeeAt(20, 22, 30));
        Assert.Equal(100m, FeeAt(20, 22, 45));
        Assert.Equal(100m, FeeAt(20, 23, 0, 45));
        Assert.Equal(900m, ShiftRules.DefaultBoundaryRate + FeeAt(20, 23, 0, 45));
    }

    [Fact]
    public void Brp017_AdditionalStartedHours_DeadlineIsShiftStartDay()
    {
        Assert.Equal(200m, FeeAt(20, 23, 1));
        Assert.Equal(200m, FeeAt(21, 0, 0));
        Assert.Equal(300m, FeeAt(21, 0, 1));
    }

    // ---------- BRP-018: stored (manager) late fee wins ----------

    [Fact]
    public void Brp018_StoredZeroLateFee_OverridesComputed200()
    {
        DateTime end = Ph.Utc(new DateTime(2026, 10, 20, 23, 30, 0));
        Assert.Equal(200m, ShiftRules.EffectiveLateFee(null, Start0630, end));
        Assert.Equal(0m, ShiftRules.EffectiveLateFee(0m, Start0630, end));

        var input = Ph.Input([Ph.Shift("s1", "d1", Start0630, end, lateFee: 0m)]);
        Assert.Equal(800m, QuickLedgerCalculator.DriverDebt(input, "d1").TotalDebt);
    }

    // ---------- BRP-019: combined charges ----------

    [Fact]
    public void Brp019_Late2315_PlusLowFuel_Due1050()
    {
        DateTime end = Ph.Utc(new DateTime(2026, 10, 20, 23, 15, 0));
        var input = Ph.Input([Ph.Shift("s1", "d1", Start0630, end, fuelPenalty: ShiftRules.LowFuelPenalty)]);
        DriverDebtResult debt = QuickLedgerCalculator.DriverDebt(input, "d1");
        Assert.Equal(1050m, debt.Shifts.Single().Expected);
        Assert.Equal(1050m, debt.TotalDebt);
    }

    // ---------- BRP-001: itemized debt sums to the total ----------

    [Fact]
    public void Brp001_Itemized500_200_300_SumsTo1000()
    {
        var shifts = new[]
        {
            Ph.Shift("s18", "d1", Ph.Utc(new DateTime(2026, 7, 18, 7, 0, 0)), Ph.Utc(new DateTime(2026, 7, 18, 21, 0, 0))),
            Ph.Shift("s20", "d1", Ph.Utc(new DateTime(2026, 7, 20, 7, 0, 0)), Ph.Utc(new DateTime(2026, 7, 20, 21, 0, 0))),
            Ph.Shift("s22", "d1", Ph.Utc(new DateTime(2026, 7, 22, 7, 0, 0)), Ph.Utc(new DateTime(2026, 7, 22, 21, 0, 0))),
        };
        var pays = new[] { Ph.Pay("p18", "s18", 500, txn: "t18"), Ph.Pay("p20", "s20", 600, txn: "t20"), Ph.Pay("p22", "s22", 300, txn: "t22") };
        DriverDebtResult debt = QuickLedgerCalculator.DriverDebt(Ph.Input(shifts, pays), "d1");
        Assert.Equal([300m, 200m, 500m], debt.Shifts.Select(s => s.Remaining).ToArray());
        Assert.Equal(1000m, debt.Shifts.Sum(s => s.OwedAfterCredit));
        Assert.Equal(1000m, debt.TotalDebt);
    }

    // ---------- BRP-007: amount validation messages (shared by web and the manager app) ----------

    [Theory]
    [InlineData(null, "Enter the amount received.")]
    [InlineData("0", "The amount received must be more than ₱0.")]
    [InlineData("-100", "The amount received must be more than ₱0.")]
    [InlineData("800.555", "The amount received can have at most 2 decimal places.")]
    [InlineData("100001", "The amount received can't be more than ₱100,000.")]
    [InlineData("800", null)]
    public void Brp007_ValidateAmount_ExactMessages(string? amount, string? expected)
    {
        decimal? value = amount is null ? null : decimal.Parse(amount, System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(expected, InputValidator.ValidateAmount(value, "the amount received", 100_000m));
    }

    /// <summary>The manager app's RecordPaymentViewModel.ParseAmount, copied verbatim (the MAUI project can't be
    /// referenced): strips commas and parses with AllowDecimalPoint only - no leading sign.</summary>
    private static decimal? MobileParseAmount(string text)
    {
        text = text.Trim().Replace(",", string.Empty);
        return decimal.TryParse(text, System.Globalization.NumberStyles.AllowDecimalPoint, System.Globalization.CultureInfo.InvariantCulture, out decimal v) ? v : null;
    }

    [Fact]
    public void Brp007_ManagerApp_NegativeAmount_GetsBlankMessage()
    {
        // "-100" doesn't parse, so it's reported as blank instead of "must be more than ₱0".
        Assert.Null(MobileParseAmount("-100"));
        Assert.Equal("Enter the amount received.", InputValidator.ValidateAmount(MobileParseAmount("-100"), "the amount received", 100_000m));
        Assert.Equal("The amount received can't be more than ₱100,000.",
            InputValidator.ValidateAmount(MobileParseAmount("100,001"), "the amount received", 100_000m));
        Assert.Equal("The amount received can't be more than ₱100,000.",
            InputValidator.ValidateAmount(MobileParseAmount("1,000,001"), "the amount received", 100_000m));
    }

    // ---------- BRP-020: overpayment -> oldest unpaid, rest advance credit (manager app plan) ----------

    [Fact]
    public void Brp020_ManagerApp_1100_ClearsTuesdayAndMonday_100Advance()
    {
        DateTime mon = Ph.Utc(new DateTime(2026, 10, 19, 6, 30, 0));
        DateTime tue = Ph.Utc(new DateTime(2026, 10, 20, 6, 30, 0));
        var shifts = new[] { Ph.Shift("mon", "d1", mon, mon.AddHours(14)), Ph.Shift("tue", "d1", tue, tue.AddHours(14)) };
        var pays = new[] { Ph.Pay("mon_PAY_20261019220000000", "mon", 600, txn: "t0", at: mon.AddHours(15)) };
        QuickLedgerInput input = Ph.Input(shifts, pays);
        var row = new PendingBoundaryRow("tue", "d1", "D", "TAXI_T", "T", 800, 0, 0, 0);

        PaymentPlan plan = QuickLedgerCalculator.PlanBoundaryPayment(input, row, 1100m, "Cash", null, DateTime.UtcNow);

        Assert.Equal(800m, plan.ToFirst);
        Assert.Equal(200m, plan.ToOthers);
        Assert.Equal(100m, plan.Advance);
        Assert.Equal(900m, plan.PaymentUpdates.Single(w => w.ShiftId == "tue").AmountPaid);
        Assert.Equal(200m, plan.PaymentUpdates.Single(w => w.ShiftId == "mon").AmountPaid);

        var after = Ph.Input(shifts, pays.Concat(plan.PaymentUpdates.Select(w =>
            new BoundaryPaymentRecord(w.DocumentId, w.ShiftId, w.ExpectedBoundary, 0, 0, w.AmountPaid, w.PaymentStatus, "Cash", DateTime.UtcNow, w.TransactionId))));
        DriverDebtResult debt = QuickLedgerCalculator.DriverDebt(after, "d1");
        Assert.Equal(0m, debt.TotalDebt);
        Assert.Equal(100m, debt.OverpaymentCredit);
    }

    // ---------- BRP-021: advance with no shift to hold it (manager app plan) ----------

    [Fact]
    public void Brp021_ManagerApp_NoShifts_500IsUnallocated()
    {
        PaymentPlan plan = QuickLedgerCalculator.PlanDebtSettlement(Ph.Input([]), "d1", 500m, "Cash", null, DateTime.UtcNow);
        Assert.Empty(plan.PaymentUpdates);
        Assert.Equal(500m, plan.Unallocated);
    }

    // ---------- BRP-023: legacy running total + individual payment ----------

    [Fact]
    public void Brp023_Legacy500_PlusStamped300_Is800()
    {
        decimal paid = BoundaryPaymentRules.TotalPaid([("s1_PAY", null, 500m), ("s1_PAY", null, 500m), ("s1_PAY_20261020220000123", null, 300m)]);
        Assert.Equal(800m, paid);

        var input = Ph.Input([Ph.Shift("s1", "d1", Start0630, Start0630.AddHours(14))],
            [Ph.Pay("s1_PAY", "s1", 500, txn: ""), Ph.Pay("s1_PAY_20261020220000123", "s1", 300, txn: "")]);
        Assert.Equal(0m, QuickLedgerCalculator.DriverDebt(input, "d1").TotalDebt);
    }

    // ---------- BRP-024: active shift isn't debt ----------

    [Fact]
    public void Brp024_ActiveShift_NotCountedAsDebt()
    {
        var input = Ph.Input([Ph.Shift("s1", "d1", DateTime.UtcNow.AddHours(-2), null, status: "Active")]);
        DriverDebtResult debt = QuickLedgerCalculator.DriverDebt(input, "d1");
        Assert.Empty(debt.Shifts);
        Assert.Equal(0m, debt.TotalDebt);
    }

    // ---------- BRP-025: 3-day flag boundary ----------

    [Fact]
    public void Brp025_MondayShortfall_FlaggedThursdayNotWednesday()
    {
        DateTime mon = Ph.Utc(new DateTime(2026, 10, 19, 6, 30, 0));
        Assert.False(ShiftRules.IsDebtOverdue(mon, Ph.Utc(new DateTime(2026, 10, 21, 23, 59, 0))));
        Assert.True(ShiftRules.IsDebtOverdue(mon, Ph.Utc(new DateTime(2026, 10, 22, 0, 1, 0))));
        Assert.Equal(2, ShiftRules.DaysUnpaid(mon, Ph.Utc(new DateTime(2026, 10, 21, 12, 0, 0))));
    }
}

/// <summary>M2 against the local Firestore emulator: ManagerWeb's FinancialLedgerService (Daily Settlements, Master
/// Debt Ledger, Record Payment, Settle Debt, + Adjustment, the debt-flag monitor). Never touches the live project.</summary>
[Collection("emulator")]
public class BoundaryLedgerEmulatorTests
{
    private static FinancialLedgerService Ledger(FirestoreDb db)
    {
        var lazy = new Lazy<FirestoreDb>(db);
        return new FinancialLedgerService(lazy,
            new Lazy<PhotoStorageTarget>(() => throw new InvalidOperationException("no storage in tests")),
            NullLogger<FinancialLedgerService>.Instance, new CollectionCache(lazy, NullLogger<CollectionCache>.Instance));
    }

    private static async Task<FirestoreDb> FreshAsync()
    {
        await Emu.ClearAsync();
        return Emu.Db();
    }

    private static Task Driver(FirestoreDb db, string id, string name = "") =>
        db.Collection("users").Document(id).SetAsync(new Dictionary<string, object>
        {
            ["fullName"] = string.IsNullOrEmpty(name) ? id : name, ["role"] = "Driver", ["email"] = $"{id}@test.local",
        });

    private static Task Shift(FirestoreDb db, string id, string driver, DateTime startUtc, DateTime? endUtc, string status = "Completed",
        double? lateFee = null, double? fuelPenalty = null)
    {
        var d = new Dictionary<string, object>
        {
            ["shiftId"] = id, ["driverId"] = driver, ["taxiId"] = "TAXI_T", ["shiftStart"] = startUtc, ["status"] = status,
            ["startMileage"] = 1000,
        };
        if (endUtc is DateTime e) d["shiftEnd"] = e;
        if (lateFee is double l) d["lateFee"] = l;
        if (fuelPenalty is double f) d["fuelPenalty"] = f;
        return db.Collection("shifts").Document(id).SetAsync(d);
    }

    private static Task Payment(FirestoreDb db, string docId, string shift, string driver, double amount, string status = "Partial",
        double expected = 800, string? txn = "TXN_SEED", DateTime? at = null)
    {
        DateTime when = at ?? DateTime.UtcNow.AddHours(-1);
        var d = new Dictionary<string, object>
        {
            ["shiftId"] = shift, ["driverId"] = driver, ["expectedBoundary"] = expected, ["lateFees"] = 0.0, ["fuelPenalty"] = 0.0,
            ["amountPaid"] = amount, ["paymentMethod"] = "Cash", ["paymentStatus"] = status, ["timestamp"] = when,
            ["recordedAtUtc"] = BoundaryPaymentRules.RecordedAtText(when),
        };
        if (txn is not null) d["transactionId"] = txn;
        return db.Collection("boundary_payments").Document(docId).SetAsync(d);
    }

    private static Task Config(FirestoreDb db, Dictionary<string, object> fields) =>
        db.Collection("system_configs").Document("global").SetAsync(fields);

    private static async Task<DebtLedgerRow> Row(FinancialLedgerService ledger, string driver) =>
        (await ledger.GetDebtLedgerAsync()).Rows.Single(r => r.DriverId == driver);

    private static async Task<List<Dictionary<string, object>>> PaymentsFor(FirestoreDb db, string shift) =>
        (await db.Collection("boundary_payments").WhereEqualTo("shiftId", shift).GetSnapshotAsync()).Documents.Select(d => d.ToDictionary()).ToList();

    // An on-time shift that ended yesterday (PH), no payment yet.
    private static (DateTime Start, DateTime End) Yesterday() =>
        (Ph.Utc(Ph.Today.AddDays(-1).AddHours(6.5)), Ph.Utc(Ph.Today.AddDays(-1).AddHours(21)));

    // ---------- BRP-013 / 014: boundary rate ----------

    [EmulatorFact]
    public async Task Brp013_NoConfig_Or_Zero_Is800()
    {
        FirestoreDb db = await FreshAsync();
        await Driver(db, "d1");
        (DateTime s, DateTime e) = Yesterday();
        await Shift(db, "s1", "d1", s, e);
        FinancialLedgerService ledger = Ledger(db);

        Assert.Equal(800m, (await Row(ledger, "d1")).TotalDebt);
        await Config(db, new() { ["defaultBoundaryRate"] = 0 });
        Assert.Equal(800m, (await Row(ledger, "d1")).TotalDebt);
    }

    [EmulatorFact]
    public async Task Brp014_Configured900_UsedByWebLedger()
    {
        FirestoreDb db = await FreshAsync();
        await Driver(db, "d1");
        (DateTime s, DateTime e) = Yesterday();
        await Shift(db, "s1", "d1", s, e);
        await Config(db, new() { ["defaultBoundaryRate"] = 900 });

        Assert.Equal(900m, (await Row(Ledger(db), "d1")).TotalDebt);
    }

    /// <summary>Same as above, but system_configs/global also carries a value of the wrong type in an unrelated
    /// field - FleetReportingService's comment says the live document had fuelExpectedKmPerLiter = "". The web
    /// ledger reads the whole document as SystemConfig.</summary>
    [EmulatorFact]
    public async Task Brp014_Configured900_WithMalformedUnrelatedField_StillUsed()
    {
        FirestoreDb db = await FreshAsync();
        await Driver(db, "d1");
        (DateTime s, DateTime e) = Yesterday();
        await Shift(db, "s1", "d1", s, e);
        await Config(db, new() { ["defaultBoundaryRate"] = 900, ["fuelExpectedKmPerLiter"] = "" });

        Assert.Equal(900m, (await Row(Ledger(db), "d1")).TotalDebt);
    }

    // ---------- BRP-009: Daily Settlements KPIs ----------

    [EmulatorFact]
    public async Task Brp009_FiveShiftsToday_Expected4000_Collected2400()
    {
        FirestoreDb db = await FreshAsync();
        double[] paid = [800, 800, 400, 400, 0];
        for (int i = 0; i < 5; i++)
        {
            await Driver(db, $"d{i}");
            DateTime start = Ph.Utc(Ph.Today.AddMinutes(1 + i));
            await Shift(db, $"s{i}", $"d{i}", start, start.AddMinutes(30));
            if (paid[i] > 0)
            {
                await Payment(db, $"s{i}_PAY_2026102600000000{i}", $"s{i}", $"d{i}", paid[i], paid[i] >= 800 ? "Paid" : "Partial", txn: $"t{i}");
            }
        }

        DailySettlementSnapshot day = await Ledger(db).GetDailySettlementAsync(DateTime.UtcNow);
        Assert.Equal(4000m, day.ExpectedCollection);
        Assert.Equal(2400m, day.CollectedSoFar);
        Assert.Equal(1600m, day.ExpectedCollection - day.CollectedSoFar);
        Assert.Equal((2, 2, 1), (day.ClearedCount, day.PartialCount, day.WaitingCount));
    }

    // ---------- BRP-010: Master Debt Ledger summary vs History ----------

    [EmulatorFact]
    public async Task Brp010_Summary_UnpaidDays_HistoryMatchesRow()
    {
        FirestoreDb db = await FreshAsync();
        await Driver(db, "a"); await Driver(db, "b"); await Driver(db, "c");
        DateTime threeAgo = Ph.Utc(Ph.Today.AddDays(-3).AddHours(6.5));
        DateTime oneAgo = Ph.Utc(Ph.Today.AddDays(-1).AddHours(6.5));
        await Shift(db, "a1", "a", threeAgo, threeAgo.AddHours(14));
        await Shift(db, "a2", "a", oneAgo, oneAgo.AddHours(14));
        await Payment(db, "a2_PAY_20261025140000000", "a2", "a", 300, txn: "ta");
        await db.Collection("debt_adjustments").AddAsync(new Dictionary<string, object> { ["driverId"] = "a", ["amount"] = 200.0, ["reason"] = "seed penalty", ["timestamp"] = DateTime.UtcNow.AddHours(-2) });
        await Shift(db, "b1", "b", oneAgo, oneAgo.AddHours(14));
        await Payment(db, "b1_PAY_20261025140000000", "b1", "b", 800, "Paid", txn: "tb");

        FinancialLedgerService ledger = Ledger(db);
        DebtLedgerSnapshot snap = await ledger.GetDebtLedgerAsync();
        Assert.Equal(1, snap.DriversWithDebtCount);
        Assert.Equal(1500m, snap.TotalOutstandingDebt);
        Assert.Equal(2, snap.DebtFreeDriverCount);
        DebtLedgerRow a = snap.Rows.Single(r => r.DriverId == "a");
        Assert.Equal(3, a.DaysUnpaid);
        Assert.True(a.IsDebtFlagged);

        DriverLedgerHistory history = await ledger.GetDriverHistoryAsync("a");
        Assert.Equal(a.TotalDebt, history.CurrentOutstandingDebt);
        Assert.Equal(a.TotalDebt, history.Transactions.First().RunningDebt);
    }

    /// <summary>History vs row when overpayment credit was paid before a later shift it covers.</summary>
    [EmulatorFact]
    public async Task Brp010_HistoryRunningBalance_MatchesRow_WithAdvanceCredit()
    {
        FirestoreDb db = await FreshAsync();
        await Driver(db, "a");
        DateTime twoAgo = Ph.Utc(Ph.Today.AddDays(-2).AddHours(6.5));
        DateTime oneAgo = Ph.Utc(Ph.Today.AddDays(-1).AddHours(6.5));
        await Shift(db, "a1", "a", twoAgo, twoAgo.AddHours(14));
        await Payment(db, "a1_PAY_20261024140000000", "a1", "a", 1100, "Paid", txn: "t1", at: twoAgo.AddHours(15));
        await Shift(db, "a2", "a", oneAgo, oneAgo.AddHours(14));
        await db.Collection("debt_adjustments").AddAsync(new Dictionary<string, object> { ["driverId"] = "a", ["amount"] = -100.0, ["reason"] = "seed write-off", ["timestamp"] = DateTime.UtcNow.AddHours(-2) });

        FinancialLedgerService ledger = Ledger(db);
        DebtLedgerRow a = await Row(ledger, "a");
        Assert.Equal(400m, a.TotalDebt);
        DriverLedgerHistory history = await ledger.GetDriverHistoryAsync("a");
        Assert.Equal(a.TotalDebt, history.CurrentOutstandingDebt);
        Assert.Equal(a.TotalDebt, history.Transactions.First().RunningDebt);
    }

    // ---------- BRP-011: + Adjustment ----------

    [EmulatorFact]
    public async Task Brp011_Adjustment200_ChangesDebt_AuditHasManagerAndReason_AndErrors()
    {
        FirestoreDb db = await FreshAsync();
        await Driver(db, "a");
        (DateTime s, DateTime e) = Yesterday();
        await Shift(db, "a1", "a", s, e);
        FinancialLedgerService ledger = Ledger(db);
        decimal before = (await Row(ledger, "a")).TotalDebt;

        AdjustmentResult ok = await ledger.AddAdjustmentAsync("a", 200m, "Missed grace period cutoff", "mgr1");
        Assert.True(ok.Ok, ok.ErrorMessage);
        Assert.Equal(before + 200m, (await Row(ledger, "a")).TotalDebt);

        var audits = (await db.Collection("audit_logs").WhereEqualTo("actionType", "DebtAdjustmentRecorded").GetSnapshotAsync()).Documents;
        Dictionary<string, object> audit = Assert.Single(audits).ToDictionary();
        Assert.Equal("mgr1", audit["userId"]);
        Assert.Contains("Missed grace period cutoff", (string)audit["auditLogDetails"]);

        Assert.Equal("Amount can't be zero.", (await ledger.AddAdjustmentAsync("a", 0m, "Missed grace period cutoff")).ErrorMessage);
        Assert.Equal("A reason is required for every adjustment.", (await ledger.AddAdjustmentAsync("a", 200m, "  ")).ErrorMessage);
    }

    // ---------- BRP-018 / 019: extras on the web ledger ----------

    [EmulatorFact]
    public async Task Brp018_StoredLateFeeZero_Web800()
    {
        FirestoreDb db = await FreshAsync();
        await Driver(db, "a");
        DateTime start = Ph.Utc(Ph.Today.AddDays(-1).AddHours(6.5));
        DateTime end = Ph.Utc(Ph.Today.AddDays(-1).AddHours(23.5));
        await Shift(db, "a1", "a", start, end, lateFee: 0);
        await Shift(db, "a2", "a", start.AddDays(-1), end.AddDays(-1));
        DriverLedgerHistory history = await Ledger(db).GetDriverHistoryAsync("a");
        Assert.Equal(800m + 1000m, history.CurrentOutstandingDebt); // a1 waived, a2 computed 200
    }

    [EmulatorFact]
    public async Task Brp019_Late2315_LowFuel_Web1050()
    {
        FirestoreDb db = await FreshAsync();
        await Driver(db, "a");
        DateTime start = Ph.Utc(Ph.Today.AddDays(-1).AddHours(6.5));
        DateTime end = Ph.Utc(Ph.Today.AddDays(-1).AddHours(23.25));
        await Shift(db, "a1", "a", start, end, fuelPenalty: 50);
        Assert.Equal(1050m, (await Row(Ledger(db), "a")).TotalDebt);
    }

    // ---------- BRP-020: Record Payment 1,100 on Tuesday ----------

    [EmulatorFact]
    public async Task Brp020_Web_1100OnTuesday_ClearsMonday_100Advance()
    {
        FirestoreDb db = await FreshAsync();
        await Driver(db, "a");
        DateTime mon = Ph.Utc(Ph.Today.AddDays(-2).AddHours(6.5));
        DateTime tue = Ph.Utc(Ph.Today.AddDays(-1).AddHours(6.5));
        await Shift(db, "mon", "a", mon, mon.AddHours(14));
        await Payment(db, "mon_PAY_20261024140000000", "mon", "a", 600, txn: "t0", at: mon.AddHours(15));
        await Shift(db, "tue", "a", tue, tue.AddHours(14));
        FinancialLedgerService ledger = Ledger(db);

        RecordPaymentResult r = await ledger.RecordPaymentAsync("tue", 1100m, "Cash", null, "mgr1");
        Assert.True(r.Ok, r.ErrorMessage);
        Assert.Equal(SettlementStatus.Cleared, r.NewStatus);
        Assert.Equal(200m, r.AppliedToOlderDebt);
        Assert.Equal(100m, r.AdvanceCredit);

        DebtLedgerRow row = await Row(ledger, "a");
        Assert.Equal(0m, row.TotalDebt);
        Assert.Equal(100m, row.AdvanceCredit);
        Assert.Equal(900.0, (await PaymentsFor(db, "tue")).Sum(p => Convert.ToDouble(p["amountPaid"])));
        Assert.Equal(800.0, (await PaymentsFor(db, "mon")).Sum(p => Convert.ToDouble(p["amountPaid"])));
    }

    // ---------- BRP-021: advance refused for a driver with no shifts ----------

    [EmulatorFact]
    public async Task Brp021_SettleDebt500_NoShifts_Refused()
    {
        FirestoreDb db = await FreshAsync();
        await Driver(db, "new");
        SettleDebtResult r = await Ledger(db).SettleDebtAsync("new", 500m, "Cash");
        Assert.False(r.Ok);
        Assert.Equal("This driver has no shifts yet to hold an advance payment - record only what they owe.", r.ErrorMessage);
        Assert.Empty((await db.Collection("boundary_payments").GetSnapshotAsync()).Documents);
    }

    /// <summary>The tester's note: "+ Adjustment" was the way around it. A credit (negative) adjustment is now refused
    /// the same way.</summary>
    [EmulatorFact]
    public async Task Brp021_CreditAdjustment500_NoShifts_Refused()
    {
        FirestoreDb db = await FreshAsync();
        await Driver(db, "new");
        AdjustmentResult r = await Ledger(db).AddAdjustmentAsync("new", -500m, "Advance payment from driver");
        Assert.False(r.Ok);
        Assert.Equal("This driver has no shifts yet to hold an advance credit - record only what they owe.", r.ErrorMessage);
        Assert.Empty((await db.Collection("debt_adjustments").GetSnapshotAsync()).Documents);
    }

    // ---------- BRP-022: simultaneous payments ----------

    [EmulatorFact]
    public async Task Brp022_TwoSimultaneous400_BothSaved_Total800()
    {
        FirestoreDb db = await FreshAsync();
        await Driver(db, "a");
        (DateTime s, DateTime e) = Yesterday();
        await Shift(db, "a1", "a", s, e);

        RecordPaymentResult[] results = await Task.WhenAll(
            Ledger(db).RecordPaymentAsync("a1", 400m, "Cash"),
            Ledger(Emu.Db()).RecordPaymentAsync("a1", 400m, "Cash"));
        Assert.All(results, r => Assert.True(r.Ok, r.ErrorMessage));

        var docs = await PaymentsFor(db, "a1");
        Assert.Equal(2, docs.Count);
        Assert.Equal(2, docs.Select(d => d["transactionId"]).Distinct().Count());
        Assert.Equal(800.0, docs.Sum(d => Convert.ToDouble(d["amountPaid"])));
        Assert.Equal(0m, (await Row(Ledger(db), "a")).TotalDebt);
    }

    /// <summary>The manager app's side of the race, as SavePaymentPlanAsync does it: the ledger version is read when the
    /// Quick Ledger page loads, and a save only commits if it hasn't moved. A web payment in between bumps it.</summary>
    [EmulatorFact]
    public async Task Brp022_WebPaymentAfterQuickLedgerLoaded_BumpsVersion()
    {
        FirestoreDb db = await FreshAsync();
        await Driver(db, "a");
        (DateTime s, DateTime e) = Yesterday();
        await Shift(db, "a1", "a", s, e);
        DocumentReference lockRef = db.Collection("ledger_locks").Document("a");
        long Version(DocumentSnapshot d) => d.Exists && d.TryGetValue("version", out long v) ? v : 0;

        long seenByPhone = Version(await lockRef.GetSnapshotAsync());
        Assert.True((await Ledger(db).RecordPaymentAsync("a1", 400m, "Cash")).Ok);
        Assert.NotEqual(seenByPhone, Version(await lockRef.GetSnapshotAsync()));
    }

    // ---------- BRP-008: duplicate GCash reference ----------

    [EmulatorFact]
    public async Task Brp008_DuplicateReference_RefusedBeforeAnythingIsWritten()
    {
        FirestoreDb db = await FreshAsync();
        await Driver(db, "a");
        (DateTime s, DateTime e) = Yesterday();
        await Shift(db, "a1", "a", s, e);
        await db.Collection(PaymentReferenceRules.Collection).Document("1234567890123")
            .SetAsync(new Dictionary<string, object> { ["paymentId"] = "earlier", ["driverId"] = "a" });

        var receipt = new PaymentReceipt([1, 2, 3], "image/jpeg", "1234 567-890123");
        RecordPaymentResult r = await Ledger(db).RecordPaymentAsync("a1", 800m, "EWallet", receipt);
        Assert.False(r.Ok);
        Assert.Equal(PaymentReferenceRules.DuplicateMessage, r.ErrorMessage);
        Assert.Empty(await PaymentsFor(db, "a1"));
    }

    // ---------- BRP-023: legacy + stamped ----------

    [EmulatorFact]
    public async Task Brp023_Web_Legacy500_Stamped300_Paid800()
    {
        FirestoreDb db = await FreshAsync();
        await Driver(db, "a");
        (DateTime s, DateTime e) = Yesterday();
        await Shift(db, "a1", "a", s, e);
        await Payment(db, "a1_PAY", "a1", "a", 500, txn: null);
        await Payment(db, "a1_PAY_20261025140000123", "a1", "a", 300, txn: null);
        FinancialLedgerService ledger = Ledger(db);
        Assert.Equal(0m, (await Row(ledger, "a")).TotalDebt);
        Assert.Equal(0m, (await Row(ledger, "a")).AdvanceCredit);
    }

    // ---------- BRP-024: active shift ----------

    [EmulatorFact]
    public async Task Brp024_Web_ActiveShift_NotDebt()
    {
        FirestoreDb db = await FreshAsync();
        await Driver(db, "a");
        await Shift(db, "a1", "a", DateTime.UtcNow.AddHours(-2), null, status: "Active");
        DebtLedgerRow row = await Row(Ledger(db), "a");
        Assert.Equal(0m, row.TotalDebt);
        Assert.Equal(0, row.UnpaidCount);
    }

    // ---------- BRP-025: flag set on day 3, cleared after payment ----------

    [EmulatorFact]
    public async Task Brp025_DebtFlag_Day2No_Day3Yes_ClearedAfterPayment()
    {
        FirestoreDb db = await FreshAsync();
        await Driver(db, "wed"); await Driver(db, "thu");
        DateTime twoAgo = Ph.Utc(Ph.Today.AddDays(-2).AddHours(6.5));
        DateTime threeAgo = Ph.Utc(Ph.Today.AddDays(-3).AddHours(6.5));
        await Shift(db, "w1", "wed", twoAgo, twoAgo.AddHours(14));
        await Payment(db, "w1_PAY_20261024140000000", "w1", "wed", 600, txn: "tw");
        await Shift(db, "t1", "thu", threeAgo, threeAgo.AddHours(14));
        await Payment(db, "t1_PAY_20261023140000000", "t1", "thu", 600, txn: "tt");
        FinancialLedgerService ledger = Ledger(db);

        await ledger.ProcessDebtFlagsAsync();
        Assert.False((await db.Collection("users").Document("wed").GetSnapshotAsync()).ContainsField("debtFlaggedSince"));
        Assert.True((await db.Collection("users").Document("thu").GetSnapshotAsync()).ContainsField("debtFlaggedSince"));
        DocumentSnapshot alert = await db.Collection("system_alerts").Document("t1_DEBT").GetSnapshotAsync();
        Assert.True(alert.Exists);
        Assert.Equal(FinancialLedgerService.UnpaidDebtAlertType, alert.GetValue<string>("type"));

        Assert.True((await ledger.SettleDebtAsync("thu", 200m, "Cash")).Ok);
        await Task.Delay(1500); // ProcessDebtFlagsAsync reads through the listener cache
        await Ledger(db).ProcessDebtFlagsAsync();
        Assert.False((await db.Collection("users").Document("thu").GetSnapshotAsync()).ContainsField("debtFlaggedSince"));
    }
}
