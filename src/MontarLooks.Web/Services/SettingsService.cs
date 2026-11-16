using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using MontarLooks.Web.Data;
using MontarLooks.Web.Models;

namespace MontarLooks.Web.Services;

/// <summary>Stores the user's city (and future preferences) in the database.</summary>
public class SettingsService(AppDbContext db, IConfiguration config)
{
    private const string CityKey = "city.name";
    private const string LatKey = "city.lat";
    private const string LonKey = "city.lon";

    public async Task<CityLocation> GetCityAsync()
    {
        var all = await db.Settings.AsNoTracking().ToDictionaryAsync(s => s.Key, s => s.Value);
        if (all.TryGetValue(CityKey, out var name)
            && all.TryGetValue(LatKey, out var lat)
            && all.TryGetValue(LonKey, out var lon))
        {
            return new CityLocation(name, null, null,
                double.Parse(lat, CultureInfo.InvariantCulture),
                double.Parse(lon, CultureInfo.InvariantCulture));
        }

        var section = config.GetSection("DefaultCity");
        return new CityLocation(
            section["Name"] ?? "Ourinhos, São Paulo, Brasil", null, null,
            section.GetValue("Latitude", -22.97889),
            section.GetValue("Longitude", -49.87056));
    }

    public async Task SetCityAsync(CityLocation city)
    {
        await Upsert(CityKey, city.Label);
        await Upsert(LatKey, city.Latitude.ToString(CultureInfo.InvariantCulture));
        await Upsert(LonKey, city.Longitude.ToString(CultureInfo.InvariantCulture));
        await db.SaveChangesAsync();
    }

    private const string PrefsKey = "style.preferences";
    private static readonly JsonSerializerOptions Json = new() { Converters = { new JsonStringEnumConverter() } };

    public async Task<StylePreferences> GetPreferencesAsync()
    {
        var s = await db.Settings.AsNoTracking().FirstOrDefaultAsync(x => x.Key == PrefsKey);
        if (s == null) return new StylePreferences();
        try { return JsonSerializer.Deserialize<StylePreferences>(s.Value, Json) ?? new StylePreferences(); }
        catch (JsonException) { return new StylePreferences(); }
    }

    public async Task SetPreferencesAsync(StylePreferences prefs)
    {
        await Upsert(PrefsKey, JsonSerializer.Serialize(prefs, Json));
        await db.SaveChangesAsync();
    }

    private async Task Upsert(string key, string value)
    {
        var s = await db.Settings.FindAsync(key);
        if (s is null) db.Settings.Add(new AppSetting { Key = key, Value = value });
        else s.Value = value;
    }
}
