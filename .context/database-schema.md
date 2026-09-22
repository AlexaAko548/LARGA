# LARGA — Database Schema (Cloud Firestore)

LARGA uses **Cloud Firestore** as its sole database — a schemaless, document-oriented NoSQL store. "Schema" here refers to the conventions enforced by the C# entity models in `LARGA.Shared.Models/Entities`, mapped via `[FirestoreData]` / `[FirestoreProperty]` attributes and custom `IFirestoreConverter<T>` implementations (for enums, decimals, and lenient strings).

There are no foreign keys or joins at the database level — all relationships below are **application-enforced by convention** (matching string IDs across collections).

---

## Collections

### `users` ? `UserProfile`
Driver and manager accounts, provisioned by admins (see `business-rules.md`).

| Field | Type | Notes |
|---|---|---|
| `UserId` (doc ID) | string | Firebase Auth UID |
| `FullName`, `Email`, `PhoneNumber` | string | |
| `Role` | string | `"Manager"` (case-insensitive) gates Manager Web access; other values imply Driver |
| `AssignedTaxiId` | string | FK-by-convention ? `taxis` |
| `LicenseNumber`, `LicenseClassification`, `LicenseRestrictionCode`, `LicenseExpiryDate` | string/DateTime? | Driver-only; null for managers |

### `taxis` ? `TaxiUnit`
Vehicle fleet inventory.

| Field | Type | Notes |
|---|---|---|
| `DocumentId` (doc ID) | string | e.g. `TAXI_001` |
| `TaxiId` | string | Business identifier |
| `Model`, `YearManufactured` | string/int | |
| `CurrentMileage` | int | Updated from shift odometer readings |
| `Status` | string | e.g. active/in-maintenance/retired |
| `LastServicedDate` | DateTime? | |

### `shifts` ? `ShiftLog`
The central operational unit. Nearly every other collection anchors to a shift's document ID.

| Field | Type | Notes |
|---|---|---|
| `DocumentId` (doc ID) | string | Auto-generated Firestore ID — this is the canonical `shiftId` referenced elsewhere |
| `ShiftId` | string | Business/logical shift identifier |
| `DriverId` | string | FK-by-convention ? `users` |
| `TaxiId` | string | FK-by-convention ? `taxis` |
| `ShiftStart`, `ShiftEnd` | DateTime? | |
| `StartMileage`, `EndMileage` | int | Captured via ML Kit OCR odometer scan |
| `Status` | string | e.g. `ONLINE`, `OFFLINE`, `COMPLETED` |
| `IsOnBreak` | bool | |
| `ManagerNote` | string | Manager annotation |

Related sub-entities:
- `ShiftSchedule` — planned/rostered shifts.
- `HandoverChecklist` — pre-shift and end-shift vehicle inspection records; `ChecklistType` enum (`PreShift`/`EndShift`); linked via `ShiftId`. Includes `ScratchesPhotoUrl` / `FuelDashboardUrl` (Firebase Storage URLs) and `FuelVerification` enum (`HalfTank`/`BelowHalfTank`).

### `boundary_payments` ? `BoundaryPayment`
Daily boundary rental settlements.

| Field | Type | Notes |
|---|---|---|
| doc ID | string | |
| Linked to a `ShiftId` | string | Anchors payment to the shift it settles |
| `PaymentMethod` | enum | `Cash`, `EWallet` |
| `PaymentStatus` | enum | `Unpaid`, `Partial`, `Paid` |

Related: `DebtAdjustment` (separate collection/document type) — manual, signed corrections to a driver's running debt (`DriverId`, `Amount`, `Reason`, `Timestamp`). Kept distinct from `BoundaryPayment` because it does not represent an actual cash-collection event (see `business-rules.md` §4).

### `maintenance_logs` ? `MaintenanceRecord`
Vehicle maintenance/repair work orders.

| Field | Type | Notes |
|---|---|---|
| doc ID | string | |
| `MaintenanceType` | enum | `RoutineCheckup`, `BreakdownRepair`, `AccidentCorrection` |
| `PriorityLevel` | enum | `Low`, `Medium`, `High` |
| Linked to `TaxiId` | string | Which vehicle |

Related:
- `RoutineCheckItem` — individual checklist line items for routine checkups.
- `MaintenancePartsUsed` — join-style record linking a `MaintenanceRecord` to `SparePart` consumption (quantity used). *(Inventory deduction logic is `[PENDING IMPLEMENTATION]` — see `features-and-requirements.md`.)*
- `SparePart` — inventory catalog entry (`PartId`, `PartName`, `StockQuantity`, `ReorderLevel`, `UnitPrice`). *(No dedicated collection-management UI yet.)*

### `fuel_reports` ? `FuelLog`
Fuel purchase/refuel records, verified via OCR receipt scan.

| Field | Type | Notes |
|---|---|---|
| `FuelId` (doc ID) | string | |
| `ShiftId` | string | Anchors report to the active shift |
| `DriverId` | string | Anchors report to the submitting driver; used by the mobile Reports page to list a driver's own past fuel reports |
| `FuelStation`, `ORNumber` | string? | |
| `LitersRefueled`, `FuelCost` | decimal (via `DecimalConverter`) | |
| `ReceiptImageUrl` | string? | Firebase Storage URL |
| `OdometerPhotoUrl` | string? | Firebase Storage URL |
| `ReceiptTimestamp` | DateTime? | |
| `VerificationStatus` | enum | `Pending`, `Verified`, `Flagged` |

### `alerts` ? `EmergencyAlert`
SOS/emergency events.

| Field | Type | Notes |
|---|---|---|
| `AlertId` (doc ID) | string | |
| `ShiftId` | string | Anchors alert to the active shift during which it was raised |
| `Latitude`, `Longitude` | double | Location at time of alert |
| `IsResolved` | bool | Manager-side resolution flag |
| `Timestamp` | DateTime | |

> Note: the model exists and is consumed by the Manager Mobile `AlertCenterPage`, but the driver-side trigger mechanisms (panic button, shake detector, crash/G-force listener) that would *create* these documents are `[PENDING IMPLEMENTATION]`.

### `chats` ? `ChatMessage`
Driver ? Manager messaging.

| Field | Type | Notes |
|---|---|---|
| `MessageId` (doc ID) | string | |
| `Text` | string | |
| `IsDriver` | bool | Distinguishes message sender role |
| `Timestamp` | DateTime | |

### `system_configs` ? `SystemConfig`
Global, admin-tunable business parameters (singleton-style or small config set).

| Field | Type | Notes |
|---|---|---|
| `ConfigId` (doc ID) | string | |
| `StandardLatePenalty` | double | Default target value: PHP 100 |
| `DefaultBoundaryRate` | double | Standard daily boundary rate |

### GPS Telemetry (collection name not fully confirmed — likely `gps_telemetry` or nested under `shifts`) ? `GpsTelemetry`

| Field | Type | Notes |
|---|---|---|
| `LogId` (doc ID) | string | |
| `ShiftId` | string | Anchors every ping to the active shift (privacy cutoff rule) |
| `Latitude`, `Longitude` | double | |
| `Speed` | int | |
| `Timestamp` | DateTime | |

### Audit Trail (collection name not fully confirmed) ? `AuditLog`

| Field | Type | Notes |
|---|---|---|
| `AuditLogId` (doc ID) | string | |
| `UserId` | string | Who performed the action |
| `ActionType` | string | e.g. `"DebtAdjustment"`, `"AccountProvisioned"` |
| `AuditLogDetails` | string | Free-text/JSON description |
| `IpAddress` | string? | |
| `Timestamp` | DateTime | |

> `[PENDING IMPLEMENTATION]`: No service or Manager Web page currently reads/writes `AuditLog` — the model exists but is not yet wired into any workflow.

---

## Relational Anchoring Pattern

Because Firestore has no relational joins, LARGA's data model relies on a consistent **anchoring convention**:

1. **`shifts` is the anchor for nearly all operational/time-bound data.** `HandoverChecklist`, `GpsTelemetry`, `FuelLog`, `EmergencyAlert`, and (via the taxi/driver on the shift) `BoundaryPayment` all carry a `ShiftId` field that ties them back to a specific, bounded work session.
2. **`users` (driver UID) and `taxis` (taxi unit ID)** are the two other primary anchor points — referenced by ID from `shifts`, `maintenance_logs`, and `boundary_payments`/`DebtAdjustment`.
3. This means: to reconstruct "everything that happened during a driver's shift on a given day," an application query starts from the `shift` document ID and fans out to query `fuel_reports`, `alerts`, and telemetry collections filtered by that `shiftId` — rather than relying on a database-level join.
4. Longer-lived, cross-shift aggregates (like a driver's running debt balance) are derived by querying `boundary_payments` and `DebtAdjustment` filtered by `DriverId` across many shifts, not from a single shift anchor.

For a visual ERD, see `docs/ERD.md` in the repository root (referenced from `README.md`).
