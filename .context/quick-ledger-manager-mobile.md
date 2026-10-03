# Manager Quick Ledger (Mobile): Current State

Reference for the next session. Last updated 2026-10-03, branch `feature/mngr-quick-ledger`. Nothing from this work is committed yet.

Status and open items are in `quick-ledger-status.md`. This file describes how the feature works now.

---

## 1. What the page does

Tab: **Ledger** under `manager-dashboard` in `AppShell.xaml`. Page: `Views/Manager/ManagerLedgerPage.xaml`.

| Section | Contents |
|---|---|
| Header | "Quick Ledger", "N pending clearance(s)", **+ Other Payment** pill, amber warning line if any record couldn't be read |
| **PENDING** | One card per shift that started today with **nothing paid yet**. Shows "Balance due", "₱X paid of ₱Y", and the taxi. Tap a card to record a payment for it. Once any payment is recorded, the shift leaves Pending. |
| **PAYMENT DONE TODAY** | One row per payment document recorded today (`amountPaid > 0`). Shows driver, method, that payment's own amount, and the **shift's** status (Paid or Partial). |

The Other Payment list was removed on request. The **+ Other Payment** pill opens the Record Other Payment form.

### Record Payment / Record Other Payment (overlay on the same page)

Driven by `ViewModels/Manager/RecordPaymentViewModel.cs`. One form, two modes.

- **Boundary mode** (tap a pending card): "Record Payment". Expected Amount = the shift's remaining balance.
- **Other mode** (+ Other Payment): "Record Other Payment". Driver picker lists drivers with an amount owed. Category picker has one option, "Debt". Expected Amount = total owed by that driver. Has a **Notes (optional)** field, up to 500 characters.

Fields: Amount Received (PHP), Notes (Other mode only), CASH / E-WALLET (GCASH) toggle, SCAN RECEIPT (E-Wallet only), Receipt Details (Date and Reference No., read-only, shown as `---` until scanned), error line, CONFIRM PAYMENT.

Rules enforced in `ConfirmAsync`:
- Amount is a positive number with at most 2 decimals.
- E-Wallet needs a scanned receipt, and the receipt amount must equal Amount Received. A successful scan fills Amount Received with the receipt amount.
- Boundary: amount ≤ remaining balance for that shift.
- Other: amount ≤ total owed by that driver. Any allocation left over is refused.
- A save that fails the read-back check (section 3) shows the specific reason in the form and does not close it.

### Scan E-Receipt (`Views/Manager/ScanEReceiptPage.xaml(.cs)`)

Pushed modally from the form. Camera (`Camera.MAUI`) → Capture → `EReceiptOcrService.ReadAsync` → Amount, Date and Reference No. shown as full-width rows → Retake or CONFIRM. CONFIRM is enabled only when all three fields were read. It sends an `EReceiptScanResult` over `WeakReferenceMessenger` and closes.

---

## 2. Files

All under `LARGA.MobileApp/` unless noted.

| File | Role |
|---|---|
| `Services/QuickLedgerCalculator.cs` | Pure rules, no Firebase types. `Build` (Pending, Done Today, Other totals), `PlanBoundaryPayment`, `PlanDebtSettlement`, `TotalOwedBy`. Holds `ShiftPaymentState` and the input/output/write records. |
| `Services/QuickLedgerService.cs` | Firestore reads and writes. Returns `QuickLedgerSnapshot(Input, Result, Warnings)`. Save methods verify the write by reading it back. |
| `Services/EReceiptOcrService.cs` | All e-receipt OCR: `EReceiptData`, `EReceiptScanResult`, `EReceiptOcrService` (calls `IOcrService`, then a static `Parse`). |
| `ViewModels/Manager/ManagerLedgerViewModel.cs` | Page VM. Loads the snapshot, builds display items, owns `RecordPayment`. Queues reloads requested during a load. |
| `ViewModels/Manager/RecordPaymentViewModel.cs` | Form VM. Validation, scan handling, saves. |
| `Views/Manager/ManagerLedgerPage.xaml(.cs)` | Page and overlay. |
| `Views/Manager/ScanEReceiptPage.xaml(.cs)` | Scan page. |
| `Resources/Styles/LedgerStyles.xaml` | All `Ledger.*` styles, merged in `App.xaml`. |
| `Controls/LedgerEntry.cs`, `Controls/LedgerPicker.cs` | Subclasses whose Android underline is removed (section 6). |
| `MauiProgram.cs` | DI for `QuickLedgerService`, `EReceiptOcrService` (singletons), the VMs, `ManagerLedgerPage` and `ScanEReceiptPage` (transient). `#if ANDROID` handler mappings for the two controls. |
| `LARGA.SharedCore/Services/FinancialLedgerService.cs` | **Web** ledger service. Changed to read a shift's payments as a set (section 7). |

Removed: `Services/GcashReceiptParser.cs` (replaced by `EReceiptOcrService`).

---

## 3. Data model

### Core rule: one payment, one document

Each payment is its own `boundary_payments` document holding only its own amount.

- **Clock-out row:** `{shiftKey}_PAY`, created by the driver app. It starts at 0.
- **Each payment after that:** `{shiftKey}_PAY_{yyyyMMddHHmmssfff}` (UTC), with `_{index}` appended when two payments land in the same millisecond. The mobile app and the web use this same convention.
- **Documents are never updated to add a payment.** That was the bug: earlier versions added each payment to one shared document, which produced running totals that didn't match what was paid.

### Per-shift state (`ShiftPaymentState`, in the calculator)

One state per shift, built from all of the shift's documents:

- `TotalPaid`: the sum of `amountPaid` across the shift's documents.
- `Expected`: from the most recent document that has `expectedBoundary > 0`, plus `lateFees`. Otherwise the default rate.
- `IsCleared`: `Expected > 0 && TotalPaid >= Expected`. This decides Paid or Partial.
- `Target`: the most recent document. Used only as a source for `Expected`.
- `HasPaymentRecord`: whether the shift has any document.

Pending (shifts with `TotalPaid = 0`), Done Today status, Other Payment and settlement all use this state.

### Matching

A payment's `shiftId` can be the business ID (`SHIFT_yyyyMMdd_nnn`) or the Firestore document ID (clock-out writes often use this). Shifts are matched on either key, in the calculator and in the web service.

### Reads (`QuickLedgerService.LoadAsync`)

Full collection reads, the same approach as the web ledger. The fleet is small.

- `shifts`: `shiftId`, `driverId`, `taxiId`, `shiftStart`.
- `boundary_payments`: `shiftId`, `expectedBoundary`, `lateFees`, `amountPaid`, `paymentStatus`, `paymentMethod`, `timestamp`, `recordedAtUtc`.
- `debt_adjustments`: `driverId`, `amount` (signed).
- `users`: `fullName`, keyed by document ID.
- `system_configs/global`: `defaultBoundaryRate`. Falls back to ₱800 (`FallbackBoundaryRate`).

**Type rule:** Plugin.Firebase returns Firestore whole numbers as integers. Every plain value is therefore read as `object?` and converted (`ToDecimal`, `StringOf`, `ToUtc`), not as `double?`. A `double?` property fails on an integer and the whole document is dropped. Any document that still can't be read is listed in `Warnings`, and the page shows the list.

**Timestamps:** `timestamp` is written as a Plugin.Firebase `DateTime`. That write path wasn't reliable on Android, so each payment also writes `recordedAtUtc` as an ISO-8601 UTC string. Payment Done Today reads `recordedAtUtc` first and falls back to `timestamp`. The web ignores `recordedAtUtc`.

### Writes (`QuickLedgerService`)

**New payment document** (`SaveBoundaryPaymentAsync`): `SetDataAsync(dictionary, SetOptions.Merge())`. Fields below. The evidence fields are written only when present.

| Field | Type | Written when | Meaning |
|---|---|---|---|
| `shiftId` | string | always | the shift's business ID (or document ID if it has none) |
| `expectedBoundary` | number | always | expected amount for the shift |
| `lateFees` | number | always | 0 for app payments |
| `amountPaid` | number | always | **this payment only** |
| `paymentMethod` | string | always | `"Cash"` or `"E-Wallet"` |
| `paymentStatus` | string | always | `"Paid"` if the shift total after this payment ≥ expected, else `"Partial"` |
| `timestamp` | timestamp | always | when the payment was recorded (Plugin.Firebase DateTime write; see §3 Timestamps) |
| `recordedAtUtc` | string | always | the same time as ISO-8601 UTC text. Done Today reads this first. |
| `notes` | string | Other Payment with a note | the manager's note on what the payment is about |
| `gcashReferenceNumber` | string | E-Wallet | GCash reference number from the receipt |
| `receiptAmount` | number | E-Wallet | amount read from the receipt (must equal `amountPaid`) |
| `receiptDate` | string | E-Wallet | receipt date, `yyyy-MM-dd` |
| `ePayReceiptPhoto` | string | E-Wallet | download URL of the receipt photo in Firebase Storage. Same field name the web model already defines. |

The document ID is `{shiftKey}_PAY_{yyyyMMddHHmmssfff}`. Documents are never updated to add a payment.

- **Why not the tuple-array overload:** `SetDataAsync` with `(object, object)[]` stored no `amountPaid`, which the read-back check caught.
- **Why not `UpdateDataAsync` for new payments:** it's only used for updates, and the new design never updates a payment document.

**Read-back verification** (`VerifySavedAsync`): after each write, the document is read back. If it's missing, or its `amountPaid` differs from what was written, the save throws an `InvalidOperationException` with the reason. The form shows that message and stays open. This check is what identified the tuple-array bug.

**Debt settlement** (`SaveDebtSettlementAsync`): each allocation is a new payment document, the same as a payment from Pending, and each carries the same notes and receipt evidence. A leftover is written as a `debt_adjustments` credit with the note in `notes` (see section 5).

**Receipt photos (Firebase Storage):** an E-Wallet payment uploads the photo to `receipts/ewallet/{yyyyMMddHHmmssfff}_{label}.jpg` with `UploadReceiptPhotoAsync`. The label is the shift key for Pending payments and the driver ID for Other Payments. The download URL goes into `ePayReceiptPhoto`.

- The upload is an audit record, so it throws on failure. The payment is **not** saved without it, and the form shows the upload error.
- The upload runs after validation and before the save. A refused payment doesn't upload anything.
- Storage Security Rules must allow the signed-in manager to write `receipts/ewallet/`. The rules are in the Firebase console, not in the repo.

**Timeouts:** every read and write has a 20-second limit (`Timed`), so a hung call can't keep the page loading.

**Audit trail (`audit_logs`):** after each payment is saved, `WriteAuditAsync` adds one entry to `audit_logs`, the collection the web's inventory and fleet actions already use.

| Field | Value |
|---|---|
| `userId` | the signed-in manager's UID (`CrossFirebaseAuth.Current.CurrentUser`) |
| `actionType` | `QuickLedgerPaymentRecorded` (Pending payment) or `QuickLedgerDebtPaymentRecorded` (Other Payment) |
| `auditLogDetails` | plain-English summary: amount, method, driver, shift or number of shifts, transaction ID, shift status, any credit, the note, and "receipt photo stored" with the GCash reference for E-Wallet |
| `timestamp` | `FieldValue.ServerTimestamp()`, set by the server so the web's date-range queries work |
| `ipAddress` | not set |

- The entry is written after the payment is saved. If it fails, the payment still stands, so the form closes and the ledger page shows "Payment saved, but its audit entry could not be written". The form isn't kept open, because a retry would pay twice.
- Other audit actions (web settlements, manual adjustments) are not connected yet.

---

## 4. Business rules applied

- **Other Payment** = unpaid balances from shifts that started before today and have a payment record, plus net `debt_adjustments`, floored at zero. Shifts with no payment record are left out, matching the web's debt ledger.
- **Settlement order:** oldest shift first (by shift start), then any positive manual debt, via an automatic credit.
- **Pending** = today's shifts (Philippine time) with **no payment yet**. Shows the shift total.
- **A partial payment removes the shift from Pending.** Its status appears in Done Today as Partial. The remaining balance isn't listed on any screen today. On the next day the shift is an earlier shift, so its balance appears in Other Payment. This is a deliberate consequence, not a bug, but it's worth knowing when you check the totals.
- **Done Today** = each payment document with `amountPaid > 0` recorded today. Its amount is that payment's own amount.
- The business day is Philippine time (UTC+8), whatever the phone's zone.

---

## 5. `debt_adjustments`

Used for corrections only, per `.context/business-rules.md` §4. Two sources write to it:

1. **Automatic credit** from a lump-sum settlement, when the amount is more than the shift balances and the driver has positive manual debt. This is existing web behaviour. The mobile app also writes its note to `notes` on this record.
2. **Manual adjustments** from the web's "+ Adjustment" modal.

**Not used for cash received.** Recording a payment as an adjustment would show it as a write-off on the web and leave shifts looking unpaid. This was discussed and rejected.

---

## 6. Styling

### Font roles (matched to other manager pages)

| Role | Font | Used for |
|---|---|---|
| Page and sheet titles | BarlowCondensedBold | "Quick Ledger", "Record Payment", "SCAN E-RECEIPT" |
| CTA buttons | BarlowCondensedSemiBold | CONFIRM PAYMENT, + Other Payment |
| Eyebrows and field captions | Inter Bold, 11pt, tracked | PENDING, PAYMENT DONE TODAY, "Balance due", "Date" (`Ledger.SectionLabel`, `Ledger.Caption`, from `EyebrowLabelStyle`) |
| Names | Inter Bold | driver names |
| Body text | Inter Regular | subtitles, errors, warnings, taxi line |
| Money, statuses, reference numbers | DMMonoMedium | Balance due value, receipt values, Paid/Partial |
| Secondary and toggle buttons | DMMonoMedium | CASH, E-WALLET, SCAN RECEIPT, RETAKE |
| Entry | DMMonoMedium | Amount Received |
| Picker | Inter Bold | Other Payment driver and category |
| Icons | MaterialIcons | `&#xe853;` person, `&#xe531;` car, `&#xe8e5;` trending, `&#xe86c;` check, `&#xe5cd;` close, `&#xe5c4;` back |

### Surfaces

Follows the Figma: the palette is `LargaLightBlue` (#A6DCEE) for cards, rows and fields; `LargaPrimaryBlue` for the selected toggle, the Scan button, the + Other Payment pill and CONFIRM; white for the boxes inside cards (Expected) and for the sheet.

- Pending and Done Today cards and rows: `LargaLightBlue`, no border, 14px radius.
- Fields (Expected, Amount Received, Receipt Details): `LargaLightBlue`, no border.
- Inner "Expected" box on a pending card: white.
- Buttons are 38px tall (primary 40px). Toggle buttons are light blue; the selected toggle is primary blue with white text.
- Overlay: dim `#99000000`. The sheet is centred and sized to its content, inside a scroll view so small screens still reach the buttons.
- The warning line is collapsed when there is no warning (`HasWarning`), so an empty label doesn't leave a gap under the header.

**Plate numbers:** a pending card and the form show the taxi's `plateNumber` from the `taxis` collection, looked up by the shift's `taxiId`. A taxi with no plate set shows its ID.

### Underline removal

Android draws an underline on `Entry` and `Picker`. `LedgerEntry` and `LedgerPicker` are subclasses. `MauiProgram` appends handler mappings that set `PlatformView.Background = null` for those two types only. Fields are wrapped in a `Ledger.Field` border to supply the background colour.

Style keys are explicit. Global implicit styles don't apply to them, except `EyebrowLabelStyle`, which is reused through `BasedOn`.

---

## 7. Web side (`LARGA.SharedCore/Services/FinancialLedgerService.cs`)

The web ledger reads a shift's payments as a set, matching the mobile model.

- **Shift total** = sum of all the shift's `boundary_payments` documents. Matched by business ID or document ID. Expected comes from the newest document that has one, else the default rate.
- **Daily Settlements:** `AmountPaid` = shift total. Status from the total.
- **Master Debt Ledger:** a driver's debt = sum of `max(0, expected − total)` over the driver's shifts that have records, plus net adjustments, floored at zero. `UnpaidCount` counts uncleared shifts.
- **Record payment** (`RecordPaymentAsync`): writes a new `{shiftId}_PAY_{timestamp}` document. Previously it overwrote one document per shift.
- **Settle debt** (`SettleDebtAsync`): oldest unpaid shift first. Each allocation is a new payment document. Leftover goes to a `debt_adjustments` credit.
- **History** (`GetDriverHistoryAsync`): each payment document is its own row with its own amount. Each adjustment is a row. RunningDebt is replayed in time order: a shift adds its expected amount on its first record, each payment reduces it, and each adjustment moves it.
- Public method signatures are unchanged, so `FinancialLedger.razor` needed no edits.
- **Not built:** `LARGA.ManagerWeb` was not rebuilt, because the running web process locks its DLLs. `LARGA.SharedCore` builds.

### Known difference: settlement scope

- **Mobile Other Payment** settles earlier shifts only.
- **Web settlement** applies to every unpaid shift, including today's.

This is not resolved. Pick one before relying on both screens.

---

## 8. Receipt OCR (`EReceiptOcrService`)

Target: GCash "Sent via GCash" receipts. OCR returns text blocks in an order that doesn't match the layout, and right-hand values often come back separately from their labels.

- **Amount:** `\d{1,3}(,\d{3})*\.\d{2}` tokens, with the peso sign optional. Prefer a value on a line with AMOUNT, SENT or TOTAL, or on the line directly below one. Otherwise the value that appears most often, if at least twice.
- **Reference:** digits after "Ref No." on the **same line**. Only if that line has no digits, the next line. Matching is anchored and stops at the first non-digit, and it must not cross lines. Length 10–20 digits.
- **Date:** `MMM d, yyyy`, plausible (within 3 years back, up to tomorrow).

A scan is complete only when all three fields are found.

**Not tested** against real receipts beyond one screenshot. There's no test project on this branch.

---

## 9. Save and read verification in the app

- A payment saves only when the read-back check passes. A failed check names the document and what was read.
- Reads never drop a document silently. Unreadable records are listed as warnings on the page.
- Every read and write has a 20-second timeout.
- `Debug.WriteLine` covers rate fallbacks, load errors, and the warnings list.

---

## 10. Build and verify

```
dotnet build LARGA.MobileApp/LARGA.MobileApp.csproj -f net9.0-android
dotnet build LARGA.SharedCore/LARGA.SharedCore.csproj
```

Last result: both succeed. There is no unit test project on this branch. The mobile app now works on the device for recording a payment, which was confirmed by the user.

**Still to do:**
- Delete the old test records in the Firebase console (`SHIFT_20261003_614_PAY` at 0.00, the ₱344 and ₱744 rows, and similar). They're from the broken writes and distort the totals.
- Decide the settlement scope (section 7).
- Rebuild and test the web page after stopping the running ManagerWeb process.
- Add a test project for `QuickLedgerCalculator` and `EReceiptOcrService.Parse`.
- Receipt photos are not uploaded. Saving them to Storage and setting `ePayReceiptPhoto` is a separate step.
