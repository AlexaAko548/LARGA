using LARGA.SharedCore.Ledger;

namespace LARGA.Tests.Ledger;

/// <summary>The driver's Ledger tab badge: which ledger changes count as new until the driver opens the Ledger.</summary>
public class LedgerUpdatesTests
{
    private static readonly DateTime T0 = new(2026, 11, 5, 6, 0, 0, DateTimeKind.Utc);

    private static (string, string?, decimal, DateTime?) Payment(string id, string? txn, decimal amount, DateTime? at) => (id, txn, amount, at);

    [Fact]
    public void OneHandoverPaidSeveralShifts_IsOneUpdate()
    {
        var updates = LedgerUpdates.FromPayments(new[]
        {
            Payment("p1", "txn-1", 800, T0),
            Payment("p2", "txn-1", 150, T0.AddMilliseconds(5)),
            Payment("p3", "txn-2", 100, T0.AddMinutes(30)),
        });

        Assert.Equal(2, updates.Count);
        Assert.Equal(T0.AddMilliseconds(5), updates.Single(u => u.Key == "txn:txn-1").AtUtc); // the handover's latest document
    }

    [Fact]
    public void PaymentWithoutTransactionId_CountsPerDocument()
    {
        var updates = LedgerUpdates.FromPayments(new[] { Payment("a", null, 800, T0), Payment("b", "", 800, T0.AddDays(1)) });
        Assert.Equal(2, updates.Count);
    }

    [Fact]
    public void ZeroAmountAndUndatedPayments_AreIgnored()
    {
        var updates = LedgerUpdates.FromPayments(new[] { Payment("a", "t", 0, T0), Payment("b", "t2", 500, null) });
        Assert.Empty(updates);
    }

    [Fact]
    public void AutomaticCreditsBelongToTheirPayment_ManualAdjustmentsAreOwnUpdates()
    {
        var updates = LedgerUpdates.FromAdjustments(new (string, string?, DateTime?)[]
        {
            ("auto", "txn-1", T0),          // created by a payment
            ("manual-penalty", null, T0.AddHours(1)),
            ("manual-credit", "", T0.AddHours(2)),
            ("undated", null, null),
        });

        Assert.Equal(new[] { "adj:manual-penalty", "adj:manual-credit" }, updates.Select(u => u.Key));
    }

    [Fact]
    public void Unseen_AreOnlyTheOnesNewerThanSeenUpTo()
    {
        var updates = LedgerUpdates.FromPayments(new[] { Payment("a", "t1", 800, T0), Payment("b", "t2", 800, T0.AddHours(1)), Payment("c", "t3", 800, T0.AddHours(2)) });

        Assert.Equal(2, LedgerUpdates.CountUnseen(updates, T0));
        Assert.Equal(0, LedgerUpdates.CountUnseen(updates, T0.AddHours(2))); // opening the Ledger marks everything up to the newest as seen
        Assert.Equal(3, LedgerUpdates.CountUnseen(updates, DateTime.MinValue));
    }

    [Fact]
    public void NothingMarkedSeenYet_ShowsNoBadgeForOldHistory()
    {
        var updates = LedgerUpdates.FromPayments(new[] { Payment("a", "t1", 800, T0) });

        Assert.Equal(0, LedgerUpdates.CountUnseen(updates, null));
        // ...and the baseline stored then makes only later updates new.
        DateTime baseline = LedgerUpdates.Baseline(updates);
        Assert.Equal(T0, baseline);
        Assert.Equal(1, LedgerUpdates.CountUnseen(updates.Append(new LedgerUpdates.Update("txn:t2", T0.AddMinutes(5))), baseline));
    }

    [Fact]
    public void NoHistoryAtAll_BaselineIsEarliest_SoTheFirstUpdateEverIsNew()
    {
        Assert.Equal(DateTime.MinValue, LedgerUpdates.Baseline(Array.Empty<LedgerUpdates.Update>()));
        Assert.Equal(1, LedgerUpdates.CountUnseen(new[] { new LedgerUpdates.Update("adj:x", T0) }, DateTime.MinValue));
    }

    [Fact]
    public void DebtAdjustmentRecord_ManualVersusAutomatic()
    {
        var manual = new DebtAdjustmentRecord("d1", 200m, T0, "Late return on Oct 12");
        var automatic = new DebtAdjustmentRecord("d1", -200m, T0, "Automatic credit from a debt payment.", "txn-1");

        Assert.True(manual.IsManual);
        Assert.False(automatic.IsManual);
        Assert.Equal("Late return on Oct 12", manual.Reason);
    }
}
