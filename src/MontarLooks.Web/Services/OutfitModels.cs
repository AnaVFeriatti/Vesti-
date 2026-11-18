using System.Globalization;
using MontarLooks.Web.Models;

namespace MontarLooks.Web.Services;

public record OutfitRequest(
    Occasion Occasion,
    double TemperatureC,
    bool IsRainy,
    DateOnly Today,
    int Count = 3,
    int Seed = 0,
    StylePreferences? Preferences = null)
{
    /// <summary>Pieces that can't be used now (in the laundry, already planned...).</summary>
    public IReadOnlySet<int>? Excluded { get; init; }
    /// <summary>Likes / "never again" given to earlier suggestions: the engine learns from them.</summary>
    public IReadOnlyList<LookFeedback>? Feedback { get; init; }
    /// <summary>Personal color analysis: colors that flatter near the face.</summary>
    public PersonalPalette? Palette { get; init; }
    /// <summary>Day's minimum temperature (real, not felt). Optional: enables the "removable layer" logic.</summary>
    public double? MinC { get; init; }
    /// <summary>Day's maximum temperature (real, not felt). Optional.</summary>
    public double? MaxC { get; init; }

    /// <summary>How much colder than the "standard" person this person feels (°C).</summary>
    public double ComfortOffset => Preferences?.ComfortOffset ?? 0;

    /// <summary>Temperature as the person feels it (someone who is cold at 25°C "feels" 17°C).</summary>
    public double FeltC => TemperatureC - ComfortOffset;
    public double FeltMinC => Math.Min(MinC ?? TemperatureC, TemperatureC) - ComfortOffset;
    public double FeltMaxC => Math.Max(MaxC ?? TemperatureC, TemperatureC) - ComfortOffset;

    /// <summary>
    /// Chilly morning and clearly warmer afternoon: the look should work without the extra
    /// layer in the afternoon and with it in the morning.
    /// </summary>
    public bool HasSwing => MinC.HasValue && MaxC.HasValue && MaxC.Value - MinC.Value >= 6 && FeltMinC < 20;
}

public record OutfitSuggestion(
    string Title,
    string Harmony,
    IReadOnlyList<ClothingItem> Items,
    IReadOnlyList<string> Reasons,
    double Score);

public record OccasionProfile(double TargetFormality, Style[] PreferredStyles, bool RequiresSporty = false);
