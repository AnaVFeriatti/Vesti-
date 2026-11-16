using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using MontarLooks.Web.Models;
using MontarLooks.Web.Services;

namespace MontarLooks.Web.Pages;

public class WishlistModel(WishlistService wishlist, SettingsService settings) : PageModel
{
    [BindProperty] public WishItem Wish { get; set; } = new();

    public List<WishEvaluation> Wishes { get; private set; } = [];
    public StylePreferences Prefs { get; private set; } = new();
    public bool Editing { get; private set; }

    [TempData] public string? BoughtName { get; set; }

    public async Task<IActionResult> OnGetAsync(Guid? edit)
    {
        if (edit is { } id)
        {
            var w = await wishlist.GetAsync(id);
            if (w == null) return RedirectToPage();
            Wish = w;
            Editing = true;
        }
        await LoadAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostSaveAsync()
    {
        FixPrice();
        if (ColorPalette.All.All(c => c.Key != Wish.PrimaryColor))
            ModelState.AddModelError("Wish.PrimaryColor", "Escolha uma cor da lista.");
        if (!string.IsNullOrWhiteSpace(Wish.Link) && !IsHttp(Wish.Link))
            ModelState.AddModelError("Wish.Link", "O link precisa começar com http:// ou https://.");
        if (!ModelState.IsValid)
        {
            Editing = (await wishlist.GetAsync(Wish.Id)) != null;
            await LoadAsync();
            return Page();
        }
        Wish.Name = Wish.Name.Trim();
        await wishlist.SaveAsync(Wish);
        TempData["Message"] = $"\"{Wish.Name}\" está na sua lista de desejos.";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostDeleteAsync(Guid id)
    {
        var w = await wishlist.RemoveAsync(id);
        if (w != null) TempData["Message"] = $"\"{w.Name}\" saiu da lista de desejos.";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostBoughtAsync(Guid id)
    {
        var w = await wishlist.RemoveAsync(id);
        if (w != null) BoughtName = w.Name;
        return RedirectToPage();
    }

    /// <summary>
    /// Re-reads the price the way people type it ("R$ 189,90", "1.299,00", "189.90"). The pt-BR model
    /// binder alone would read "189.90" as 18990, and its error messages are in English.
    /// </summary>
    private void FixPrice()
    {
        const string key = "Wish.Price";
        var raw = Request.Form[key].ToString().Replace("R$", "", StringComparison.OrdinalIgnoreCase).Trim();
        ModelState.Remove(key);
        if (raw.Length == 0) { Wish.Price = null; return; }
        string? error = !TryParsePrice(raw, out var price) ? "Escreva só o valor, por exemplo 189,90."
            : price is < 0 or > 100000 ? "Informe um preço entre R$ 0 e R$ 100.000."
            : null;
        if (error == null) { Wish.Price = price; return; }
        ModelState.SetModelValue(key, raw, raw); // keeps what was typed when the form comes back
        ModelState.AddModelError(key, error);
    }

    public static bool TryParsePrice(string raw, out decimal price)
    {
        var pt = System.Globalization.CultureInfo.GetCultureInfo("pt-BR");
        var styles = System.Globalization.NumberStyles.Number;
        // "189.90" (one dot, two decimals) is a decimal point, not a thousands separator.
        if (System.Text.RegularExpressions.Regex.IsMatch(raw, @"^\d+\.\d{1,2}$"))
            return decimal.TryParse(raw, styles, System.Globalization.CultureInfo.InvariantCulture, out price);
        return decimal.TryParse(raw, styles, pt, out price);
    }

    public static bool IsHttp(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps);

    private async Task LoadAsync()
    {
        Prefs = await settings.GetPreferencesAsync();
        Wishes = await wishlist.EvaluateAllAsync(Prefs);
    }
}
