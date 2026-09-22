# LARGA — Features & Requirements

This document describes LARGA's **target architecture** across its 8 core modules. It reflects both what is currently implemented in the codebase and what is planned. Items not yet found in the codebase are explicitly tagged **`[PENDING IMPLEMENTATION]`** — do not assume this code exists elsewhere or treat its absence as a bug; it is simply not built yet.

---

## 1. Driver & Shift Management ? *(implemented)*

**Platforms:** Driver Mobile, Manager Mobile, Manager Web

| Screen/Page | Platform | Notes |
|---|---|---|
| `DriverDashboardPage` | Driver Mobile | Landing screen after login; shift status/entry point |
| `PreShiftStep1Page` | Driver Mobile | Pre-shift handover checklist (tires, oil, coolant, interior, exterior) |
| `PreShiftStep2Page` | Driver Mobile | Continuation of pre-shift flow (fuel verification, photo capture) |
| `OdometerScanPage` | Driver Mobile | **Live ML Kit OCR** capture of starting/ending odometer reading |
| `ActiveShiftPage` | Driver Mobile | **Live timer/metrics** while a shift is `ONLINE` |
| `EndShiftStep1Page` | Driver Mobile | End-of-shift handover checklist |
| `EndShiftStep2Page` | Driver Mobile | Final end-shift confirmation, ending odometer |
| `ShiftCompletedPage` | Driver Mobile | Post-shift summary |
| `ManagerDashboardPage` | Manager Mobile | Manager's tactical mobile view |
| Live Roster & Shift Logs Audit Dashboard | Manager Web | `DriverShifts.razor` — real-time roster of active/past shifts |

**Backing services/models:** `ShiftManagementService`, `ShiftLog`, `ShiftSchedule`, `HandoverChecklist`.

---

## 2. Automated Boundary & Arrears Cashiering ? *(implemented)*

**Platforms:** Driver Mobile, Manager Web

| Screen/Page | Platform | Notes |
|---|---|---|
| `LedgerPage` | Driver Mobile | Active shift boundary due, running debt balance, payment history summary |
| `DebtDetailPage` | Driver Mobile | Drill-down into a driver's debt/adjustment breakdown |
| `PaymentHistory` | Driver Mobile | Historical boundary payments |
| Daily Settlements Tab | Manager Web | Part of `FinancialLedger.razor` — day's boundary collections |
| Master Debt Ledger Tab | Manager Web | Part of `FinancialLedger.razor` — rolling per-driver debt balances |

**Backing services/models:** `FinancialLedgerService`, `BoundaryPayment`, `DebtAdjustment`, `SystemConfig` (penalty/boundary rate defaults).

---

## 3. Real-Time GPS Fleet Monitoring & Contextual Tracking ?? *(partially implemented)*

**Platforms:** Driver Mobile (background service), Manager Mobile, Manager Web

| Component | Platform | Status |
|---|---|---|
| Background geolocation capture | Driver Mobile | `[PARTIAL]` — `GpsTelemetry` model and telemetry service scaffolding exist; a dedicated always-on background location service is not confirmed |
| Tactical Map (live fleet view) | Manager Mobile | `[PENDING IMPLEMENTATION]` — no map component found |
| Full-screen interactive map | Manager Web | `[PENDING IMPLEMENTATION]` — no MapLibre/MapTiler integration found in the codebase |

**Backing model:** `GpsTelemetry` (anchored to `ShiftId`, per the contextual-tracking privacy rule in `business-rules.md`).

> **Note:** MapLibre/MapTiler is named in the target architecture as the intended mapping stack, but **no map package references or view components currently exist** in either `LARGA.MobileApp` or `LARGA.ManagerWeb`. Treat all map-based UI as `[PENDING IMPLEMENTATION]`.

---

## 4. Fuel Monitoring & Verification ? *(implemented)*

**Platforms:** Driver Mobile, Manager Web

| Screen/Page | Platform | Notes |
|---|---|---|
| `FuelReportPage` | Driver Mobile | Fuel report submission form (station, liters, cost, receipt date); reached from `ReportsPage`'s Fuel tab via `AddFuelReportCommand` (`fuel-report-page` route) |
| `ScanFuelReceiptPage` | Driver Mobile | **ML Kit OCR** receipt scanner, populates `FuelLog` fields (cost, quantity, station, receipt date) with uncertain-field flags for manual correction |
| `OdometerScanPage` | Driver Mobile | Also reused by the fuel flow to capture the odometer reading alongside the receipt scan, with OCR digit-confusion normalization (e.g. `O`?`0`, `I`?`1`) |
| `ReportsPage` (Fuel tab) | Driver Mobile | Lists the driver's past fuel reports (`FuelReports` collection bound via `BindableLayout`) and an "Add Fuel Report" entry point |
| Fuel audit tables | Manager Web | `[PARTIAL]` — no dedicated fuel-audit page confirmed under `Components/Pages`; likely folded into `Dashboard`/`FinancialLedger` or `[PENDING IMPLEMENTATION]` as a standalone page |

**Backing services/models:** `FuelReportViewModel`, `ReportsViewModel` (fuel tab + `FuelReportItem`/`FuelReportProxy`), `FuelLog` (with `DriverId` and `VerificationStatus`: `Pending`/`Verified`/`Flagged`), `IOcrService`.

---

## 5. Vehicle Maintenance Management ?? *(partially implemented)*

**Platforms:** Driver Mobile, Manager Web

| Screen/Page | Platform | Notes |
|---|---|---|
| `VehicleDefectPage` | Driver Mobile | Defect reporting form with photo capture |
| `DefectReportDetailPage` | Driver Mobile | Detail view of a submitted defect report |
| `Garage.razor` | Manager Web | Garage Dashboard — exists |
| Maintenance scheduler | Manager Web | `[PENDING IMPLEMENTATION]` — not confirmed as a distinct sub-view within `Garage.razor` |
| Unit history | Manager Web | `[PENDING IMPLEMENTATION]` — not confirmed as a distinct sub-view within `Garage.razor` |

**Backing services/models:** `MaintenanceService`, `GarageService`, `MaintenanceRecord`, `RoutineCheckItem`, `MaintenancePartsUsed`.

---

## 6. Spare Parts Inventory Management ? `[PENDING IMPLEMENTATION]`

**Platforms:** Manager Web

| Screen/Page | Status |
|---|---|
| Inventory stock list | `[PENDING IMPLEMENTATION]` |
| Parts catalog | `[PENDING IMPLEMENTATION]` |
| Usage logs | `[PENDING IMPLEMENTATION]` |

- The `SparePart` entity (`PartId`, `PartName`, `StockQuantity`, `ReorderLevel`, `UnitPrice`) exists in `LARGA.Shared.Models`, and `MaintenancePartsUsed` links parts consumption to maintenance records.
- **No corresponding Manager Web page, component, or dedicated service method for browsing/managing inventory was found in the codebase.** Do not assume an inventory UI exists — this module is data-model-only today.

---

## 7. Mobile SOS Emergency Signaling ?? *(partially implemented)*

**Platforms:** Driver Mobile, Manager Mobile, Manager Web

| Screen/Page | Platform | Status |
|---|---|---|
| Driver panic trigger | Driver Mobile | `[PENDING IMPLEMENTATION]` — no explicit panic-button trigger UI found |
| Shake-to-SOS background listener | Driver Mobile | `[PENDING IMPLEMENTATION]` — no accelerometer/shake-sensor code found |
| Crash Protocol (G-force listener) | Driver Mobile | `[PENDING IMPLEMENTATION]` — no accelerometer/G-force crash-detection code found |
| Alert Center | Manager Mobile | ? Implemented — `AlertCenterPage` / `AlertCenterViewModel` |
| Dispatch panel | Manager Web | `[PENDING IMPLEMENTATION]` — no dedicated alert dispatch page found under `Components/Pages` |

**Backing model:** `EmergencyAlert` (`ShiftId`, `Latitude`, `Longitude`, `IsResolved`, `Timestamp`) — exists and is consumed by the Manager Mobile `AlertCenterPage`, but there is currently no confirmed driver-side code path that *creates* an `EmergencyAlert` (no panic button, shake detector, or crash listener implementation found).

---

## 8. Analytics, Reporting & Audit Logs ? `[PENDING IMPLEMENTATION]`

**Platforms:** Manager Web

| Screen/Page | Status |
|---|---|
| KPI charts | `[PARTIAL]` — `SimpleBarChart.razor` shared component exists, but no dedicated analytics page consuming it for KPIs was found |
| Revenue summaries | `[PENDING IMPLEMENTATION]` |
| Immutable audit trails (viewer) | `[PENDING IMPLEMENTATION]` — `AuditLog` entity exists in `LARGA.Shared.Models`, but no page/service surfaces it |

**Backing model:** `AuditLog` (`UserId`, `ActionType`, `AuditLogDetails`, `IpAddress`, `Timestamp`).

---

## Summary Status Table

| # | Module | Status |
|---|---|---|
| 1 | Driver & Shift Management | ? Implemented |
| 2 | Boundary & Arrears Cashiering | ? Implemented |
| 3 | GPS Fleet Monitoring & Tracking | ?? Partial (model only; map UI pending) |
| 4 | Fuel Monitoring & Verification | ? Mobile implemented / ?? web audit tables unconfirmed |
| 5 | Vehicle Maintenance Management | ?? Partial (mobile done; web scheduler/history pending) |
| 6 | Spare Parts Inventory Management | ? Pending (model only) |
| 7 | Mobile SOS Emergency Signaling | ?? Partial (viewer done; trigger/detection pending) |
| 8 | Analytics, Reporting & Audit Logs | ? Pending (model only) |
