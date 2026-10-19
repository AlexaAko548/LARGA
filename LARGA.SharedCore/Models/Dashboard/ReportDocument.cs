using System;
using System.Collections.Generic;

namespace LARGA.SharedCore.Models.Dashboard;

/// <summary>
/// One generated report (Dashboard > Generate Reports, Audit Logs export) as plain tables, so the
/// CSV and the PDF (ANA-007 / ANA-012) are built from exactly the same data.
/// </summary>
public sealed class ReportDocument
{
    public string Title { get; init; } = string.Empty;

    public List<ReportTable> Tables { get; init; } = new();
}

public sealed class ReportTable
{
    /// <summary>Shown above the table when a report has more than one (e.g. "Fuel Logs").</summary>
    public string? Heading { get; init; }

    public IReadOnlyList<string> Headers { get; init; } = Array.Empty<string>();

    /// <summary>Cell values as the CSV has always written them (ToString()).</summary>
    public IReadOnlyList<object?[]> Rows { get; init; } = Array.Empty<object?[]>();

    /// <summary>Column indexes holding peso amounts - shown as ₱1,234.00 in the PDF.</summary>
    public IReadOnlySet<int> MoneyColumns { get; init; } = new HashSet<int>();

    /// <summary>Optional relative column widths for the PDF (one per header); equal when null.</summary>
    public IReadOnlyList<float>? RelativeWidths { get; init; }
}
