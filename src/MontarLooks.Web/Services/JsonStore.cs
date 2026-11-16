using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using MontarLooks.Web.Data;
using MontarLooks.Web.Models;

namespace MontarLooks.Web.Services;

/// <summary>
/// Small JSON documents kept in the Settings table (key → JSON). Used by the newer features
/// (planner, wishlist, feedback, laundry, palette...) so they need no schema migrations.
/// Keys in use: see <see cref="Keys"/>.
/// </summary>
public class JsonStore(AppDbContext db)
{
    public static class Keys
    {
        public const string DirtyItems = "laundry.dirty";        // List<int>
        public const string Feedback = "looks.feedback";          // List<LookFeedback>
        public const string Wishlist = "wishlist.items";          // owned by the Wishlist feature
        public const string WeekPlan = "planner.week";            // owned by the Planner feature
        public const string Trips = "travel.trips";               // owned by the Travel feature
        public const string Palette = "palette.personal";         // PersonalPalette
        public const string BodyPhotos = "avatar.bodyPhotos";      // List<string> (file names in uploads)
    }

    public static readonly JsonSerializerOptions Options = new()
    {
        Converters = { new JsonStringEnumConverter() },
        PropertyNameCaseInsensitive = true
    };

    public async Task<T> GetAsync<T>(string key, Func<T> fallback)
    {
        var row = await db.Settings.AsNoTracking().FirstOrDefaultAsync(s => s.Key == key);
        if (row == null) return fallback();
        try { return JsonSerializer.Deserialize<T>(row.Value, Options) ?? fallback(); }
        catch (JsonException) { return fallback(); }
    }

    public async Task SetAsync<T>(string key, T value)
    {
        var json = JsonSerializer.Serialize(value, Options);
        var row = await db.Settings.FindAsync(key);
        if (row == null) db.Settings.Add(new AppSetting { Key = key, Value = json });
        else row.Value = json;
        await db.SaveChangesAsync();
    }

    public async Task<HashSet<int>> DirtyAsync() => await GetAsync(Keys.DirtyItems, () => new HashSet<int>());
    public async Task<List<LookFeedback>> FeedbackAsync() => await GetAsync(Keys.Feedback, () => new List<LookFeedback>());
    public async Task<PersonalPalette?> PaletteAsync() => await GetAsync<PersonalPalette?>(Keys.Palette, () => null);
}

/// <summary>A like / "never again" on a suggested look.</summary>
public record LookFeedback(int[] ItemIds, bool Liked, DateTime At);

/// <summary>Result of the personal color analysis (Palette feature).</summary>
public class PersonalPalette
{
    /// <summary>e.g. "Primavera clara", "Inverno profundo".</summary>
    public string Season { get; set; } = "";
    /// <summary>Keys from <see cref="ColorPalette"/> that flatter the person.</summary>
    public List<string> BestColors { get; set; } = [];
    /// <summary>Keys from <see cref="ColorPalette"/> to keep away from the face.</summary>
    public List<string> AvoidColors { get; set; } = [];
}
