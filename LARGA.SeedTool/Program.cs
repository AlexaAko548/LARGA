using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Google.Apis.Auth.OAuth2;
using Google.Cloud.Firestore;
using Google.Cloud.Firestore.V1;
using LARGA.Shared.Models.Entities;

namespace LARGA.SeedTool;

/// <summary>
/// One-off / re-runnable console tool that seeds the LARGA Firestore project with a
/// small, internally-consistent set of test data across every collection the
/// ManagerWeb dashboard (and near-future pages: Garage, Inventory, Fuel Verification,
/// Audit Logs) will need to render something meaningful.
///
/// All documents use fixed IDs, so re-running this tool overwrites the same records
/// instead of creating duplicates - safe to run again before a demo to reset state.
///
/// Usage:
///   dotnet run --project LARGA.SeedTool -- <path-to-service-account-key.json> [projectId]
///
/// If no key path is given, falls back to Application Default Credentials
/// (GOOGLE_APPLICATION_CREDENTIALS env var or `gcloud auth application-default login`).
/// Default project id is "larga-blmtaxi" (from firebase-auth.js).
/// </summary>
internal static class Program
{
    private const string DefaultProjectId = "larga-blmtaxi";

    // Fixed, meaningful IDs so reruns are idempotent and console records are easy to read.
    // Note: "WD2nFT8xngMPNYQb0xEeIyPAenl1" (the real Firebase Auth manager) is intentionally
    // never referenced below - this tool does not touch that document.
    private const string SampleManagerUid = "SAMPLE_MANAGER_UID"; // referenced by an existing maintenance_logs doc but never created - fixed here
    private const string DriverJuan = "SAMPLE_USER_UID";                 // existing placeholder, already referenced by shifts/maintenance_logs - upgraded to a full profile
    private const string DriverMaria = "SAMPLE_DRIVER2_UID";
    private const string DriverPedro = "SAMPLE_DRIVER3_UID";
    private const string DriverAna = "SAMPLE_DRIVER4_UID";
    private const string DriverCarlos = "SAMPLE_DRIVER5_UID";

    private const string Taxi1 = "TAXI_001";
    private const string Taxi2 = "TAXI_002";
    private const string Taxi3 = "TAXI_003";
    private const string Taxi4 = "TAXI_004";
    private const string Taxi5 = "TAXI_005";

    private static async Task<int> Main(string[] args)
    {
        string? keyPath = args.Length > 0 ? args[0] : null;
        string projectId = args.Length > 1 ? args[1] : DefaultProjectId;

        Console.WriteLine("LARGA Firestore Seed Tool");
        Console.WriteLine("=========================");
        Console.WriteLine($"Target project : {projectId}");
        Console.WriteLine($"Credentials    : {(keyPath is null ? "Application Default Credentials" : keyPath)}");
        Console.WriteLine();
        Console.WriteLine("This will write/overwrite fixed-ID documents in: users, taxis, shifts,");
        Console.WriteLine("boundary_payments, fuel_logs, maintenance_logs, maintenance_parts_used,");
        Console.WriteLine("spare_parts, emergency_alerts, gps_telemetry, audit_logs,");
        Console.WriteLine("handover_checklists, shift_schedules, system_configs.");
        Console.WriteLine("It also corrects two known bad fields found in your existing data:");
        Console.WriteLine("  - maintenance_logs: 'supportingPhotoURL' -> 'supportingPhotoUrl' (casing)");
        Console.WriteLine("  - boundary_payments: amountPaid \"600\" (string) -> 600 (number)");
        Console.WriteLine();
        Console.WriteLine("3 shifts are seeded as currently 'Active' so the dashboard's live fleet-status");
        Console.WriteLine("pills each have one dedicated example taxi: TAXI_001=Active (moving GPS),");
        Console.WriteLine("TAXI_002=On Break, TAXI_004=SOS (unresolved alert). TAXI_003=Maintenance and");
        Console.WriteLine("TAXI_005=Idle (no active shift) round out all 5 states.");
        Console.WriteLine();
        Console.Write("Type YES to continue: ");
        if (Console.ReadLine()?.Trim() != "YES")
        {
            Console.WriteLine("Aborted.");
            return 1;
        }

        FirestoreDb db;
        if (!string.IsNullOrWhiteSpace(keyPath))
        {
            if (!File.Exists(keyPath))
            {
                Console.WriteLine($"Service account key not found at: {keyPath}");
                return 1;
            }

            // GoogleCredential.FromFile is obsolete in favor of CredentialFactory, but for a
            // local seeding tool reading an explicit, developer-supplied key path this is fine.
#pragma warning disable CS0618
            GoogleCredential credential = GoogleCredential.FromFile(keyPath);
#pragma warning restore CS0618
            FirestoreClient client = new FirestoreClientBuilder { GoogleCredential = credential }.Build();
            db = FirestoreDb.Create(projectId, client);
        }
        else
        {
            db = await FirestoreDb.CreateAsync(projectId);
        }

        await SeedUsersAsync(db);
        await SeedTaxisAsync(db);
        await SeedShiftsAsync(db);
        await SeedBoundaryPaymentsAsync(db);
        await SeedFuelLogsAsync(db);
        await SeedMaintenanceAsync(db);
        await SeedSparePartsAsync(db);
        await SeedEmergencyAlertsAsync(db);
        await SeedGpsTelemetryAsync(db);
        await SeedAuditLogsAsync(db);
        await SeedHandoverChecklistsAsync(db);
        await SeedShiftSchedulesAsync(db);
        await SeedSystemConfigAsync(db);

        Console.WriteLine();
        Console.WriteLine("Done.");
        return 0;
    }

    private static async Task SetAsync<T>(FirestoreDb db, string collection, string docId, T entity) where T : class
    {
        await db.Collection(collection).Document(docId).SetAsync(entity, SetOptions.Overwrite);
        Console.WriteLine($"  wrote {collection}/{docId}");
    }

    // ---------------------------------------------------------------------
    // users
    // ---------------------------------------------------------------------
    private static async Task SeedUsersAsync(FirestoreDb db)
    {
        Console.WriteLine("Seeding users...");

        await SetAsync(db, "users", SampleManagerUid, new UserProfile
        {
            FullName = "Antonio G. Medina",
            Email = "antonio.medina@larga-blmtaxi.com",
            PhoneNumber = "09171234567",
            Role = "Manager",
            Address = "22 Kalayaan Ave, Quezon City",
            DateJoined = new DateTime(2022, 1, 10, 0, 0, 0, DateTimeKind.Utc),
        });

        await SetAsync(db, "users", DriverJuan, new UserProfile
        {
            FullName = "Juan Dela Cruz",
            Email = "juan.delacruz@larga-blmtaxi.com",
            PhoneNumber = "09171112222",
            Role = "Driver",
            AssignedTaxiId = Taxi1,
            LicenseNumber = "N01-23-456789",
            LicenseClassification = "Professional",
            LicenseRestrictionCode = "1",
            LicenseExpiryDate = new DateTime(2028, 3, 15, 0, 0, 0, DateTimeKind.Utc),
            CurrentArrears = 0,
            PerformanceScore = 92,
            Address = "14 Rizal St, Quezon City",
            DateJoined = new DateTime(2022, 1, 15, 0, 0, 0, DateTimeKind.Utc),
            ManagerNote = "Please double check the AC vents before your shift - a rider complained last week.",
        });

        await SetAsync(db, "users", DriverMaria, new UserProfile
        {
            FullName = "Maria Santos",
            Email = "maria.santos@larga-blmtaxi.com",
            PhoneNumber = "09173334444",
            Role = "Driver",
            AssignedTaxiId = Taxi2,
            LicenseNumber = "N02-34-567890",
            LicenseClassification = "Professional",
            LicenseRestrictionCode = "1",
            // Within 30 days of the seed-tool's "today" - demonstrates the Expiring status.
            LicenseExpiryDate = DateTime.UtcNow.Date.AddDays(18),
            CurrentArrears = 0,
            PerformanceScore = 97,
            Address = "8 Mabini St, Pasig City",
            DateJoined = new DateTime(2023, 4, 2, 0, 0, 0, DateTimeKind.Utc),
        });

        await SetAsync(db, "users", DriverPedro, new UserProfile
        {
            FullName = "Pedro Reyes",
            Email = "pedro.reyes@larga-blmtaxi.com",
            PhoneNumber = "09175556666",
            Role = "Driver",
            AssignedTaxiId = Taxi3,
            LicenseNumber = "N03-45-678901",
            LicenseClassification = "Professional",
            LicenseRestrictionCode = "1",
            // Already in the past - demonstrates the Expired status.
            LicenseExpiryDate = DateTime.UtcNow.Date.AddDays(-14),
            CurrentArrears = 0,
            PerformanceScore = 88,
            Address = "101 Aguinaldo Hwy, Bacoor, Cavite",
            DateJoined = new DateTime(2022, 6, 20, 0, 0, 0, DateTimeKind.Utc),
        });

        await SetAsync(db, "users", DriverAna, new UserProfile
        {
            FullName = "Ana Lim",
            Email = "ana.lim@larga-blmtaxi.com",
            PhoneNumber = "09177778888",
            Role = "Driver",
            AssignedTaxiId = Taxi4,
            LicenseNumber = "N04-56-789012",
            LicenseClassification = "Professional",
            LicenseRestrictionCode = "1",
            LicenseExpiryDate = new DateTime(2026, 12, 10, 0, 0, 0, DateTimeKind.Utc),
            CurrentArrears = 900, // 400 unpaid from Sep 2 shift + 500 unpaid from Aug 27 shift
            PerformanceScore = 61,
            Address = "5 Bonifacio St, Makati City",
            DateJoined = new DateTime(2024, 2, 14, 0, 0, 0, DateTimeKind.Utc),
        });

        await SetAsync(db, "users", DriverCarlos, new UserProfile
        {
            FullName = "Carlos Bautista",
            Email = "carlos.bautista@larga-blmtaxi.com",
            PhoneNumber = "09179990000",
            Role = "Driver",
            AssignedTaxiId = Taxi5,
            LicenseNumber = "N05-67-890123",
            LicenseClassification = "Non-Professional",
            LicenseRestrictionCode = "2",
            LicenseExpiryDate = new DateTime(2029, 1, 5, 0, 0, 0, DateTimeKind.Utc),
            CurrentArrears = 0,
            PerformanceScore = 0, // no shifts yet - brand-new driver, good empty-state test case
            Address = "33 EDSA, Mandaluyong City",
            DateJoined = DateTime.UtcNow.Date.AddDays(-3), // brand-new hire
        });
    }

    // ---------------------------------------------------------------------
    // taxis
    // ---------------------------------------------------------------------
    private static async Task SeedTaxisAsync(FirestoreDb db)
    {
        Console.WriteLine("Seeding taxis...");

        // TAXI_001 already has good data (Toyota Vios, Active Unit) - bring its mileage
        // forward to match the new shift history added below and add the plate number.
        await SetAsync(db, "taxis", Taxi1, new TaxiUnit
        {
            TaxiId = Taxi1,
            Model = "Toyota Vios",
            PlateNumber = "GHK-4471-MP",
            YearManufactured = 2018,
            CurrentMileage = 187780,
            Status = "Active Unit",
            LastServicedDate = new DateTime(2026, 8, 20, 0, 0, 0, DateTimeKind.Utc),
        });

        await SetAsync(db, "taxis", Taxi2, new TaxiUnit
        {
            TaxiId = Taxi2,
            Model = "Toyota Vios",
            PlateNumber = "NGP-2210-JL",
            YearManufactured = 2019,
            CurrentMileage = 95475,
            Status = "Active Unit",
            LastServicedDate = new DateTime(2026, 8, 10, 0, 0, 0, DateTimeKind.Utc),
        });

        await SetAsync(db, "taxis", Taxi3, new TaxiUnit
        {
            TaxiId = Taxi3,
            Model = "Toyota Vios",
            PlateNumber = "TVR-5583-QC",
            YearManufactured = 2020,
            CurrentMileage = 120710,
            Status = "Under Maintenance",
            LastServicedDate = new DateTime(2026, 6, 2, 0, 0, 0, DateTimeKind.Utc),
        });

        await SetAsync(db, "taxis", Taxi4, new TaxiUnit
        {
            TaxiId = Taxi4,
            Model = "Toyota Wigo",
            PlateNumber = "WGO-8841-MN",
            YearManufactured = 2017,
            CurrentMileage = 152410,
            Status = "Active Unit",
            LastServicedDate = new DateTime(2026, 7, 15, 0, 0, 0, DateTimeKind.Utc),
        });

        await SetAsync(db, "taxis", Taxi5, new TaxiUnit
        {
            TaxiId = Taxi5,
            Model = "Toyota Vios",
            PlateNumber = "NEW-0192-BL",
            YearManufactured = 2021,
            CurrentMileage = 500,
            Status = "On Standby",
            LastServicedDate = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc),
        });
    }

    // ---------------------------------------------------------------------
    // shifts
    // ---------------------------------------------------------------------
    // Shift IDs double as document IDs for idempotent reruns.
    public const string ShiftJuanOverdueExisting = "SHIFT_20250612_001"; // pre-existing document - left as-is except noted elsewhere
    public const string ShiftJuanAug31 = "SHIFT_2026083101";
    public const string ShiftJuanActiveToday = "SHIFT_2026090501";
    public const string ShiftMariaAug29 = "SHIFT_2026082901";
    public const string ShiftMariaSep4 = "SHIFT_2026090401";
    public const string ShiftMariaActiveOnBreak = "SHIFT_2026090502"; // demonstrates the "On Break" fleet status
    public const string ShiftPedroSep3 = "SHIFT_2026090301";
    public const string ShiftAnaAug27 = "SHIFT_2026082701";
    public const string ShiftAnaSep2Late = "SHIFT_2026090201";
    public const string ShiftAnaActiveSos = "SHIFT_2026090503"; // demonstrates the "SOS" fleet status

    private static async Task SeedShiftsAsync(FirestoreDb db)
    {
        Console.WriteLine("Seeding shifts...");

        await SetAsync(db, "shifts", ShiftJuanAug31, new ShiftLog
        {
            ShiftId = ShiftJuanAug31,
            DriverId = DriverJuan,
            TaxiId = Taxi1,
            ShiftStart = new DateTime(2026, 8, 31, 6, 0, 0, DateTimeKind.Utc),
            ShiftEnd = new DateTime(2026, 8, 31, 18, 5, 0, DateTimeKind.Utc),
            StartMileage = 187604,
            EndMileage = 187780,
            Status = "Completed",
            ManagerNote = string.Empty,
        });

        await SetAsync(db, "shifts", ShiftJuanActiveToday, new ShiftLog
        {
            ShiftId = ShiftJuanActiveToday,
            DriverId = DriverJuan,
            TaxiId = Taxi1,
            ShiftStart = DateTime.UtcNow.Date.AddHours(6),
            ShiftEnd = null,
            StartMileage = 187780,
            EndMileage = 0,
            Status = "Active",
            IsOnBreak = false, // has moving GPS telemetry seeded below - demonstrates "Active"
            ManagerNote = string.Empty,
        });

        // A second currently-active shift, on break - demonstrates the "On Break" fleet status
        // (TAXI_001 above covers "Active"; TAXI_003 is "Maintenance"; this + the SOS shift below
        // give every one of the 5 live fleet-status pills a dedicated, unambiguous example taxi).
        await SetAsync(db, "shifts", ShiftMariaActiveOnBreak, new ShiftLog
        {
            ShiftId = ShiftMariaActiveOnBreak,
            DriverId = DriverMaria,
            TaxiId = Taxi2,
            ShiftStart = DateTime.UtcNow.Date.AddHours(6),
            ShiftEnd = null,
            StartMileage = 95475,
            EndMileage = 0,
            Status = "Active",
            IsOnBreak = true,
            ManagerNote = string.Empty,
        });

        await SetAsync(db, "shifts", ShiftMariaAug29, new ShiftLog
        {
            ShiftId = ShiftMariaAug29,
            DriverId = DriverMaria,
            TaxiId = Taxi2,
            ShiftStart = new DateTime(2026, 8, 29, 6, 0, 0, DateTimeKind.Utc),
            ShiftEnd = new DateTime(2026, 8, 29, 18, 0, 0, DateTimeKind.Utc),
            StartMileage = 95000,
            EndMileage = 95230,
            Status = "Completed",
            ManagerNote = string.Empty,
        });

        await SetAsync(db, "shifts", ShiftMariaSep4, new ShiftLog
        {
            ShiftId = ShiftMariaSep4,
            DriverId = DriverMaria,
            TaxiId = Taxi2,
            ShiftStart = new DateTime(2026, 9, 4, 6, 0, 0, DateTimeKind.Utc),
            ShiftEnd = new DateTime(2026, 9, 4, 18, 0, 0, DateTimeKind.Utc),
            StartMileage = 95230,
            EndMileage = 95475,
            Status = "Completed",
            ManagerNote = string.Empty,
        });

        await SetAsync(db, "shifts", ShiftPedroSep3, new ShiftLog
        {
            ShiftId = ShiftPedroSep3,
            DriverId = DriverPedro,
            TaxiId = Taxi3,
            ShiftStart = new DateTime(2026, 9, 3, 6, 0, 0, DateTimeKind.Utc),
            ShiftEnd = new DateTime(2026, 9, 3, 18, 0, 0, DateTimeKind.Utc),
            StartMileage = 120500,
            EndMileage = 120710,
            Status = "Completed",
            ManagerNote = "Driver reported engine noise at end of shift; unit pulled for inspection.",
        });

        await SetAsync(db, "shifts", ShiftAnaAug27, new ShiftLog
        {
            ShiftId = ShiftAnaAug27,
            DriverId = DriverAna,
            TaxiId = Taxi4,
            ShiftStart = new DateTime(2026, 8, 27, 6, 0, 0, DateTimeKind.Utc),
            ShiftEnd = new DateTime(2026, 8, 27, 18, 0, 0, DateTimeKind.Utc),
            StartMileage = 152000,
            EndMileage = 152190,
            Status = "Completed",
            ManagerNote = string.Empty,
        });

        await SetAsync(db, "shifts", ShiftAnaSep2Late, new ShiftLog
        {
            ShiftId = ShiftAnaSep2Late,
            DriverId = DriverAna,
            TaxiId = Taxi4,
            ShiftStart = new DateTime(2026, 9, 2, 6, 0, 0, DateTimeKind.Utc),
            ShiftEnd = new DateTime(2026, 9, 2, 19, 10, 0, DateTimeKind.Utc),
            StartMileage = 152190,
            EndMileage = 152410,
            Status = "Overdue",
            ManagerNote = "Driver returned 1 hour past shift end citing heavy traffic.",
        });

        // A third currently-active shift, with an unresolved SOS tied to it (seeded below in
        // SeedEmergencyAlertsAsync) - demonstrates the "SOS" fleet status.
        await SetAsync(db, "shifts", ShiftAnaActiveSos, new ShiftLog
        {
            ShiftId = ShiftAnaActiveSos,
            DriverId = DriverAna,
            TaxiId = Taxi4,
            ShiftStart = DateTime.UtcNow.Date.AddHours(6),
            ShiftEnd = null,
            StartMileage = 152410,
            EndMileage = 0,
            Status = "Active",
            ManagerNote = string.Empty,
        });
    }

    // ---------------------------------------------------------------------
    // boundary_payments
    // ---------------------------------------------------------------------
    private static async Task SeedBoundaryPaymentsAsync(FirestoreDb db)
    {
        Console.WriteLine("Seeding boundary_payments...");

        // Fixes the existing document's amountPaid (was the string "600") while keeping
        // everything else about it the same.
        await SetAsync(db, "boundary_payments", "REiuRCcA3xVTpAUrxCyQ", new BoundaryPayment
        {
            ShiftId = ShiftJuanOverdueExisting,
            ExpectedBoundary = 800,
            LateFees = 200,
            AmountPaid = 600,
            PaymentMethod = PaymentMethod.Cash,
            PaymentStatus = PaymentStatus.Partial,
            ReferenceNumber = 0,
            EPayReceiptPhoto = "",
            Timestamp = new DateTime(2026, 8, 20, 15, 45, 31, DateTimeKind.Utc),
        });

        await SetAsync(db, "boundary_payments", $"{ShiftJuanAug31}_PAY", new BoundaryPayment
        {
            ShiftId = ShiftJuanAug31,
            ExpectedBoundary = 800,
            LateFees = 0,
            AmountPaid = 800,
            PaymentMethod = PaymentMethod.Cash,
            PaymentStatus = PaymentStatus.Paid,
            ReferenceNumber = 0,
            EPayReceiptPhoto = "",
            Timestamp = new DateTime(2026, 8, 31, 18, 10, 0, DateTimeKind.Utc),
        });

        await SetAsync(db, "boundary_payments", $"{ShiftMariaAug29}_PAY", new BoundaryPayment
        {
            ShiftId = ShiftMariaAug29,
            ExpectedBoundary = 800,
            LateFees = 0,
            AmountPaid = 800,
            PaymentMethod = PaymentMethod.EWallet,
            PaymentStatus = PaymentStatus.Paid,
            ReferenceNumber = 458213,
            EPayReceiptPhoto = "https://storage.googleapis.com/larga-blmtaxi.appspot.com/epay_receipts/SHIFT_2026082901.jpg",
            Timestamp = new DateTime(2026, 8, 29, 18, 5, 0, DateTimeKind.Utc),
        });

        await SetAsync(db, "boundary_payments", $"{ShiftMariaSep4}_PAY", new BoundaryPayment
        {
            ShiftId = ShiftMariaSep4,
            ExpectedBoundary = 800,
            LateFees = 0,
            AmountPaid = 800,
            PaymentMethod = PaymentMethod.Cash,
            PaymentStatus = PaymentStatus.Paid,
            ReferenceNumber = 0,
            EPayReceiptPhoto = "",
            Timestamp = new DateTime(2026, 9, 4, 18, 5, 0, DateTimeKind.Utc),
        });

        await SetAsync(db, "boundary_payments", $"{ShiftPedroSep3}_PAY", new BoundaryPayment
        {
            ShiftId = ShiftPedroSep3,
            ExpectedBoundary = 800,
            LateFees = 0,
            AmountPaid = 800,
            PaymentMethod = PaymentMethod.EWallet,
            PaymentStatus = PaymentStatus.Paid,
            ReferenceNumber = 771190,
            EPayReceiptPhoto = "https://storage.googleapis.com/larga-blmtaxi.appspot.com/epay_receipts/SHIFT_2026090301.jpg",
            Timestamp = new DateTime(2026, 9, 3, 18, 5, 0, DateTimeKind.Utc),
        });

        await SetAsync(db, "boundary_payments", $"{ShiftAnaAug27}_PAY", new BoundaryPayment
        {
            ShiftId = ShiftAnaAug27,
            ExpectedBoundary = 800,
            LateFees = 0,
            AmountPaid = 300,
            PaymentMethod = PaymentMethod.Cash,
            PaymentStatus = PaymentStatus.Partial,
            ReferenceNumber = 0,
            EPayReceiptPhoto = "",
            Timestamp = new DateTime(2026, 8, 27, 18, 5, 0, DateTimeKind.Utc),
        });

        await SetAsync(db, "boundary_payments", $"{ShiftAnaSep2Late}_PAY", new BoundaryPayment
        {
            ShiftId = ShiftAnaSep2Late,
            ExpectedBoundary = 800,
            LateFees = 100,
            AmountPaid = 500,
            PaymentMethod = PaymentMethod.Cash,
            PaymentStatus = PaymentStatus.Partial,
            ReferenceNumber = 0,
            EPayReceiptPhoto = "",
            Timestamp = new DateTime(2026, 9, 2, 19, 15, 0, DateTimeKind.Utc),
        });
    }

    // ---------------------------------------------------------------------
    // fuel_logs
    // ---------------------------------------------------------------------
    private static async Task SeedFuelLogsAsync(FirestoreDb db)
    {
        Console.WriteLine("Seeding fuel_logs...");

        await SetAsync(db, "fuel_logs", $"{ShiftJuanOverdueExisting}_FUEL", new FuelLog
        {
            ShiftId = ShiftJuanOverdueExisting,
            FuelStation = "Petron - EDSA Taguig",
            LitersRefueled = 10.5m,
            FuelCost = 650,
            ORNumber = "OR-2026-000101",
            ReceiptImageUrl = "https://storage.googleapis.com/larga-blmtaxi.appspot.com/fuel_receipts/SHIFT_20250612_001.jpg",
            ReceiptTimestamp = new DateTime(2026, 8, 20, 5, 45, 0, DateTimeKind.Utc),
            VerificationStatus = FuelVerificationStatus.Verified,
            FuelLogDetails = "Full tank at start of shift.",
            OdometerReading = 187430,
            OdometerPhotoUrl = "https://storage.googleapis.com/larga-blmtaxi.appspot.com/odometer/SHIFT_20250612_001.jpg",
        });

        await SetAsync(db, "fuel_logs", $"{ShiftJuanAug31}_FUEL", new FuelLog
        {
            ShiftId = ShiftJuanAug31,
            FuelStation = "Shell - C5 Taguig",
            LitersRefueled = 9.2m,
            FuelCost = 570,
            ORNumber = "OR-2026-000102",
            ReceiptImageUrl = "https://storage.googleapis.com/larga-blmtaxi.appspot.com/fuel_receipts/SHIFT_2026083101.jpg",
            ReceiptTimestamp = new DateTime(2026, 8, 31, 5, 50, 0, DateTimeKind.Utc),
            VerificationStatus = FuelVerificationStatus.Verified,
            FuelLogDetails = string.Empty,
            OdometerReading = 187604,
            OdometerPhotoUrl = "https://storage.googleapis.com/larga-blmtaxi.appspot.com/odometer/SHIFT_2026083101.jpg",
        });

        await SetAsync(db, "fuel_logs", $"{ShiftJuanActiveToday}_FUEL", new FuelLog
        {
            ShiftId = ShiftJuanActiveToday,
            FuelStation = "Petron - EDSA Taguig",
            LitersRefueled = 8.0m,
            FuelCost = 496,
            ORNumber = "OR-2026-000108",
            ReceiptImageUrl = "https://storage.googleapis.com/larga-blmtaxi.appspot.com/fuel_receipts/SHIFT_2026090501.jpg",
            ReceiptTimestamp = DateTime.UtcNow.Date.AddHours(5).AddMinutes(50),
            VerificationStatus = FuelVerificationStatus.Pending, // shift still active - manager hasn't reviewed yet
            FuelLogDetails = string.Empty,
            OdometerReading = 187780,
            OdometerPhotoUrl = "https://storage.googleapis.com/larga-blmtaxi.appspot.com/odometer/SHIFT_2026090501.jpg",
        });

        await SetAsync(db, "fuel_logs", $"{ShiftMariaAug29}_FUEL", new FuelLog
        {
            ShiftId = ShiftMariaAug29,
            FuelStation = "Caltex - Bicutan",
            LitersRefueled = 8.0m,
            FuelCost = 496,
            ORNumber = "OR-2026-000103",
            ReceiptImageUrl = "https://storage.googleapis.com/larga-blmtaxi.appspot.com/fuel_receipts/SHIFT_2026082901.jpg",
            ReceiptTimestamp = new DateTime(2026, 8, 29, 5, 55, 0, DateTimeKind.Utc),
            VerificationStatus = FuelVerificationStatus.Flagged, // receipt photo illegible
            FuelLogDetails = "Receipt image blurry - re-verification requested.",
            OdometerReading = 95000,
            OdometerPhotoUrl = "https://storage.googleapis.com/larga-blmtaxi.appspot.com/odometer/SHIFT_2026082901.jpg",
        });

        await SetAsync(db, "fuel_logs", $"{ShiftMariaSep4}_FUEL", new FuelLog
        {
            ShiftId = ShiftMariaSep4,
            FuelStation = "Caltex - Bicutan",
            LitersRefueled = 7.5m,
            FuelCost = 465,
            ORNumber = "OR-2026-000106",
            ReceiptImageUrl = "https://storage.googleapis.com/larga-blmtaxi.appspot.com/fuel_receipts/SHIFT_2026090401.jpg",
            ReceiptTimestamp = new DateTime(2026, 9, 4, 5, 45, 0, DateTimeKind.Utc),
            VerificationStatus = FuelVerificationStatus.Verified,
            FuelLogDetails = string.Empty,
            OdometerReading = 95230,
            OdometerPhotoUrl = "https://storage.googleapis.com/larga-blmtaxi.appspot.com/odometer/SHIFT_2026090401.jpg",
        });

        await SetAsync(db, "fuel_logs", $"{ShiftPedroSep3}_FUEL", new FuelLog
        {
            ShiftId = ShiftPedroSep3,
            FuelStation = "Phoenix - Ortigas",
            LitersRefueled = 9.0m,
            FuelCost = 558,
            ORNumber = "OR-2026-000105",
            ReceiptImageUrl = "https://storage.googleapis.com/larga-blmtaxi.appspot.com/fuel_receipts/SHIFT_2026090301.jpg",
            ReceiptTimestamp = new DateTime(2026, 9, 3, 5, 50, 0, DateTimeKind.Utc),
            VerificationStatus = FuelVerificationStatus.Verified,
            FuelLogDetails = string.Empty,
            OdometerReading = 120500,
            OdometerPhotoUrl = "https://storage.googleapis.com/larga-blmtaxi.appspot.com/odometer/SHIFT_2026090301.jpg",
        });

        await SetAsync(db, "fuel_logs", $"{ShiftAnaAug27}_FUEL", new FuelLog
        {
            ShiftId = ShiftAnaAug27,
            FuelStation = "Shell - Makati Ave",
            LitersRefueled = 7.0m,
            FuelCost = 434,
            ORNumber = "OR-2026-000104",
            ReceiptImageUrl = "https://storage.googleapis.com/larga-blmtaxi.appspot.com/fuel_receipts/SHIFT_2026082701.jpg",
            ReceiptTimestamp = new DateTime(2026, 8, 27, 5, 55, 0, DateTimeKind.Utc),
            VerificationStatus = FuelVerificationStatus.Verified,
            FuelLogDetails = string.Empty,
            OdometerReading = 152000,
            OdometerPhotoUrl = "https://storage.googleapis.com/larga-blmtaxi.appspot.com/odometer/SHIFT_2026082701.jpg",
        });

        await SetAsync(db, "fuel_logs", $"{ShiftAnaSep2Late}_FUEL", new FuelLog
        {
            ShiftId = ShiftAnaSep2Late,
            FuelStation = "Shell - Makati Ave",
            LitersRefueled = 6.0m,
            FuelCost = 372,
            ORNumber = "OR-2026-000107",
            ReceiptImageUrl = "https://storage.googleapis.com/larga-blmtaxi.appspot.com/fuel_receipts/SHIFT_2026090201.jpg",
            ReceiptTimestamp = new DateTime(2026, 9, 2, 5, 45, 0, DateTimeKind.Utc),
            VerificationStatus = FuelVerificationStatus.Pending,
            FuelLogDetails = string.Empty,
            OdometerReading = 152190,
            OdometerPhotoUrl = "https://storage.googleapis.com/larga-blmtaxi.appspot.com/odometer/SHIFT_2026090201.jpg",
        });

        await SetAsync(db, "fuel_logs", $"{ShiftMariaActiveOnBreak}_FUEL", new FuelLog
        {
            ShiftId = ShiftMariaActiveOnBreak,
            FuelStation = "Caltex - Bicutan",
            LitersRefueled = 7.8m,
            FuelCost = 484,
            ORNumber = "OR-2026-000109",
            ReceiptImageUrl = "https://storage.googleapis.com/larga-blmtaxi.appspot.com/fuel_receipts/SHIFT_2026090502.jpg",
            ReceiptTimestamp = DateTime.UtcNow.Date.AddHours(5).AddMinutes(50),
            VerificationStatus = FuelVerificationStatus.Pending,
            FuelLogDetails = string.Empty,
            OdometerReading = 95475,
            OdometerPhotoUrl = "https://storage.googleapis.com/larga-blmtaxi.appspot.com/odometer/SHIFT_2026090502.jpg",
        });

        await SetAsync(db, "fuel_logs", $"{ShiftAnaActiveSos}_FUEL", new FuelLog
        {
            ShiftId = ShiftAnaActiveSos,
            FuelStation = "Shell - Makati Ave",
            LitersRefueled = 6.5m,
            FuelCost = 403,
            ORNumber = "OR-2026-000110",
            ReceiptImageUrl = "https://storage.googleapis.com/larga-blmtaxi.appspot.com/fuel_receipts/SHIFT_2026090503.jpg",
            ReceiptTimestamp = DateTime.UtcNow.Date.AddHours(5).AddMinutes(55),
            VerificationStatus = FuelVerificationStatus.Pending,
            FuelLogDetails = string.Empty,
            OdometerReading = 152410,
            OdometerPhotoUrl = "https://storage.googleapis.com/larga-blmtaxi.appspot.com/odometer/SHIFT_2026090503.jpg",
        });
    }

    // ---------------------------------------------------------------------
    // maintenance_logs + maintenance_parts_used
    // ---------------------------------------------------------------------
    private const string MaintenanceTireBlowoutExisting = "E7IDULZqCBNlgXx03yrw";
    private const string MaintenanceTaxi3Engine = "MAINT_TAXI003_ENGINE";
    private const string MaintenanceTaxi2Routine = "MAINT_TAXI002_ROUTINE";

    private static async Task SeedMaintenanceAsync(FirestoreDb db)
    {
        Console.WriteLine("Seeding maintenance_logs...");

        // Fixes the existing document's field-name casing bug
        // (was "supportingPhotoURL", code expects "supportingPhotoUrl") while keeping
        // the rest of the record the same.
        await SetAsync(db, "maintenance_logs", MaintenanceTireBlowoutExisting, new MaintenanceRecord
        {
            TaxiId = Taxi1,
            ManagerId = SampleManagerUid,
            ShiftId = ShiftJuanOverdueExisting,
            MaintenanceType = MaintenanceType.BreakdownRepair,
            IssueTitle = "Left rear tire blowout",
            IssueDescription = "Driver reported a sudden blowout on the left rear tire while along the national highway in Taguig.",
            DateLogged = new DateTime(2026, 8, 20, 14, 55, 53, DateTimeKind.Utc),
            DateResolved = new DateTime(2026, 8, 21, 10, 0, 33, DateTimeKind.Utc),
            LaborCost = 150,
            TotalCost = 1350,
            SupportingPhotoUrl = "https://storage.googleapis.com/larga-blmtaxi.appspot.com/defect_reports/SHIFT_20250612_001.jpg",
            PriorityLevel = PriorityLevel.High,
        });

        await SetAsync(db, "maintenance_logs", MaintenanceTaxi3Engine, new MaintenanceRecord
        {
            TaxiId = Taxi3,
            ManagerId = SampleManagerUid,
            ShiftId = null,
            MaintenanceType = MaintenanceType.BreakdownRepair,
            IssueTitle = "Engine overheating / unusual noise",
            IssueDescription = "Driver reported engine noise at end of shift on Sep 3. Unit pulled for inspection; overheating confirmed at the shop.",
            DateLogged = new DateTime(2026, 9, 4, 9, 0, 0, DateTimeKind.Utc),
            DateResolved = null, // still open - good test case for "Open Incidents" widget
            LaborCost = 800,
            TotalCost = 800, // estimate only while parts are pending
            SupportingPhotoUrl = "https://storage.googleapis.com/larga-blmtaxi.appspot.com/defect_reports/MAINT_TAXI003_ENGINE.jpg",
            PriorityLevel = PriorityLevel.High,
        });

        await SetAsync(db, "maintenance_logs", MaintenanceTaxi2Routine, new MaintenanceRecord
        {
            TaxiId = Taxi2,
            ManagerId = SampleManagerUid,
            ShiftId = null,
            MaintenanceType = MaintenanceType.RoutineCheckup,
            IssueTitle = "Scheduled oil change and filter replacement",
            IssueDescription = "Routine 5,000 km service: oil and air filter replacement.",
            DateLogged = new DateTime(2026, 8, 28, 8, 0, 0, DateTimeKind.Utc),
            DateResolved = new DateTime(2026, 8, 28, 11, 0, 0, DateTimeKind.Utc),
            LaborCost = 200,
            TotalCost = 350,
            SupportingPhotoUrl = "https://storage.googleapis.com/larga-blmtaxi.appspot.com/defect_reports/MAINT_TAXI002_ROUTINE.jpg",
            PriorityLevel = PriorityLevel.Low,
        });

        Console.WriteLine("Seeding maintenance_parts_used...");

        await SetAsync(db, "maintenance_parts_used", $"{MaintenanceTireBlowoutExisting}_PART1", new MaintenancePartsUsed
        {
            MaintenanceId = MaintenanceTireBlowoutExisting,
            PartId = "PART_TIRE",
            QuantityUsed = 1,
        });

        await SetAsync(db, "maintenance_parts_used", $"{MaintenanceTaxi2Routine}_PART1", new MaintenancePartsUsed
        {
            MaintenanceId = MaintenanceTaxi2Routine,
            PartId = "PART_ENGINEOIL",
            QuantityUsed = 4,
        });

        await SetAsync(db, "maintenance_parts_used", $"{MaintenanceTaxi2Routine}_PART2", new MaintenancePartsUsed
        {
            MaintenanceId = MaintenanceTaxi2Routine,
            PartId = "PART_AIRFILTER",
            QuantityUsed = 1,
        });
    }

    // ---------------------------------------------------------------------
    // spare_parts
    // ---------------------------------------------------------------------
    private static async Task SeedSparePartsAsync(FirestoreDb db)
    {
        Console.WriteLine("Seeding spare_parts...");

        await SetAsync(db, "spare_parts", "PART_TIRE", new SparePart
        {
            PartName = "Tire (175/65R14)",
            StockQuantity = 8,
            ReorderLevel = 4,
            UnitPrice = 3200,
        });

        await SetAsync(db, "spare_parts", "PART_BRAKEPAD", new SparePart
        {
            PartName = "Brake Pad Set (Front)",
            StockQuantity = 3, // below reorder level - low-stock test case
            ReorderLevel = 5,
            UnitPrice = 1200,
        });

        await SetAsync(db, "spare_parts", "PART_ENGINEOIL", new SparePart
        {
            PartName = "Engine Oil (1L)",
            StockQuantity = 20,
            ReorderLevel = 10,
            UnitPrice = 850,
        });

        await SetAsync(db, "spare_parts", "PART_AIRFILTER", new SparePart
        {
            PartName = "Air Filter",
            StockQuantity = 6,
            ReorderLevel = 5,
            UnitPrice = 450,
        });

        await SetAsync(db, "spare_parts", "PART_BATTERY", new SparePart
        {
            PartName = "Car Battery (N50)",
            StockQuantity = 2, // below reorder level - low-stock test case
            ReorderLevel = 3,
            UnitPrice = 4500,
        });
    }

    // ---------------------------------------------------------------------
    // emergency_alerts
    // ---------------------------------------------------------------------
    private static async Task SeedEmergencyAlertsAsync(FirestoreDb db)
    {
        Console.WriteLine("Seeding emergency_alerts...");

        await SetAsync(db, "emergency_alerts", "ALERT_ANA_SEP2", new EmergencyAlert
        {
            ShiftId = ShiftAnaSep2Late,
            Latitude = 14.5547,
            Longitude = 121.0244,
            IsResolved = true,
            Timestamp = new DateTime(2026, 9, 2, 12, 30, 0, DateTimeKind.Utc),
        });

        await SetAsync(db, "emergency_alerts", "ALERT_ANA_ACTIVE", new EmergencyAlert
        {
            ShiftId = ShiftAnaActiveSos,
            Latitude = 14.5547,
            Longitude = 121.0244,
            IsResolved = false, // still open - this taxi is the dedicated "SOS" fleet-status example
            Timestamp = DateTime.UtcNow.AddMinutes(-15),
        });
    }

    // ---------------------------------------------------------------------
    // gps_telemetry
    // ---------------------------------------------------------------------
    private static async Task SeedGpsTelemetryAsync(FirestoreDb db)
    {
        Console.WriteLine("Seeding gps_telemetry...");

        (double lat, double lng, int speed, int minutesAgo)[] points =
        {
            (14.5995, 120.9842, 0, 60),
            (14.6010, 120.9865, 22, 45),
            (14.6035, 120.9901, 35, 30),
            (14.6050, 120.9930, 18, 15),
            (14.5995, 120.9842, 5, 1),
        };

        for (int i = 0; i < points.Length; i++)
        {
            (double lat, double lng, int speed, int minutesAgo) = points[i];
            await SetAsync(db, "gps_telemetry", $"{ShiftJuanActiveToday}_PT{i + 1}", new GpsTelemetry
            {
                ShiftId = ShiftJuanActiveToday,
                Latitude = lat,
                Longitude = lng,
                Speed = speed,
                Timestamp = DateTime.UtcNow.AddMinutes(-minutesAgo),
            });
        }
    }

    // ---------------------------------------------------------------------
    // audit_logs
    // ---------------------------------------------------------------------
    private static async Task SeedAuditLogsAsync(FirestoreDb db)
    {
        Console.WriteLine("Seeding audit_logs...");

        await SetAsync(db, "audit_logs", "AUDIT_001", new AuditLog
        {
            UserId = SampleManagerUid,
            ActionType = "Login",
            AuditLogDetails = "Manager logged into the Fleet Ops web portal.",
            IpAddress = "203.0.113.10",
            Timestamp = DateTime.UtcNow.AddHours(-2),
        });

        await SetAsync(db, "audit_logs", "AUDIT_002", new AuditLog
        {
            UserId = SampleManagerUid,
            ActionType = "ResolveMaintenanceTicket",
            AuditLogDetails = $"Marked maintenance ticket {MaintenanceTireBlowoutExisting} (Left rear tire blowout) as resolved.",
            IpAddress = "203.0.113.10",
            Timestamp = new DateTime(2026, 8, 21, 10, 0, 33, DateTimeKind.Utc),
        });

        await SetAsync(db, "audit_logs", "AUDIT_003", new AuditLog
        {
            UserId = SampleManagerUid,
            ActionType = "VerifyFuelReceipt",
            AuditLogDetails = $"Verified fuel receipt for shift {ShiftJuanAug31}.",
            IpAddress = "203.0.113.10",
            Timestamp = new DateTime(2026, 8, 31, 19, 0, 0, DateTimeKind.Utc),
        });

        await SetAsync(db, "audit_logs", "AUDIT_004", new AuditLog
        {
            UserId = DriverAna,
            ActionType = "EmergencyAlertTriggered",
            AuditLogDetails = $"Driver triggered SOS during active shift {ShiftAnaActiveSos}.",
            IpAddress = null,
            Timestamp = DateTime.UtcNow.AddMinutes(-15),
        });

        await SetAsync(db, "audit_logs", "AUDIT_005", new AuditLog
        {
            UserId = SampleManagerUid,
            ActionType = "FlagFuelReceipt",
            AuditLogDetails = $"Flagged fuel receipt for shift {ShiftMariaAug29} as illegible; requested re-submission.",
            IpAddress = "203.0.113.10",
            Timestamp = new DateTime(2026, 8, 29, 20, 0, 0, DateTimeKind.Utc),
        });
    }

    // ---------------------------------------------------------------------
    // handover_checklists
    // ---------------------------------------------------------------------
    private static async Task SeedHandoverChecklistsAsync(FirestoreDb db)
    {
        Console.WriteLine("Seeding handover_checklists...");

        await SetAsync(db, "handover_checklists", $"{ShiftJuanActiveToday}_PRE", new HandoverChecklist
        {
            ShiftId = ShiftJuanActiveToday,
            ChecklistType = ChecklistType.PreShift,
            TireCondition = true,
            OilLevel = true,
            CoolantLevel = true,
            InteriorCleanliness = true,
            ExteriorScratches = false,
            FuelVerification = FuelVerification.HalfTank,
            ScratchesPhotoUrl = null,
            FuelDashboardUrl = "https://storage.googleapis.com/larga-blmtaxi.appspot.com/fuel_dashboard/SHIFT_2026090501_pre.jpg",
            Timestamp = DateTime.UtcNow.Date.AddHours(6),
        });

        await SetAsync(db, "handover_checklists", $"{ShiftJuanAug31}_END", new HandoverChecklist
        {
            ShiftId = ShiftJuanAug31,
            ChecklistType = ChecklistType.EndShift,
            TireCondition = true,
            OilLevel = true,
            CoolantLevel = true,
            InteriorCleanliness = true,
            ExteriorScratches = false,
            FuelVerification = FuelVerification.BelowHalfTank,
            ScratchesPhotoUrl = null,
            FuelDashboardUrl = "https://storage.googleapis.com/larga-blmtaxi.appspot.com/fuel_dashboard/SHIFT_2026083101_end.jpg",
            Timestamp = new DateTime(2026, 8, 31, 18, 5, 0, DateTimeKind.Utc),
        });

        await SetAsync(db, "handover_checklists", $"{ShiftAnaSep2Late}_PRE", new HandoverChecklist
        {
            ShiftId = ShiftAnaSep2Late,
            ChecklistType = ChecklistType.PreShift,
            TireCondition = true,
            OilLevel = true,
            CoolantLevel = true,
            InteriorCleanliness = true,
            ExteriorScratches = true, // driver flagged a new scratch before starting
            FuelVerification = FuelVerification.HalfTank,
            ScratchesPhotoUrl = "https://storage.googleapis.com/larga-blmtaxi.appspot.com/scratches/SHIFT_2026090201_pre.jpg",
            FuelDashboardUrl = "https://storage.googleapis.com/larga-blmtaxi.appspot.com/fuel_dashboard/SHIFT_2026090201_pre.jpg",
            Timestamp = new DateTime(2026, 9, 2, 6, 0, 0, DateTimeKind.Utc),
        });

        await SetAsync(db, "handover_checklists", $"{ShiftMariaActiveOnBreak}_PRE", new HandoverChecklist
        {
            ShiftId = ShiftMariaActiveOnBreak,
            ChecklistType = ChecklistType.PreShift,
            TireCondition = true,
            OilLevel = true,
            CoolantLevel = true,
            InteriorCleanliness = true,
            ExteriorScratches = false,
            FuelVerification = FuelVerification.HalfTank,
            ScratchesPhotoUrl = null,
            FuelDashboardUrl = "https://storage.googleapis.com/larga-blmtaxi.appspot.com/fuel_dashboard/SHIFT_2026090502_pre.jpg",
            Timestamp = DateTime.UtcNow.Date.AddHours(6),
        });

        await SetAsync(db, "handover_checklists", $"{ShiftAnaActiveSos}_PRE", new HandoverChecklist
        {
            ShiftId = ShiftAnaActiveSos,
            ChecklistType = ChecklistType.PreShift,
            TireCondition = true,
            OilLevel = true,
            CoolantLevel = true,
            InteriorCleanliness = true,
            ExteriorScratches = false,
            FuelVerification = FuelVerification.HalfTank,
            ScratchesPhotoUrl = null,
            FuelDashboardUrl = "https://storage.googleapis.com/larga-blmtaxi.appspot.com/fuel_dashboard/SHIFT_2026090503_pre.jpg",
            Timestamp = DateTime.UtcNow.Date.AddHours(6),
        });
    }

    // ---------------------------------------------------------------------
    // shift_schedules
    // ---------------------------------------------------------------------
    private static async Task SeedShiftSchedulesAsync(FirestoreDb db)
    {
        Console.WriteLine("Seeding shift_schedules...");

        // Doc IDs match ManagerWeb's Schedule Planner exactly ({driverId}_{yyyyMMdd}) so a
        // manager clicking a cell to assign/clear a unit overwrites/deletes these same
        // documents instead of creating duplicates alongside them.
        DateTime today = DateTime.UtcNow.Date;
        int daysSinceMonday = (7 + (int)today.DayOfWeek - (int)DayOfWeek.Monday) % 7;
        DateTime weekStart = today.AddDays(-daysSinceMonday);

        // Mon..Sun per driver - null means a rest day (no document at all). Each driver
        // mostly keeps their own AssignedTaxiId from SeedUsersAsync, with a couple of rest
        // days spread through the week so the planner isn't a wall of identical cells.
        (string DriverId, string?[] Days)[] plan =
        {
            (DriverJuan,   new[] { Taxi1, null,  Taxi1, Taxi1, Taxi1, Taxi1, null  }),
            (DriverMaria,  new[] { Taxi2, Taxi2, null,  Taxi2, Taxi2, null,  Taxi2 }),
            (DriverPedro,  new[] { Taxi3, Taxi3, Taxi3, null,  Taxi3, Taxi3, null  }),
            (DriverAna,    new[] { Taxi4, null,  Taxi4, Taxi4, null,  Taxi4, Taxi4 }),
            (DriverCarlos, new[] { null,  Taxi5, Taxi5, Taxi5, Taxi5, null,  Taxi5 }),
        };

        foreach ((string driverId, string?[] days) in plan)
        {
            for (int i = 0; i < 7; i++)
            {
                string? taxiId = days[i];
                if (taxiId is null)
                {
                    continue;
                }

                DateTime date = weekStart.AddDays(i);
                await SetAsync(db, "shift_schedules", $"{driverId}_{date:yyyyMMdd}", new ShiftSchedule
                {
                    DriverId = driverId,
                    TaxiId = taxiId,
                    ScheduledStartTime = date.AddHours(6),
                    Status = "Planned",
                });
            }
        }
    }

    // ---------------------------------------------------------------------
    // system_configs
    // ---------------------------------------------------------------------
    private static async Task SeedSystemConfigAsync(FirestoreDb db)
    {
        Console.WriteLine("Seeding system_configs...");

        // Matches the existing document's values - written here only so the tool is a
        // complete, self-contained reset of everything it depends on (boundary amounts
        // above assume a defaultBoundaryRate of 800 and a standardLatePenalty of 100).
        await SetAsync(db, "system_configs", "global", new SystemConfig
        {
            StandardLatePenalty = 100,
            DefaultBoundaryRate = 800,
            IdleThresholdMinutes = 10,
        });
    }
}
