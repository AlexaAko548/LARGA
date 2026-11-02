using System;
using System.Linq;
using System.Reflection;
using Google.Cloud.Firestore;
using LARGA.Shared.Models.Entities;

namespace LARGA.SharedCore;

/// <summary>
/// Reads system_configs/global into a <see cref="SystemConfig"/> one field at a time (ManagerWeb,
/// Admin SDK). That document is edited by hand in the Firebase console, and ConvertTo&lt;SystemConfig&gt;
/// throws for the whole document over one field of the wrong type (BRP-014: fuelExpectedKmPerLiter
/// saved as "" put the web ledger back on the ₱800 default while the phones, which read single
/// fields, used the configured ₱900). Here a missing, null or malformed field keeps its default and
/// every other field is still read.
/// </summary>
public static class SystemConfigReader
{
    private static readonly MethodInfo GetValueByPath = typeof(DocumentSnapshot).GetMethods()
        .Single(m => m.Name == nameof(DocumentSnapshot.GetValue)
                     && m.IsGenericMethodDefinition
                     && m.GetParameters().Select(p => p.ParameterType).SequenceEqual(new[] { typeof(string) }));

    private static readonly (PropertyInfo Property, string Field, MethodInfo Read)[] Fields = typeof(SystemConfig)
        .GetProperties()
        .Select(p => (Property: p, Attribute: p.GetCustomAttribute<FirestorePropertyAttribute>()))
        .Where(x => x.Attribute is not null && x.Property.CanWrite)
        .Select(x => (x.Property, x.Attribute!.Name ?? x.Property.Name, GetValueByPath.MakeGenericMethod(x.Property.PropertyType)))
        .ToArray();

    /// <summary>The settings in <paramref name="snapshot"/>, with defaults for anything missing or
    /// unreadable; all defaults when the document doesn't exist. <paramref name="onBadField"/> is
    /// told about each field that was there but couldn't be read.</summary>
    public static SystemConfig Read(DocumentSnapshot snapshot, Action<string, Exception>? onBadField = null)
    {
        var config = new SystemConfig();
        if (!snapshot.Exists) return config;

        config.ConfigId = snapshot.Id;
        foreach ((PropertyInfo property, string field, MethodInfo read) in Fields)
        {
            if (!snapshot.ContainsField(field)) continue;
            try
            {
                object? value = read.Invoke(snapshot, new object[] { field });
                if (value is not null) property.SetValue(config, value);
            }
            catch (Exception ex)
            {
                onBadField?.Invoke(field, ex is TargetInvocationException { InnerException: Exception inner } ? inner : ex);
            }
        }

        return config;
    }
}
