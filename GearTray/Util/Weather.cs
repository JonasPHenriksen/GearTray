using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using GearTray.Effects;

namespace GearTray.Util;

/// <summary>A place found by name: a label to show ("Copenhagen, Capital Region, Denmark") and its coordinates.</summary>
public sealed record WeatherPlace(string Label, double Latitude, double Longitude);

/// <summary>
/// Weather from Open-Meteo (open-meteo.com: free, no account or key), over HTTPS. The place search runs when a place is
/// typed on the Headset tab; the current weather is asked for every 15 minutes by the base-station screen. Only names
/// and numbers are picked out of the answers.
/// </summary>
public static class Weather
{
    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        // At most 10 s and 1 MB per answer (a real one is under 1 KB; .NET's default would take up to 2 GB), so a
        // misbehaving server can't make GearTray wait or hold memory.
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10), MaxResponseContentBufferSize = 1 << 20 };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("GearTray");
        return http;
    }

    /// <summary>The best match for a town or city name, or null when there is none.</summary>
    public static async Task<WeatherPlace?> FindPlace(string name)
    {
        string url = "https://geocoding-api.open-meteo.com/v1/search?count=1&language=en&format=json&name=" + Uri.EscapeDataString(name.Trim());
        using var doc = JsonDocument.Parse(await Http.GetStringAsync(url));
        if (!doc.RootElement.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array || results.GetArrayLength() == 0)
            return null;
        var r = results[0];
        string label = string.Join(", ", new[] { Text(r, "name"), Text(r, "admin1"), Text(r, "country") }.Where(s => s.Length > 0).Distinct());
        return new WeatherPlace(label, r.GetProperty("latitude").GetDouble(), r.GetProperty("longitude").GetDouble());
    }

    /// <summary>The weather there now: temperature in °C, WMO weather code, and whether it's day.</summary>
    public static async Task<WeatherNow> Current(double latitude, double longitude)
    {
        string url = string.Create(CultureInfo.InvariantCulture,
            $"https://api.open-meteo.com/v1/forecast?latitude={latitude:0.####}&longitude={longitude:0.####}&current=temperature_2m,weather_code,is_day&timezone=auto");
        using var doc = JsonDocument.Parse(await Http.GetStringAsync(url));
        var now = doc.RootElement.GetProperty("current");
        return new WeatherNow(now.GetProperty("temperature_2m").GetDouble(), now.GetProperty("weather_code").GetInt32(),
            now.GetProperty("is_day").GetInt32() == 1);
    }

    private static string Text(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()!.Trim() : "";
}
