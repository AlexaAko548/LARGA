using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using LARGA.SharedCore;
using LARGA.SharedCore.Models.Dashboard;
using LARGA.SharedCore.Services;
using SkiaSharp;

namespace LARGA.MobileApp.Services;

/// <summary>
/// The driver Payment History screen's Export button (test gap: "export is currently not working").
/// Builds the same kind of <see cref="ReportDocument"/> ManagerWeb's reports use, so the CSV comes out
/// of the exact same <see cref="FleetReportingService.ToCsv"/> the web downloads. The PDF is drawn with
/// SkiaSharp (SKDocument.CreatePdf) rather than QuestPDF - QuestPDF's static Settings initializer throws
/// on Android (it isn't built for mobile AOT runtimes and crashed the whole app on launch), while
/// SkiaSharp is already a proven dependency here (it renders the Live Fleet map on every platform).
/// </summary>
public static class PaymentHistoryExportService
{
    private static readonly CultureInfo Numbers = CultureInfo.InvariantCulture;
    private static readonly int[] MoneyColumnIndexes = { 3, 4, 6 };

    /// <summary>Every paid-on shift whose payment falls in [fromUtc, toUtc], newest first - the same rows the
    /// screen already lists, just with the columns testing asked for (Date, Shift ID, Unit, Amount Due, Amount
    /// Paid, Payment Type, Balance).</summary>
    public static ReportDocument BuildReport(IEnumerable<DriverDebtCalculator.ShiftEntry> shifts, DateTime fromUtc, DateTime toUtc)
    {
        List<object?[]> rows = shifts
            .Where(s => s.HasPayment && s.PaymentTimestampUtc is DateTime paidAt && paidAt >= fromUtc && paidAt <= toUtc)
            .OrderByDescending(s => s.PaymentTimestampUtc)
            .Select(s => new object?[]
            {
                s.PaymentTimestampUtc!.Value.ToPhilippineTime().ToString("yyyy-MM-dd HH:mm", Numbers),
                s.ShiftId,
                s.TaxiId,
                s.Expected,
                s.Paid,
                PaymentTypeLabel(s.PaymentStatus),
                s.Outstanding,
            })
            .ToList();

        return new ReportDocument
        {
            Title = "Payment History",
            Tables = new List<ReportTable>
            {
                new()
                {
                    Headers = new[] { "Date", "Shift ID", "Unit", "Amount Due", "Amount Paid", "Payment Type", "Balance" },
                    Rows = rows,
                    MoneyColumns = new HashSet<int>(MoneyColumnIndexes),
                },
            },
        };
    }

    private static string PaymentTypeLabel(string paymentStatus) => paymentStatus switch
    {
        "Paid" => "Full",
        "Partial" => "Partial",
        _ => "Unpaid",
    };

    public static string ToCsv(ReportDocument report) => FleetReportingService.ToCsv(report);

    // A4 landscape in points (1/72 inch) - seven columns read more comfortably wide, same call ManagerWeb's
    // PdfReportRenderer makes for its wider reports.
    private const float PageWidth = 842f;
    private const float PageHeight = 595f;
    private const float Margin = 36f;
    private const float RowHeight = 20f;
    private const float HeaderRowHeight = 22f;
    private const float FooterSpace = 24f;

    public static byte[] ToPdf(ReportDocument report, DateTime fromDatePh, DateTime toDatePh, string generatedBy)
    {
        DateTime generatedAtPh = PhilippineTime.Now;
        string range = fromDatePh.Date == toDatePh.Date
            ? fromDatePh.ToString("MMMM d, yyyy", Numbers)
            : $"{fromDatePh.ToString("MMMM d, yyyy", Numbers)} - {toDatePh.ToString("MMMM d, yyyy", Numbers)}";

        using var stream = new MemoryStream();
        using (SKDocument document = SKDocument.CreatePdf(stream))
        {
            DrawPages(document, report.Title, range, generatedBy, generatedAtPh, report.Tables[0]);
        }
        return stream.ToArray();
    }

    private static void DrawPages(SKDocument document, string title, string range, string generatedBy, DateTime generatedAtPh, ReportTable table)
    {
        float contentWidth = PageWidth - Margin * 2;
        int columns = table.Headers.Count;
        float colWidth = contentWidth / columns;

        using SKTypeface regular = SKTypeface.FromFamilyName("sans-serif", SKFontStyleWeight.Normal, SKFontStyleWidth.Normal, SKFontStyleSlant.Upright);
        using SKTypeface bold = SKTypeface.FromFamilyName("sans-serif", SKFontStyleWeight.Bold, SKFontStyleWidth.Normal, SKFontStyleSlant.Upright);
        using var titleFont = new SKFont(bold, 16);
        using var subFont = new SKFont(regular, 10);
        using var headerFont = new SKFont(bold, 9);
        using var cellFont = new SKFont(regular, 9);
        using var textPaint = new SKPaint { Color = new SKColor(45, 45, 45), IsAntialias = true };
        using var grayPaint = new SKPaint { Color = new SKColor(110, 110, 110), IsAntialias = true };
        using var headerBgPaint = new SKPaint { Color = new SKColor(232, 232, 232) };
        using var linePaint = new SKPaint { Color = new SKColor(215, 215, 215), StrokeWidth = 0.75f };

        int rowIndex = 0;
        int pageNumber = 0;

        do
        {
            pageNumber++;
            SKCanvas canvas = document.BeginPage(PageWidth, PageHeight);
            float y = Margin;

            if (pageNumber == 1)
            {
                canvas.DrawText("LARGA / BLM Taxi", Margin, y + 10, SKTextAlign.Left, subFont, grayPaint);
                y += 20;
                canvas.DrawText(title, Margin, y + 14, SKTextAlign.Left, titleFont, textPaint);
                y += 24;
                canvas.DrawText($"Date range: {range}", Margin, y + 10, SKTextAlign.Left, subFont, textPaint);
                y += 15;
                canvas.DrawText($"Generated by: {generatedBy}", Margin, y + 10, SKTextAlign.Left, subFont, textPaint);
                y += 15;
                canvas.DrawText($"Generated at: {generatedAtPh:MMMM d, yyyy h:mm tt} (Philippine time, UTC+8)", Margin, y + 10, SKTextAlign.Left, subFont, textPaint);
                y += 18;
                canvas.DrawLine(Margin, y, Margin + contentWidth, y, linePaint);
                y += 12;
            }
            else
            {
                y += 4;
            }

            canvas.DrawRect(Margin, y, contentWidth, HeaderRowHeight, headerBgPaint);
            for (int c = 0; c < columns; c++)
            {
                DrawCell(canvas, table.Headers[c], Margin + c * colWidth, y, colWidth, HeaderRowHeight, headerFont, textPaint, table.MoneyColumns.Contains(c));
            }
            y += HeaderRowHeight;

            while (rowIndex < table.Rows.Count && y + RowHeight <= PageHeight - Margin - FooterSpace)
            {
                object?[] row = table.Rows[rowIndex];
                for (int c = 0; c < columns; c++)
                {
                    object? value = c < row.Length ? row[c] : null;
                    string text = table.MoneyColumns.Contains(c) ? FormatPeso(value) : value?.ToString() ?? string.Empty;
                    DrawCell(canvas, text, Margin + c * colWidth, y, colWidth, RowHeight, cellFont, textPaint, table.MoneyColumns.Contains(c));
                }
                canvas.DrawLine(Margin, y + RowHeight, Margin + contentWidth, y + RowHeight, linePaint);
                y += RowHeight;
                rowIndex++;
            }

            if (table.Rows.Count == 0)
            {
                canvas.DrawText("No payments in this date range.", Margin, y + 16, SKTextAlign.Left, subFont, grayPaint);
            }

            string footer = $"Page {pageNumber}";
            canvas.DrawText(footer, PageWidth / 2, PageHeight - Margin / 2, SKTextAlign.Center, subFont, grayPaint);

            document.EndPage();
        }
        while (rowIndex < table.Rows.Count);
    }

    private static void DrawCell(SKCanvas canvas, string text, float x, float y, float width, float height, SKFont font, SKPaint paint, bool alignRight)
    {
        const float padding = 4f;
        float baseline = y + (height / 2) + (font.Size / 2.8f);
        if (alignRight)
        {
            canvas.DrawText(text, x + width - padding, baseline, SKTextAlign.Right, font, paint);
        }
        else
        {
            canvas.DrawText(text, x + padding, baseline, SKTextAlign.Left, font, paint);
        }
    }

    private static string FormatPeso(object? value)
    {
        if (value is null) return string.Empty;
        try
        {
            decimal amount = Convert.ToDecimal(value, Numbers);
            return (amount < 0 ? "-₱" : "₱") + Math.Abs(amount).ToString("N2", Numbers);
        }
        catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException)
        {
            return value.ToString() ?? string.Empty;
        }
    }

    public static string FileBaseName(DateTime fromDatePh, DateTime toDatePh) =>
        $"PaymentHistory_{fromDatePh:yyyyMMdd}-{toDatePh:yyyyMMdd}";
}
