# Manager Quick Ledger (Mobile): Status & Fix Notes

Snapshot as of 2026-10-03, branch `feature/manager-quick-ledger`. Last commit: `2aa909c`. A large uncommitted rewrite of the ledger sits on top of it.

## Intended behavior

- **Pending** lists the boundaries still due for *today* (Philippine time). There is one row per shift.
- Once a shift's boundary is fully paid, it moves to **Payment Done Today**.
- **Payment Done Today** lists every payment as its own entry. Payments from the same driver or shift are not combined.
- **Other Payment** lists the drivers who still owe unpaid boundary or debt from earlier shifts, with their names and the amounts owed.

## Files involved

| Layer | File |
|---|---|
| Page | `LARGA.MobileApp/Views/Manager/ManagerLedgerPage.xaml` |
| ViewModels | `LARGA.MobileApp/ViewModels/Manager/ManagerLedgerViewModel.cs`, `RecordPaymentViewModel.cs` |
| Row model | `LARGA.MobileApp/Models/LedgerItemModel.cs` |
| Service (orchestration) | `LARGA.MobileApp/Services/MobileFinancialLedgerService.cs` |
| Firestore reads (new, untracked) | `LARGA.MobileApp/Services/LedgerFirestore.cs` |
| Pure ledger logic (new, untracked) | `LARGA.MobileApp/Services/QuickLedgerCalculator.cs` |
| Debug log (new, untracked) | `LARGA.MobileApp/Services/LedgerLog.cs` |
| Web counterpart of the ledger logic (new, untracked) | `LARGA.SharedCore/Services/BoundaryPaymentRollup.cs` |
| Unit tests (new, untracked) | `LARGA.MobileApp.Tests/`: 54 tests, all passing as of 2026-10-03 |
| Writes the unpaid row at clock-out | `LARGA.SharedCore/Services/ShiftManagementService.cs` (`ClockOutAsync` → `EnsureBoundaryPaymentForShiftAsync`), `LARGA.MobileApp/ViewModels/Driver/ShiftCompletedViewModel.cs` (fallback) |

## Data conventions (important)

- **Two kinds of shift ID.** A shift document's `shiftId` field holds a business ID such as `SHIFT_yyyyMMdd_nnn`. The `boundary_payments` row created at clock-out usually stores the shift's **Firestore document ID** instead, because clock-out's read of the shift comes back empty and it falls back to the document ID. Payments must therefore be matched to shifts by either ID.
- **One payment, one row.** A shift has one `boundary_payments` row per payment, plus the unpaid row created at clock-out. A shift's status is worked out from all of its rows together. Each row stores:
  - `amountPaid`: this one payment only.
  - `paymentStatus`: where the whole shift stood after this payment.
- **Document IDs:**
  - Clock-out row: `{shiftKey}_PAY`.
  - Further payments: `{shiftKey}_PAY_{yyyyMMddHHmmssfff}`.
- **Plugin.Firebase can't read into a dictionary.** `GetDocumentsAsync<Dictionary<string, object>>()` returns documents with no usable fields. Read through classes whose properties carry `[FirestoreProperty]`, using `object?` for plain values and `DateTimeOffset?` for timestamps. Then flatten the result into a `LedgerDoc` (see `LedgerFirestore`).
- **Dates.** The business day is Philippine time (UTC+8), whatever time zone the phone is set to. Use `QuickLedgerCalculator.GetUtcBoundsForPhilippineDay`.

## Root causes in the committed code (`2aa909c`)

### Pending doesn't show
1. **A debug line was left in.** `GetPendingSettlementsAsync` ends with `throw new Exception("DIAG7 ...")`, so it always fails. The viewmodel's catch then clears **both** sections and shows a "Ledger Debug" alert.
2. **Reads come back empty.** Every read used `GetDocumentsAsync<Dictionary<string, object>>()`, so each row has no timestamp, no shift info and no driver.
3. **The day check can't pass.** Clock-out rows are named after the shift's random document ID (e.g. `zdIlk0bA9OIgWOg27WiC_PAY`), which contains no date. With no timestamp and no shift dates either, the today filter drops every pending row.

### Payment Done Today is collated
1. **Payments overwrite each other.** The committed `RecordPaymentAsync` loads the shift's `{shiftId}_PAY` document, adds the new amount to its `amountPaid`, and saves it back with `SetDataAsync`. A second payment replaces the first, so each shift ends up with a single combined row.
2. **Rows with no timestamp count as today.** The completed-today filter included them (`!timestampUtc.HasValue && !ShiftIdHasDatePrefix`). Because the reads come back empty, that let every row through.

## What the uncommitted rewrite changes

- **Reads.** `LedgerFirestore` reads through those field-mapped classes, with a 20-second timeout per read. `debt_adjustments` and `system_configs/global` fall back gracefully if they can't be read.
- **Pure logic.** All logic moved to `QuickLedgerCalculator`, which doesn't depend on Firebase and has unit tests:
  - `GroupByShift` / `ShiftLedger`: combine a shift's rows to get its total and status.
  - `BuildPendingRows`: one row per uncleared shift for today.
  - `BuildPaymentsDoneToday`: one entry per payment row received today, plus debt credits (negative `debt_adjustments`) recorded today.
  - `PlanPayment`: the first payment fills the unpaid clock-out row, and each later payment gets a new row.
  - `PlanDebtSettlement`: applies a lump sum to the oldest shift first. Any remainder then offsets manual-adjustment debt, and anything beyond that is reported back as unallocated.
- **ViewModel.**
  - Pending and Done Today load independently, so a failure in one doesn't blank the other.
  - Load errors appear on the page through `LoadErrorMessage`.
  - The realtime listeners on `shifts`, `boundary_payments` and `debt_adjustments` now use an empty `ChangeSignal` class instead of a dictionary.
- **Page and row model.**
  - Added an empty state for Payment Done Today.
  - `LedgerItemModel` gained `TransactionTimeUtc`, and `CompletedMetaText` shows "METHOD · Category · h:mm tt" in Manila time.
- **Web.** `FinancialLedgerService` was changed in the same way, through `BoundaryPaymentRollup`.

## Remaining issues (not yet fixed)

1. **Other Payment shows every driver when nobody owes anything.** In `GetDriversForOtherPaymentAsync`, if `debtDriverIds.Count == 0` the method returns all drivers. The "No drivers with pending debts" message therefore never appears. It should return an empty list.
2. **The Other Payment picker shows names only.** The spec also wants each driver's outstanding boundary or debt. `ComputeDebtByDriver` already calculates it, but the amount is thrown away.
3. **Older shifts can show up in today's Pending.** `BuildPendingRows` treats a shift as today's if any of its payments has a timestamp today. A partial debt payment today on an old shift brings that shift back into Pending. The today check should use the shift's own dates or ID only.
4. **A shift with no payment row never appears in Pending.** Pending is built from `boundary_payments` rows only, so a shift whose clock-out row failed to save is invisible. Consider building Pending from today's completed shifts and using the default rate when a shift has no row.
5. **Clock-out still reads into a dictionary.** `ShiftManagementService` reads the shift and `system_configs/global` with `Dictionary<string, object>`. The configured boundary rate is never seen, so it always uses ₱800. Matching still works, because rows fall back to the shift's document ID.
6. **Old combined rows stay combined.** `_PAY` documents written by the old code hold summed totals and cannot be split after the fact. They will keep showing as one entry each until they're cleaned up or reseeded.

## Not yet verified

- The rewrite has not been run on a device. Only the unit tests have been run, and they pass.
- Nothing in this rewrite has been committed yet.
