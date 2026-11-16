using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using MontarLooks.Web.Models;
using MontarLooks.Web.Services;

namespace MontarLooks.Web.Pages;

public class SettingsModel(SettingsService settings, WeatherService weather, DemoDataService demo,
    MontarLooks.Web.Data.AppDbContext db, PhotoStorage photos, PhotoProcessor processor) : PageModel
{
    [BindProperty(SupportsGet = true)] public string? Search { get; set; }

    public CityLocation City { get; private set; } = null!;
    public StylePreferences Prefs { get; private set; } = new();
    public IReadOnlyList<CityLocation> Results { get; private set; } = [];
    public bool Searched { get; private set; }

    public async Task OnGetAsync(CancellationToken ct)
    {
        City = await settings.GetCityAsync();
        Prefs = await settings.GetPreferencesAsync();
        if (!string.IsNullOrWhiteSpace(Search))
        {
            Searched = true;
            Results = await weather.SearchCitiesAsync(Search, ct);
        }
    }

    // Coordinates are posted in invariant format ("-22.97"); parse them explicitly because
    // form binding uses the pt-BR culture, where "." is a thousands separator.
    public async Task<IActionResult> OnPostCityAsync(string name, string latitude, string longitude)
    {
        if (!double.TryParse(latitude, NumberStyles.Float, CultureInfo.InvariantCulture, out var lat)
            || !double.TryParse(longitude, NumberStyles.Float, CultureInfo.InvariantCulture, out var lon))
            return BadRequest();
        await settings.SetCityAsync(new CityLocation(name, null, null, lat, lon));
        TempData["Message"] = $"Cidade alterada para {name}.";
        return RedirectToPage("/Index");
    }

    public async Task<IActionResult> OnPostLoadDemoAsync()
    {
        var n = await demo.LoadAsync(LocalClock.Today);
        TempData["Message"] = n > 0 ? $"{n} peças de exemplo e um histórico fictício carregados." : "Os exemplos já estão carregados.";
        return RedirectToPage("/Index");
    }

    /// <summary>Rules arrive as "Work:NoStraps" checkbox values.</summary>
    public async Task<IActionResult> OnPostPreferencesAsync(int coldBelow, string[] rules)
    {
        var prefs = await settings.GetPreferencesAsync();
        prefs.ColdBelowC = Math.Clamp(coldBelow, 5, 35);
        prefs.Rules = new();
        foreach (var r in rules)
        {
            var parts = r.Split(':');
            if (parts.Length == 2 && Enum.TryParse<MontarLooks.Web.Models.Occasion>(parts[0], out var o) && Enum.TryParse<MontarLooks.Web.Models.DressRule>(parts[1], out var rule))
            {
                if (!prefs.Rules.TryGetValue(o, out var set)) prefs.Rules[o] = set = [];
                set.Add(rule);
            }
        }
        await settings.SetPreferencesAsync(prefs);
        TempData["Message"] = "Preferências salvas. Os looks já seguem as novas regras.";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostAvatarAsync(string skinTone, string hairColor, HairStyle hair, IFormFile? face, bool removeFace,
        IFormFile? body, bool removeBody)
    {
        var prefs = await settings.GetPreferencesAsync();
        static string Hex(string? v, string fallback) =>
            v is { Length: 7 } && v[0] == '#' && v.Skip(1).All(Uri.IsHexDigit) ? v : fallback;
        prefs.SkinTone = Hex(skinTone, prefs.SkinTone);
        prefs.HairColor = Hex(hairColor, prefs.HairColor);
        prefs.Hair = hair;
        if (removeFace || (face != null && PhotoStorage.IsAllowed(face)))
        {
            photos.Delete(prefs.FacePhoto);
            prefs.FacePhoto = null;
        }
        if (face != null && PhotoStorage.IsAllowed(face))
            prefs.FacePhoto = await photos.SaveAsync(face, Category.Accessory); // no hanger / rotation logic for people
        if (removeBody || (body != null && PhotoStorage.IsAllowed(body)))
        {
            photos.Delete(prefs.BodyPhoto);
            prefs.BodyPhoto = null;
        }
        if (body != null && PhotoStorage.IsAllowed(body))
            prefs.BodyPhoto = await photos.SaveAsync(body, Category.Accessory);
        await settings.SetPreferencesAsync(prefs);
        TempData["Message"] = "Provador atualizado. Veja você nos looks da página inicial!";
        return RedirectToPage();
    }

    public bool AiBackgroundRemoval => !string.IsNullOrWhiteSpace(processor.RemoveBgUrl);

    /// <summary>Re-runs crop / background cleanup on every photo, from the kept originals.</summary>
    public async Task<IActionResult> OnPostReprocessPhotosAsync(CancellationToken ct)
    {
        var items = db.ClothingItems.Where(i => i.PhotoFileName != null).ToList();
        foreach (var i in items)
            i.PhotoFileName = await photos.ReprocessAsync(i.PhotoFileName, i.Category, ct);
        await db.SaveChangesAsync(ct);
        TempData["Message"] = $"{items.Count} foto(s) retocadas.";
        return RedirectToPage("/Wardrobe/Index");
    }

    public async Task<IActionResult> OnPostRemoveDemoAsync()
    {
        await demo.RemoveAsync();
        TempData["Message"] = "Dados de exemplo removidos.";
        return RedirectToPage();
    }
}
