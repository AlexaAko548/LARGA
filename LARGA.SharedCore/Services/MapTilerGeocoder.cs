using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace LARGA.SharedCore.Services;

/// <summary>
/// Reverse geocoding through the MapTiler Geocoding API (the same MapTiler account the mobile
/// map tiles use): GPS coordinates → a street address for SOS alerts. Results are cached per
/// ~1 m (5 decimal places), so the polling dispatch page and the alert lists don't re-query.
/// Returns null when there is no key, no location (0/0), or the lookup fails - callers then
/// show the coordinates instead.
/// </summary>
public class MapTilerGeocoder
{
    public const string SourceMapTiler = "maptiler";
    public const string SourceCoordinates = "coords";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(5) };

    private readonly string? _apiKey;
    private readonly ConcurrentDictionary<string, string> _cache = new();

    // A lookup that just failed isn't retried for a while, so a MapTiler outage doesn't make every
    // refresh (or every alert on a page) wait out the timeout again.
    private static readonly TimeSpan FailureBackoff = TimeSpan.FromMinutes(2);
    private readonly ConcurrentDictionary<string, DateTime> _failedUntil = new();

    public MapTilerGeocoder(string? apiKey)
    {
        _apiKey = string.IsNullOrWhiteSpace(apiKey) || apiKey.Contains("YOUR_", StringComparison.Ordinal) ? null : apiKey.Trim();
    }

    public bool IsConfigured => _apiKey is not null;

    /// <summary>"14.59951, 120.98422" - what's shown when there's no address.</summary>
    public static string FormatCoordinates(double latitude, double longitude) =>
        string.Create(CultureInfo.InvariantCulture, $"{latitude:F5}, {longitude:F5}");

    public async Task<string?> ReverseGeocodeAsync(double latitude, double longitude, CancellationToken cancellationToken = default)
    {
        if (_apiKey is null || (latitude == 0 && longitude == 0))
        {
            return null;
        }

        string key = string.Create(CultureInfo.InvariantCulture, $"{latitude:F5},{longitude:F5}");
        if (_cache.TryGetValue(key, out string? cached))
        {
            return cached;
        }
        if (_failedUntil.TryGetValue(key, out DateTime until) && DateTime.UtcNow < until)
        {
            return null;
        }

        try
        {
            // MapTiler takes longitude first.
            string url = string.Create(CultureInfo.InvariantCulture,
                $"https://api.maptiler.com/geocoding/{longitude:F6},{latitude:F6}.json?key={Uri.EscapeDataString(_apiKey)}&limit=1&language=en");
            using HttpResponseMessage response = await Http.GetAsync(url, cancellationToken);
            string? address = null;
            if (response.IsSuccessStatusCode)
            {
                address = ParsePlaceName(await response.Content.ReadAsStringAsync(cancellationToken));
            }

            if (address is not null)
            {
                _cache[key] = address;
                _failedUntil.TryRemove(key, out _);
            }
            else
            {
                _failedUntil[key] = DateTime.UtcNow + FailureBackoff;
            }
            return address;
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            // Offline, timeout, bad JSON - the caller falls back to coordinates.
            _failedUntil[key] = DateTime.UtcNow + FailureBackoff;
            return null;
        }
    }

    /// <summary>features[0].place_name of a MapTiler geocoding response, or null.</summary>
    public static string? ParsePlaceName(string json)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("features", out JsonElement features)
                || features.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            foreach (JsonElement feature in features.EnumerateArray())
            {
                foreach (string property in new[] { "place_name_en", "place_name" })
                {
                    if (feature.TryGetProperty(property, out JsonElement name)
                        && name.ValueKind == JsonValueKind.String
                        && !string.IsNullOrWhiteSpace(name.GetString()))
                    {
                        return name.GetString()!.Trim();
                    }
                }
            }
        }
        catch (JsonException)
        {
        }
        return null;
    }
}
