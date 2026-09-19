using System.Globalization;
using LARGA.MobileApp.Models;
using LARGA.Shared.Models.Entities;
using LARGA.SharedCore.Models.FinancialLedger;
using Plugin.Firebase.Firestore;

namespace LARGA.MobileApp.Services;

public class MobileFinancialLedgerService
{
    private const decimal DefaultBoundaryRate = 800m;

    public async Task<List<UserProfile>> GetDriversAsync()
    {
        var users = await CrossFirebaseFirestore.Current
            .GetCollection("users")
            .GetDocumentsAsync<Dictionary<string, object>>();

        var drivers = new List<UserProfile>();
        foreach (var document in users.Documents)
        {
            if (document.Data == null)
            {
                continue;
            }

            string role = GetString(document.Data, "role");
            string normalizedRole = NormalizeRole(role);
            bool looksLikeDriver = normalizedRole.Equals("driver", StringComparison.OrdinalIgnoreCase)
                || (string.IsNullOrWhiteSpace(normalizedRole)
                    && !string.IsNullOrWhiteSpace(GetString(document.Data, "assignedTaxiId"))
                    && string.IsNullOrWhiteSpace(GetString(document.Data, "managerNote")));

            if (!looksLikeDriver)
            {
                continue;
            }

            drivers.Add(new UserProfile
            {
                UserId = document.Reference.Id,
                FullName = GetString(document.Data, "fullName", GetString(document.Data, "name", document.Reference.Id)),
                Role = string.IsNullOrWhiteSpace(role) ? "Driver" : role,
                AssignedTaxiId = GetString(document.Data, "assignedTaxiId"),
            });
        }

        return drivers
            .OrderBy(driver => driver.FullName)
            .ToList();
    }

    public async Task<DailySettlementSnapshot> GetDailySettlementAsync(DateTime dateUtc)
    {
        (DateTime dayStartUtc, DateTime dayEndUtc, DateTime localDate) = GetUtcBoundsForLocalDay(dateUtc);
        var shifts = await CrossFirebaseFirestore.Current
            .GetCollection("shifts")
            .GetDocumentsAsync<Dictionary<string, object>>();
        var users = await CrossFirebaseFirestore.Current
            .GetCollection("users")
            .GetDocumentsAsync<Dictionary<string, object>>();
        var defaultRate = await GetDefaultBoundaryRateAsync();
        var driverNames = users.Documents.ToDictionary(
            document => document.Reference.Id,
            document => GetString(document.Data, "fullName"));
        var rows = new List<SettlementRow>();

        foreach (var document in shifts.Documents)
        {
            if (document.Data == null)
            {
                continue;
            }

            string shiftId = GetString(document.Data, "shiftId", document.Reference.Id);
            DateTime? shiftStartUtc = ToUtc(GetDate(document.Data, "shiftStart"));
            bool inDay = IsWithinUtcWindow(shiftStartUtc, dayStartUtc, dayEndUtc)
                || IsShiftIdOnLocalDay(shiftId, localDate);
            if (!inDay)
            {
                continue;
            }

            string driverId = GetString(document.Data, "driverId");
            string taxiId = GetString(document.Data, "taxiId");
            var payment = await GetPaymentAsync(shiftId);
            decimal expected = payment is null
                ? defaultRate
                : GetDecimal(payment.Value.Data, "expectedBoundary") + GetDecimal(payment.Value.Data, "lateFees");
            decimal paid = payment is null ? 0m : GetDecimal(payment.Value.Data, "amountPaid");
            string driverName = driverNames.TryGetValue(driverId, out string? name) && !string.IsNullOrWhiteSpace(name)
                ? name
                : driverId;

            rows.Add(new SettlementRow
            {
                ShiftId = shiftId,
                DriverId = driverId,
                DriverName = driverName,
                TaxiId = taxiId,
                ShiftStart = ToUtc(GetDate(document.Data, "shiftStart")),
                ShiftEnd = GetDate(document.Data, "shiftEnd"),
                ExpectedTotal = expected,
                AmountPaid = paid,
                Status = GetStatus(payment?.Data, paid, expected),
            });
        }

        rows = rows
            .OrderBy(row => row.Status == SettlementStatus.Cleared ? 1 : 0)
            .ThenBy(row => row.DriverName)
            .ToList();

        return new DailySettlementSnapshot
        {
            Date = localDate,
            ExpectedCollection = rows.Sum(row => row.ExpectedTotal),
            CollectedSoFar = rows.Sum(row => row.AmountPaid),
            ClearedCount = rows.Count(row => row.Status == SettlementStatus.Cleared),
            PartialCount = rows.Count(row => row.Status == SettlementStatus.Partial),
            WaitingCount = rows.Count(row => row.Status == SettlementStatus.Waiting),
            Rows = rows,
        };
    }

    public async Task<RecordPaymentResult> RecordPaymentAsync(string shiftId, decimal amountReceived, string paymentMethod)
    {
        return await RecordPaymentAsync(shiftId, amountReceived, paymentMethod, null, null, "Boundary");
    }

    public async Task<RecordPaymentResult> RecordPaymentAsync(
        string shiftId,
        decimal amountReceived,
        string paymentMethod,
        string? referenceNumber,
        string? receiptPhoto,
        string paymentCategory)
    {
        if (string.IsNullOrWhiteSpace(shiftId))
        {
            return new RecordPaymentResult { Ok = false, ErrorMessage = "Missing shift." };
        }

        if (amountReceived <= 0)
        {
            return new RecordPaymentResult { Ok = false, ErrorMessage = "Amount received must be greater than zero." };
        }

        try
        {
            var existing = await GetPaymentAsync(shiftId);
            decimal expected = existing is null
                ? await GetDefaultBoundaryRateAsync()
                : GetDecimal(existing.Value.Data, "expectedBoundary") + GetDecimal(existing.Value.Data, "lateFees");
            decimal newAmountPaid = (existing is null ? 0m : GetDecimal(existing.Value.Data, "amountPaid")) + amountReceived;
            string documentId = existing?.Reference ?? $"{shiftId}_PAY";
            string methodValue = string.Equals(paymentMethod, "EWallet", StringComparison.OrdinalIgnoreCase) ? "E-Wallet" : "Cash";
            var payment = new Dictionary<object, object>
            {
                ["shiftId"] = shiftId,
                ["expectedBoundary"] = (double)expected,
                ["lateFees"] = existing is null ? 0d : GetDecimal(existing.Value.Data, "lateFees"),
                ["amountPaid"] = (double)newAmountPaid,
                ["paymentMethod"] = methodValue,
                ["paymentStatus"] = newAmountPaid >= expected ? "Paid" : "Partial",
                ["paymentCategory"] = string.IsNullOrWhiteSpace(paymentCategory) ? "Boundary" : paymentCategory,
                ["timestamp"] = DateTime.UtcNow,
            };

            string existingReference = existing is null ? string.Empty : GetString(existing.Value.Data, "referenceNumber");
            string finalReference = !string.IsNullOrWhiteSpace(referenceNumber) ? referenceNumber : existingReference;
            if (!string.IsNullOrWhiteSpace(finalReference))
            {
                payment["referenceNumber"] = finalReference;
            }

            string existingReceipt = existing is null ? string.Empty : GetString(existing.Value.Data, "ePayReceiptPhoto");
            string finalReceipt = !string.IsNullOrWhiteSpace(receiptPhoto) ? receiptPhoto : existingReceipt;
            if (!string.IsNullOrWhiteSpace(finalReceipt))
            {
                payment["ePayReceiptPhoto"] = finalReceipt;
            }

            await CrossFirebaseFirestore.Current
                .GetCollection("boundary_payments")
                .GetDocument(documentId)
                .SetDataAsync(payment);

            SettlementStatus status = newAmountPaid >= expected ? SettlementStatus.Cleared : SettlementStatus.Partial;
            return new RecordPaymentResult
            {
                Ok = true,
                NewStatus = status,
                RemainingAfterPayment = Math.Max(0, expected - newAmountPaid),
            };
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Mobile ledger payment error: {ex.Message}");
            return new RecordPaymentResult { Ok = false, ErrorMessage = "Could not save this payment. Please try again." };
        }
    }

    public async Task<string?> GetTodayPendingShiftForDriverAsync(string driverId, DateTime dateUtc)
    {
        if (string.IsNullOrWhiteSpace(driverId))
        {
            return null;
        }

        DailySettlementSnapshot snapshot = await GetDailySettlementAsync(dateUtc);
        SettlementRow? row = snapshot.Rows
            .FirstOrDefault(r => r.DriverId == driverId && r.Status != SettlementStatus.Cleared);

        return string.IsNullOrWhiteSpace(row?.ShiftId) ? null : row.ShiftId;
    }

    public async Task<List<LedgerItemModel>> GetCompletedEntriesForTodayAsync(DateTime dateUtc, IEnumerable<string>? excludeShiftIds)
    {
        (DateTime dayStartUtc, DateTime dayEndUtc, DateTime localDate) = GetUtcBoundsForLocalDay(dateUtc);
        HashSet<string> excluded = excludeShiftIds == null
            ? new HashSet<string>()
            : excludeShiftIds.Where(id => !string.IsNullOrWhiteSpace(id)).ToHashSet();

        var users = await CrossFirebaseFirestore.Current
            .GetCollection("users")
            .GetDocumentsAsync<Dictionary<string, object>>();
        var shifts = await CrossFirebaseFirestore.Current
            .GetCollection("shifts")
            .GetDocumentsAsync<Dictionary<string, object>>();
        var payments = await CrossFirebaseFirestore.Current
            .GetCollection("boundary_payments")
            .GetDocumentsAsync<Dictionary<string, object>>();
        var adjustments = await CrossFirebaseFirestore.Current
            .GetCollection("debt_adjustments")
            .GetDocumentsAsync<Dictionary<string, object>>();

        Dictionary<string, string> driverNames = users.Documents
            .Where(document => document.Data != null)
            .ToDictionary(document => document.Reference.Id, document => GetString(document.Data, "fullName", document.Reference.Id));

        Dictionary<string, (string DriverId, string TaxiId)> shiftIndex = shifts.Documents
            .Where(document => document.Data != null)
            .Select(document =>
            {
                Dictionary<string, object> data = document.Data!;
                string shiftId = GetString(data, "shiftId");
                string driverId = GetString(data, "driverId");
                string taxiId = GetString(data, "taxiId");
                if (string.IsNullOrWhiteSpace(shiftId))
                {
                    shiftId = document.Reference.Id;
                }

                return (ShiftId: shiftId, DriverId: driverId, TaxiId: taxiId);
            })
            .Where(row => !string.IsNullOrWhiteSpace(row.ShiftId))
            .GroupBy(row => row.ShiftId)
            .ToDictionary(group => group.Key, group => (group.First().DriverId, group.First().TaxiId));

        var entries = new List<LedgerItemModel>();

        foreach (var paymentDocument in payments.Documents)
        {
            if (paymentDocument.Data == null)
            {
                continue;
            }

            string shiftId = GetShiftIdFromPaymentDoc(paymentDocument.Reference.Id, paymentDocument.Data);
            DateTime? timestampUtc = ToUtc(GetDate(paymentDocument.Data, "timestamp"));
            bool inDay = IsWithinUtcWindow(timestampUtc, dayStartUtc, dayEndUtc)
                || IsShiftIdOnLocalDay(shiftId, localDate)
                || (!timestampUtc.HasValue && !ShiftIdHasDatePrefix(shiftId));
            if (!inDay)
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(shiftId) && excluded.Contains(shiftId))
            {
                continue;
            }

            shiftIndex.TryGetValue(shiftId, out (string DriverId, string TaxiId) shiftInfo);
            string driverId = shiftInfo.DriverId;
            string taxiId = shiftInfo.TaxiId;
            string driverName = driverNames.TryGetValue(driverId, out string? name) ? name : driverId;

            decimal expected = GetDecimal(paymentDocument.Data, "expectedBoundary") + GetDecimal(paymentDocument.Data, "lateFees");
            decimal paid = GetDecimal(paymentDocument.Data, "amountPaid");
            SettlementStatus status = GetStatus(paymentDocument.Data, paid, expected);
            string method = NormalizePaymentMethod(GetString(paymentDocument.Data, "paymentMethod"));
            string category = GetString(paymentDocument.Data, "paymentCategory", "Boundary");

            entries.Add(new LedgerItemModel
            {
                ShiftId = shiftId,
                DriverId = driverId,
                DriverName = string.IsNullOrWhiteSpace(driverName) ? "Unknown Driver" : driverName,
                TaxiId = taxiId,
                PlateNumber = taxiId,
                ExpectedAmount = expected,
                AmountPaid = paid,
                Status = status,
                PaymentMethodLabel = method,
                CategoryLabel = category,
                IsOtherPaymentEntry = string.Equals(category, "Debt", StringComparison.OrdinalIgnoreCase),
            });
        }

        foreach (var adjustmentDocument in adjustments.Documents)
        {
            if (adjustmentDocument.Data == null)
            {
                continue;
            }

            DateTime? timestampUtc = ToUtc(GetDate(adjustmentDocument.Data, "timestamp"));
            if (!timestampUtc.HasValue || timestampUtc.Value < dayStartUtc || timestampUtc.Value >= dayEndUtc)
            {
                continue;
            }

            decimal amount = GetDecimal(adjustmentDocument.Data, "amount");
            if (amount >= 0)
            {
                continue;
            }

            string driverId = GetString(adjustmentDocument.Data, "driverId");
            string driverName = driverNames.TryGetValue(driverId, out string? name) ? name : driverId;
            string method = NormalizePaymentMethod(GetString(adjustmentDocument.Data, "paymentMethod", "E-Wallet"));

            entries.Add(new LedgerItemModel
            {
                ShiftId = string.Empty,
                DriverId = driverId,
                DriverName = string.IsNullOrWhiteSpace(driverName) ? "Unknown Driver" : driverName,
                TaxiId = string.Empty,
                PlateNumber = string.Empty,
                ExpectedAmount = 0m,
                AmountPaid = Math.Abs(amount),
                Status = SettlementStatus.Cleared,
                PaymentMethodLabel = method,
                CategoryLabel = "Debt",
                IsOtherPaymentEntry = true,
            });
        }

        return entries
            .OrderByDescending(entry => entry.AmountPaid)
            .ThenBy(entry => entry.DriverName)
            .ToList();
    }

    public async Task<SettleDebtResult> SettleDebtAsync(string driverId, decimal amountReceived, string paymentMethod)
    {
        if (string.IsNullOrWhiteSpace(driverId))
        {
            return new SettleDebtResult { Ok = false, ErrorMessage = "Missing driver." };
        }

        if (amountReceived <= 0)
        {
            return new SettleDebtResult { Ok = false, ErrorMessage = "Amount received must be greater than zero." };
        }

        try
        {
            var shiftSnapshot = await CrossFirebaseFirestore.Current
                .GetCollection("shifts")
                .WhereEqualsTo("driverId", driverId)
                .GetDocumentsAsync<Dictionary<string, object>>();

            HashSet<string> driverShiftIds = shiftSnapshot.Documents
                .Where(document => document.Data != null)
                .Select(document => GetString(document.Data, "shiftId", document.Reference.Id))
                .Where(shiftId => !string.IsNullOrWhiteSpace(shiftId))
                .ToHashSet();

            var paymentSnapshot = await CrossFirebaseFirestore.Current
                .GetCollection("boundary_payments")
                .GetDocumentsAsync<Dictionary<string, object>>();

            List<(string Id, Dictionary<string, object> Data)> outstanding = paymentSnapshot.Documents
                .Where(document => document.Data != null)
                .Select(document => (Id: document.Reference.Id, Data: document.Data!))
                .Where(document => driverShiftIds.Contains(GetString(document.Data, "shiftId")))
                .Where(document => !GetString(document.Data, "paymentStatus").Equals("Paid", StringComparison.OrdinalIgnoreCase))
                .OrderBy(document => GetDate(document.Data, "timestamp") ?? DateTime.MinValue)
                .ToList();

            decimal remaining = amountReceived;
            foreach ((string id, Dictionary<string, object> data) in outstanding)
            {
                if (remaining <= 0)
                {
                    break;
                }

                decimal expected = GetDecimal(data, "expectedBoundary") + GetDecimal(data, "lateFees");
                decimal paid = GetDecimal(data, "amountPaid");
                decimal shortfall = Math.Max(0, expected - paid);
                if (shortfall <= 0)
                {
                    continue;
                }

                decimal apply = Math.Min(remaining, shortfall);
                decimal newAmountPaid = paid + apply;

                var updated = new Dictionary<object, object>
                {
                    ["shiftId"] = GetString(data, "shiftId"),
                    ["expectedBoundary"] = (double)GetDecimal(data, "expectedBoundary"),
                    ["lateFees"] = (double)GetDecimal(data, "lateFees"),
                    ["amountPaid"] = (double)newAmountPaid,
                    ["paymentMethod"] = string.Equals(paymentMethod, "EWallet", StringComparison.OrdinalIgnoreCase) ? "E-Wallet" : "Cash",
                    ["paymentStatus"] = newAmountPaid >= expected ? "Paid" : "Partial",
                    ["paymentCategory"] = "Debt",
                    ["timestamp"] = DateTime.UtcNow,
                };

                string reference = GetString(data, "referenceNumber");
                if (!string.IsNullOrWhiteSpace(reference))
                {
                    updated["referenceNumber"] = reference;
                }

                string receiptPhoto = GetString(data, "ePayReceiptPhoto");
                if (!string.IsNullOrWhiteSpace(receiptPhoto))
                {
                    updated["ePayReceiptPhoto"] = receiptPhoto;
                }

                await CrossFirebaseFirestore.Current
                    .GetCollection("boundary_payments")
                    .GetDocument(id)
                    .SetDataAsync(updated);

                remaining -= apply;
            }

            if (remaining > 0)
            {
                var credit = new Dictionary<object, object>
                {
                    ["driverId"] = driverId,
                    ["amount"] = (double)(-remaining),
                    ["reason"] = "Automatic credit from a lump-sum debt settlement.",
                    ["paymentMethod"] = string.Equals(paymentMethod, "EWallet", StringComparison.OrdinalIgnoreCase) ? "E-Wallet" : "Cash",
                    ["paymentCategory"] = "Debt",
                    ["timestamp"] = DateTime.UtcNow,
                };

                await CrossFirebaseFirestore.Current
                    .GetCollection("debt_adjustments")
                    .AddDocumentAsync(credit);
            }

            decimal newTotal = await GetOutstandingDebtForDriverAsync(driverId);
            return new SettleDebtResult
            {
                Ok = true,
                RemainingDebt = newTotal,
                UnallocatedAmount = 0,
            };
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Mobile settle debt error: {ex.Message}");
            return new SettleDebtResult { Ok = false, ErrorMessage = "Could not save this settlement. Please try again." };
        }
    }

    private async Task<decimal> GetOutstandingDebtForDriverAsync(string driverId)
    {
        var shiftSnapshot = await CrossFirebaseFirestore.Current
            .GetCollection("shifts")
            .WhereEqualsTo("driverId", driverId)
            .GetDocumentsAsync<Dictionary<string, object>>();

        HashSet<string> driverShiftIds = shiftSnapshot.Documents
            .Where(document => document.Data != null)
            .Select(document => GetString(document.Data, "shiftId", document.Reference.Id))
            .Where(shiftId => !string.IsNullOrWhiteSpace(shiftId))
            .ToHashSet();

        var paymentSnapshot = await CrossFirebaseFirestore.Current
            .GetCollection("boundary_payments")
            .GetDocumentsAsync<Dictionary<string, object>>();

        decimal outstandingFromPayments = paymentSnapshot.Documents
            .Where(document => document.Data != null)
            .Select(document => document.Data!)
            .Where(data => driverShiftIds.Contains(GetString(data, "shiftId")))
            .Where(data => !GetString(data, "paymentStatus").Equals("Paid", StringComparison.OrdinalIgnoreCase))
            .Sum(data => Math.Max(0, GetDecimal(data, "expectedBoundary") + GetDecimal(data, "lateFees") - GetDecimal(data, "amountPaid")));

        var adjustmentSnapshot = await CrossFirebaseFirestore.Current
            .GetCollection("debt_adjustments")
            .WhereEqualsTo("driverId", driverId)
            .GetDocumentsAsync<Dictionary<string, object>>();

        decimal adjustmentDebt = adjustmentSnapshot.Documents
            .Where(document => document.Data != null)
            .Sum(document => GetDecimal(document.Data, "amount"));

        return Math.Max(0, outstandingFromPayments + adjustmentDebt);
    }

    private static async Task<DocumentData?> GetPaymentAsync(string shiftId)
    {
        var snapshot = await CrossFirebaseFirestore.Current
            .GetCollection("boundary_payments")
            .WhereEqualsTo("shiftId", shiftId)
            .GetDocumentsAsync<Dictionary<string, object>>();
        var document = snapshot.Documents.FirstOrDefault(document => document.Data != null);
        if (document != null && document.Data != null)
        {
            return new DocumentData(document.Reference.Id, document.Data);
        }

        // Fallback for legacy/sparse docs where shiftId was omitted but deterministic doc ID was used.
        var direct = await CrossFirebaseFirestore.Current
            .GetCollection("boundary_payments")
            .GetDocument($"{shiftId}_PAY")
            .GetDocumentSnapshotAsync<Dictionary<string, object>>();

        return direct?.Data == null ? null : new DocumentData(direct.Reference.Id, direct.Data);
    }

    private static async Task<decimal> GetDefaultBoundaryRateAsync()
    {
        var document = await CrossFirebaseFirestore.Current
            .GetCollection("system_configs")
            .GetDocument("global")
            .GetDocumentSnapshotAsync<Dictionary<string, object>>();
        return document?.Data == null
            ? DefaultBoundaryRate
            : Math.Max(0, GetDecimal(document.Data, "defaultBoundaryRate"));
    }

    private static SettlementStatus GetStatus(Dictionary<string, object>? payment, decimal paid, decimal expected)
    {
        string status = GetString(payment, "paymentStatus");
        if (status.Equals("Paid", StringComparison.OrdinalIgnoreCase) || (paid > 0 && paid >= expected))
        {
            return SettlementStatus.Cleared;
        }

        return paid > 0 ? SettlementStatus.Partial : SettlementStatus.Waiting;
    }

    private static string GetString(Dictionary<string, object>? data, string key, string fallback = "")
    {
        return data != null && data.TryGetValue(key, out object? value) && value != null
            ? Convert.ToString(value, CultureInfo.InvariantCulture) ?? fallback
            : fallback;
    }

    private static decimal GetDecimal(Dictionary<string, object>? data, string key)
    {
        if (data == null || !data.TryGetValue(key, out object? value) || value == null)
        {
            return 0m;
        }

        return value switch
        {
            decimal number => number,
            double number => (decimal)number,
            float number => (decimal)number,
            long number => number,
            int number => number,
            _ => decimal.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Any, CultureInfo.InvariantCulture, out decimal parsed) ? parsed : 0m,
        };
    }

    private static DateTime? GetDate(Dictionary<string, object>? data, string key)
    {
        if (data == null || !data.TryGetValue(key, out object? value) || value == null)
        {
            return null;
        }

        if (value is DateTime dateTime)
        {
            return FirestoreDateTimeFix.Apply(dateTime);
        }

        if (value is DateTimeOffset dateTimeOffset)
        {
            return dateTimeOffset.UtcDateTime;
        }

        if (value is long longValue)
        {
            return DateTimeOffset.FromUnixTimeMilliseconds(longValue).UtcDateTime;
        }

        if (value is int intValue)
        {
            return DateTimeOffset.FromUnixTimeMilliseconds(intValue).UtcDateTime;
        }

        if (value is double doubleValue)
        {
            return DateTimeOffset.FromUnixTimeMilliseconds((long)doubleValue).UtcDateTime;
        }

        DateTime? reflectedTimestamp = TryConvertFirestoreTimestampObject(value);
        if (reflectedTimestamp.HasValue)
        {
            return reflectedTimestamp.Value;
        }

        return DateTime.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTime parsed)
            ? parsed.ToUniversalTime()
            : null;
    }

    private static (DateTime StartUtc, DateTime EndUtc, DateTime LocalDate) GetUtcBoundsForLocalDay(DateTime reference)
    {
        DateTime localReference = reference.Kind == DateTimeKind.Utc ? reference.ToLocalTime() : reference;
        DateTime localDay = localReference.Date;
        DateTime startLocal = DateTime.SpecifyKind(localDay, DateTimeKind.Local);
        DateTime endLocal = startLocal.AddDays(1);
        return (startLocal.ToUniversalTime(), endLocal.ToUniversalTime(), localDay);
    }

    private static DateTime? ToUtc(DateTime? value)
    {
        if (!value.HasValue)
        {
            return null;
        }

        DateTime date = value.Value;
        return date.Kind switch
        {
            DateTimeKind.Utc => date,
            DateTimeKind.Local => date.ToUniversalTime(),
            _ => DateTime.SpecifyKind(date, DateTimeKind.Local).ToUniversalTime(),
        };
    }

    private static bool IsWithinUtcWindow(DateTime? utcValue, DateTime startUtc, DateTime endUtc)
    {
        return utcValue.HasValue && utcValue.Value >= startUtc && utcValue.Value < endUtc;
    }

    private static bool IsShiftIdOnLocalDay(string shiftId, DateTime localDate)
    {
        if (!TryGetDateFromShiftId(shiftId, out DateTime parsedDate))
        {
            return false;
        }

        return parsedDate.Date == localDate.Date;
    }

    private static bool ShiftIdHasDatePrefix(string shiftId)
    {
        return TryGetDateFromShiftId(shiftId, out _);
    }

    private static bool TryGetDateFromShiftId(string shiftId, out DateTime parsedDate)
    {
        parsedDate = default;
        if (string.IsNullOrWhiteSpace(shiftId))
        {
            return false;
        }

        // Seed/live IDs commonly look like SHIFT_2026091901 or SHIFT_TEST_001.
        int underscoreIndex = shiftId.IndexOf('_');
        if (underscoreIndex < 0 || shiftId.Length < underscoreIndex + 9)
        {
            return false;
        }

        string tail = shiftId[(underscoreIndex + 1)..];
        if (tail.Length < 8)
        {
            return false;
        }

        string datePart = tail[..8];
        if (!datePart.All(char.IsDigit))
        {
            return false;
        }

        if (!int.TryParse(datePart[..4], out int year)
            || !int.TryParse(datePart.Substring(4, 2), out int month)
            || !int.TryParse(datePart.Substring(6, 2), out int day))
        {
            return false;
        }

        try
        {
            parsedDate = new DateTime(year, month, day);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string GetShiftIdFromPaymentDoc(string paymentDocId, Dictionary<string, object>? data)
    {
        string shiftId = GetString(data, "shiftId");
        if (!string.IsNullOrWhiteSpace(shiftId))
        {
            return shiftId;
        }

        const string paySuffix = "_PAY";
        if (!string.IsNullOrWhiteSpace(paymentDocId)
            && paymentDocId.EndsWith(paySuffix, StringComparison.OrdinalIgnoreCase)
            && paymentDocId.Length > paySuffix.Length)
        {
            return paymentDocId[..^paySuffix.Length];
        }

        return paymentDocId;
    }

    private static DateTime? TryConvertFirestoreTimestampObject(object value)
    {
        Type valueType = value.GetType();
        var secondsProperty = valueType.GetProperty("Seconds");
        if (secondsProperty?.GetValue(value) is long seconds)
        {
            long nanos = 0;
            var nanosProperty = valueType.GetProperty("Nanoseconds");
            if (nanosProperty?.GetValue(value) is int nanosInt)
            {
                nanos = nanosInt;
            }

            var timestamp = DateTimeOffset.FromUnixTimeSeconds(seconds).AddTicks(nanos / 100);
            return timestamp.UtcDateTime;
        }

        var millisecondsProperty = valueType.GetProperty("Milliseconds");
        if (millisecondsProperty?.GetValue(value) is long milliseconds)
        {
            return DateTimeOffset.FromUnixTimeMilliseconds(milliseconds).UtcDateTime;
        }

        return null;
    }

    private static string NormalizePaymentMethod(string raw)
    {
        if (raw.Equals("E-Wallet", StringComparison.OrdinalIgnoreCase) || raw.Equals("EWallet", StringComparison.OrdinalIgnoreCase))
        {
            return "E-WALLET";
        }

        return "CASH";
    }

    private static string NormalizeRole(string role)
    {
        return string.IsNullOrWhiteSpace(role)
            ? string.Empty
            : role.Trim().Replace("_", string.Empty).Replace("-", string.Empty).Replace(" ", string.Empty);
    }

    private readonly record struct DocumentData(string ReferenceId, Dictionary<string, object> Data)
    {
        public string Reference => ReferenceId;
    }
}
