using System.Globalization;

namespace LARGA.SharedCore;

/// <summary>
/// One CSV cell for the dashboard's exports (Audit Logs, Financial Ledger, Fleet report), safe to open in
/// Excel or Google Sheets (audit G-02).
///
/// Every cell is quoted (commas, quotes and line breaks in names or notes stay inside it). A cell that
/// starts with = + - @ tab or CR would otherwise be run as a formula - and driver-typed text (names,
/// defect titles, fuel notes) reaches these files - so it gets a leading apostrophe, which spreadsheets
/// show as plain text. Plain numbers such as "-500.00" are left alone so amounts stay numbers.
/// </summary>
public static class CsvRules
{
    private const string FormulaStarts = "=+-@\t\r";

    public static string Escape(string? value)
    {
        string text = value ?? string.Empty;
        if (text.Length > 0 && FormulaStarts.Contains(text[0]) && !IsPlainNumber(text))
        {
            text = "'" + text;
        }

        return "\"" + text.Replace("\"", "\"\"") + "\"";
    }

    private static bool IsPlainNumber(string text) =>
        decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out _);
}
