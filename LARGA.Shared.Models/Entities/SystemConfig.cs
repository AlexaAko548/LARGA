using System.Collections.Generic;
using Google.Cloud.Firestore;

namespace LARGA.Shared.Models.Entities;

[FirestoreData]
public class SystemConfig
{
    [FirestoreDocumentId]
    public string ConfigId { get; set; } = string.Empty;

    [FirestoreProperty("standardLatePenalty")]
    public double StandardLatePenalty { get; set; }

    [FirestoreProperty("defaultBoundaryRate")]
    public double DefaultBoundaryRate { get; set; }

    [FirestoreProperty("idleThresholdMinutes")]
    public double IdleThresholdMinutes { get; set; } = 15;

    // LAR-86/87: numbers the driver app auto-answers after an automated SOS. Kept here (any
    // signed-in user may read system_configs) because firestore.rules don't let a driver read
    // managers' users documents to discover them.
    [FirestoreProperty("managerPhoneNumbers")]
    public List<string> ManagerPhoneNumbers { get; set; } = new();

    // GCash numbers / bank account numbers boundary payments may be sent to. Quick Ledger
    // (mobile) and the Financial Ledger (web) compare the receipt's recipient against these
    // (PayoutAccountMatcher). Entries are free text such as "0917 123 4567" or
    // "BDO 001234567890" - only the digits are compared.
    [FirestoreProperty("authorizedPayoutAccounts")]
    public List<string> AuthorizedPayoutAccounts { get; set; } = new();

    // FUEL-017: fuel submissions outside these are flagged automatically (FuelAutoCheckService).
    // Odometer distance vs GPS distance, as a % of the GPS distance.
    [FirestoreProperty("fuelDistanceTolerancePercent")]
    public double FuelDistanceTolerancePercent { get; set; } = 8.0;

    // Plausible fuel cost per odometer km, in pesos.
    [FirestoreProperty("fuelCostPerKmMin")]
    public double FuelCostPerKmMin { get; set; } = 4.50;

    [FirestoreProperty("fuelCostPerKmMax")]
    public double FuelCostPerKmMax { get; set; } = 7.50;

    // Optional, per unit (taxiId -> km/L): flagged when a refuel's km/L is off by more than the
    // distance tolerance %. Units not listed aren't checked.
    [FirestoreProperty("fuelExpectedKmPerLiter")]
    public Dictionary<string, double> FuelExpectedKmPerLiter { get; set; } = new();

    // MNT-014/015/016: recurring preventive maintenance (MaintenanceSchedulerService), in days.
    [FirestoreProperty("maintenanceWeeklyIntervalDays")]
    public int MaintenanceWeeklyIntervalDays { get; set; } = 7;

    [FirestoreProperty("maintenanceBiweeklyIntervalDays")]
    public int MaintenanceBiweeklyIntervalDays { get; set; } = 14;

    [FirestoreProperty("maintenanceMonthlyIntervalDays")]
    public int MaintenanceMonthlyIntervalDays { get; set; } = 30;

    // Oil change: every 2 months by default...
    [FirestoreProperty("oilChangeIntervalDays")]
    public int OilChangeIntervalDays { get; set; } = 60;

    // ...and, for units listed here (taxiId -> km), also every N km - whichever comes first.
    [FirestoreProperty("oilChangeMileageIntervalKm")]
    public Dictionary<string, int> OilChangeMileageIntervalKm { get; set; } = new();

    // A DueSoon bell alert is raised this many days before a check is due.
    [FirestoreProperty("maintenanceDueSoonDays")]
    public int MaintenanceDueSoonDays { get; set; } = 2;
}
