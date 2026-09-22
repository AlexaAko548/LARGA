# LARGA — Business Rules

This document captures domain rules that constrain how the system behaves, independent of any specific UI. These rules should be treated as authoritative when implementing or reviewing features, even if not every rule is fully enforced in code yet.

## 1. Account Provisioning

- **Drivers and staff cannot self-register.** There is no public sign-up flow for drivers.
- Accounts are **strictly provisioned by administrators/managers** via Firebase Authentication (and a corresponding `UserProfile` Firestore document).
- `UserProfile.Role` determines what a user can access:
  - Driver-role accounts are expected to carry license fields (`LicenseNumber`, `LicenseClassification`, `LicenseRestrictionCode`, `LicenseExpiryDate`) and an `AssignedTaxiId`.
  - Manager-role accounts legitimately leave driver-only fields null/empty.
- **Manager Web access control**: A Firebase Auth login alone is not sufficient. `LARGA.ManagerWeb` additionally checks that the authenticated user's Firestore `users/{uid}.role` equals `"Manager"` (case-insensitive) before granting dashboard access. Valid Firebase credentials without this profile/role do **not** grant web access.
- `LARGA.ManagerWeb` includes `Register.razor` / `RegisterPassword.razor` pages — these represent the **admin-provisioning flow** (a manager creating a new driver/staff account), not public self-registration.

## 2. Concurrency & Shift Lifecycle

- **Single-shift concurrency rule**: A driver may only have one active (`ONLINE`/in-progress) shift at any given time. A new shift cannot be started while a prior shift for that driver/taxi is still open.
- **Shift status model** (`ShiftLog.Status`) tracks the lifecycle of a shift, including an `IsOnBreak` flag for temporary pauses without ending the shift.
- **Mandatory pre-shift handover inspection**: A shift cannot transition to `ONLINE` until:
  1. A `HandoverChecklist` of type `PreShift` is completed, covering: `TireCondition`, `OilLevel`, `CoolantLevel`, `InteriorCleanliness`, `ExteriorScratches`, and `FuelVerification` (`HalfTank` / `BelowHalfTank`).
  2. The **starting odometer reading** (`ShiftLog.StartMileage`) has been captured — in the target flow this is read via on-device ML Kit OCR (`OdometerScanPage`) rather than free-text manual entry, to reduce fraud/typos.
- **End-of-shift mirrors the start**: An `EndShift` `HandoverChecklist` and `ShiftLog.EndMileage` are required to close out a shift cleanly (see `EndShiftStep1Page` / `EndShiftStep2Page`).
- A `ManagerNote` field on `ShiftLog` allows managers to annotate/flag a shift (e.g., after review) without altering the immutable operational data.

## 3. Contextual Tracking & Privacy Cutoff

- **GPS telemetry (`GpsTelemetry`) is tethered exclusively to active shifts.** A `GpsTelemetry` document always carries a `ShiftId`, anchoring every location ping to a specific, bounded work session — never to a driver's personal/off-duty time.
- **Tracking terminates immediately upon clock-out.** Once a shift transitions out of `ONLINE` (end-shift completed), location collection must stop. This is a deliberate privacy boundary: drivers are not tracked outside of active, paid shifts.
- This same anchoring pattern applies to other contextual/operational data generated during a shift (fuel reports, defect reports, emergency alerts) — see `database-schema.md` for the relational anchoring pattern.
- **Fuel reports are double-anchored**: each `FuelLog` carries both a `ShiftId` (the active shift during which the fuel was purchased) and a `DriverId` (the submitting driver), so reports can be queried either per-shift or per-driver (e.g., the driver mobile Reports page's "past fuel reports" list queries by `DriverId`).
- **Fuel submission requires both odometer and receipt OCR evidence**: the mobile `FuelReportViewModel` will not allow submission (`CanSubmit`) until an odometer scan and a receipt scan have both produced photo evidence, mirroring the anti-fraud intent of the pre-shift odometer capture rule above. OCR-uncertain fields (cost, quantity, station, date) are flagged and left editable for manual correction rather than silently trusted.
- Submitted fuel reports default to `VerificationStatus = Pending`; on submission failure (e.g., network/upload error), the mobile app preserves an unsent draft on-device rather than discarding the driver's input.

## 4. Boundary & Debt Computations

- **Daily taxi boundary rental**: Drivers pay a daily "boundary" (a fixed/negotiated rental fee for use of the taxi unit for that day), tracked per shift via `BoundaryPayment`.
  - `BoundaryPayment.PaymentMethod` supports `Cash` and `EWallet`.
  - `BoundaryPayment.PaymentStatus` supports `Unpaid`, `Partial`, and `Paid` — enabling partial boundary settlements.
- **Late penalty**: A standard late penalty is applied when boundary payment is late. The default value is configured system-wide via `SystemConfig.StandardLatePenalty` (business rule target value: **PHP 100** per late instance), rather than hardcoded, so managers can adjust it without a code change.
- **Default boundary rate**: `SystemConfig.DefaultBoundaryRate` holds the standard daily boundary amount used when a specific rate isn't otherwise negotiated per driver/taxi.
- **Rolling driver debt ledger**: When a driver underpays or misses a boundary payment, the shortfall accumulates as debt against that driver, tracked over time (not just per-shift).
  - `DebtAdjustment` provides **manual corrections** to a driver's owed balance (write-offs, bonus deductions, miscount fixes). It is deliberately modeled as its own document type rather than a synthetic `BoundaryPayment`, because it does not represent money physically handed over — folding it into `BoundaryPayment` would misrepresent it as a real collection event.
  - `DebtAdjustment.Amount` is signed: **positive** increases what the driver owes, **negative** is a credit/write-off.
  - The driver's **running balance** (Master Debt Ledger) is derived by aggregating `BoundaryPayment` shortfalls and `DebtAdjustment` entries per `DriverId` over time — this is exposed to drivers via the mobile `LedgerPage`/`DebtDetailPage` and to managers via the Manager Web Master Debt Ledger Tab.

## 5. Vehicle Maintenance Rules

- Maintenance work is categorized by `MaintenanceRecord.MaintenanceType`: `RoutineCheckup`, `BreakdownRepair`, or `AccidentCorrection` — distinguishing planned upkeep from reactive/incident-driven repairs.
- Each maintenance record carries a `PriorityLevel` (`Low`, `Medium`, `High`) to help managers triage the garage backlog.
- Parts consumed during maintenance are tracked via `MaintenancePartsUsed`, which is expected to decrement `SparePart.StockQuantity` against `SparePart.ReorderLevel` thresholds *(inventory deduction logic and UI are `[PENDING IMPLEMENTATION]` — see `features-and-requirements.md`)*.

## 6. Auditability

- Significant actions (e.g., manual debt adjustments, account provisioning, resolved emergency alerts) are expected to produce an `AuditLog` entry (`ActionType`, `AuditLogDetails`, `UserId`, `IpAddress`, `Timestamp`) forming an **immutable trail** for compliance/dispute resolution. The data model exists; the consuming UI/reporting surface is `[PENDING IMPLEMENTATION]`.
