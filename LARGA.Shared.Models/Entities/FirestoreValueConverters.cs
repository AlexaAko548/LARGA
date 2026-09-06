using System;
using Google.Cloud.Firestore;

namespace LARGA.Shared.Models.Entities;

/// <summary>
/// Google.Cloud.Firestore has no built-in converter for System.Decimal: it can write one
/// (a decimal serializes fine as a double) but throws "Unable to create converter for
/// type System.Decimal" the moment anything tries to read one back with ConvertTo&lt;T&gt;().
/// Round-trips decimal through Firestore's native int64/double wire types instead.
///
/// Apply to every decimal-typed [FirestoreProperty] via
/// ConverterType = typeof(DecimalConverter).
/// </summary>
public class DecimalConverter : IFirestoreConverter<decimal>
{
    public object ToFirestore(decimal value) => (double)value;

    public decimal FromFirestore(object? value) => value switch
    {
        decimal d => d,
        double d => (decimal)d,
        long l => l,
        int i => i,
        null => 0m,
        _ => Convert.ToDecimal(value),
    };
}

/// <summary>
/// Tolerates a string-typed field (e.g. a phone number) being hand-entered into the
/// Firestore console as a number - a recurring mistake, since values like phone numbers
/// look numeric but must stay strings (to preserve a leading "0" and match the ERD's
/// VARCHAR type). Coerces any numeric value to its string form instead of throwing.
/// </summary>
public class LenientStringConverter : IFirestoreConverter<string>
{
    public object ToFirestore(string value) => value;

    public string FromFirestore(object? value) => value switch
    {
        string s => s,
        null => string.Empty,
        _ => value.ToString() ?? string.Empty,
    };
}
