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

    public static bool IsHttp(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps);

    private async Task LoadAsync()
    {
        Wishes = await wishlist.EvaluateAllAsync();
        Prefs = await settings.GetPreferencesAsync();
    }
}
