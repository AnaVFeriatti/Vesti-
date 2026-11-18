using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using MontarLooks.Web.Models;
using MontarLooks.Web.Services;

namespace MontarLooks.Web.Pages;

public class PaletteModel(PaletteService palettes, SettingsService settings, PhotoStorage photos, ILogger<PaletteModel> log) : PageModel
{
    private const string AnswersKey = "Palette.Answers";

    public PersonalPalette? Palette { get; private set; }
    public SeasonProfile? Profile { get; private set; }
    public SeasonScore? Score { get; private set; }
    public PaletteWardrobe? Clothes { get; private set; }
    public SkinAnalysis? PhotoHint { get; private set; }
    public string? FacePhoto { get; private set; }
    /// <summary>Answers to pre-select: last submitted quiz, else what the photo suggests.</summary>
    public Dictionary<string, string> Prefill { get; private set; } = new();
    public string? Error { get; private set; }

    public async Task OnGetAsync()
    {
        var last = Parse(TempData[AnswersKey] as string);
        await LoadAsync(last);
    }

    /// <summary>Quiz submit: compute the season, save it and show the result with the score breakdown.</summary>
    public async Task<IActionResult> OnPostSaveAsync()
    {
        var answers = new Dictionary<string, string>();
        foreach (var q in PaletteService.Questions)
        {
            var v = Request.Form["q_" + q.Id].ToString();
            if (q.Options.Any(o => o.Value == v)) answers[q.Id] = v;
        }
        var missing = PaletteService.Questions.Where(q => !answers.ContainsKey(q.Id)).ToList();
        if (missing.Count > 0)
        {
            Error = "Responda todas as perguntas para descobrir sua estação (faltou: " + string.Join("; ", missing.Select(m => m.Title)) + ").";
            await LoadAsync(answers, showScore: false);
            return Page();
        }
        var score = PaletteService.Score(answers);
        await palettes.SaveSeasonAsync(score.Season);
        TempData[AnswersKey] = string.Join(";", answers.Select(kv => kv.Key + "=" + kv.Value));
        TempData["Message"] = $"Sua estação é {score.Season}! A paleta foi salva e já vale para o guarda-roupa.";
        return RedirectToPage(null, null, "resultado");
    }

    /// <summary>For people who already know their season (e.g. from a consultant).</summary>
    public async Task<IActionResult> OnPostChooseAsync(string season)
    {
        if (PaletteService.Profile(season) == null) return BadRequest();
        await palettes.SaveSeasonAsync(season);
        TempData["Message"] = $"Paleta {season} salva.";
        return RedirectToPage(null, null, "resultado");
    }

    /// <summary>Analyses a photo picked on this page. Nothing is stored: the bytes stay in memory.</summary>
    public async Task<IActionResult> OnPostAnalyzeAsync(IFormFile? photo)
    {
        if (photo == null || !await PhotoStorage.IsValidImageAsync(photo))
            return new JsonResult(new { ok = false, message = PhotoStorage.InvalidImageMessage });
        try
        {
            using var s = photo.OpenReadStream();
            var r = PaletteService.AnalyzeStream(s);
            return r == null
                ? new JsonResult(new { ok = false, message = "Não encontrei pele suficiente na foto. Tente uma foto de frente, com luz natural." })
                : new JsonResult(new { ok = true, result = r });
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Palette photo analysis failed.");
            return new JsonResult(new { ok = false, message = "Não consegui ler essa imagem." });
        }
    }

    private async Task LoadAsync(Dictionary<string, string>? answers, bool showScore = true)
    {
        Palette = await palettes.GetAsync();
        Profile = PaletteService.Profile(Palette?.Season);
        if (Palette != null) Clothes = palettes.Wardrobe(Palette);

        var prefs = await settings.GetPreferencesAsync();
        if (!string.IsNullOrEmpty(prefs.FacePhoto))
        {
            var path = Path.Combine(photos.RootPath, Path.GetFileName(prefs.FacePhoto));
            if (System.IO.File.Exists(path))
            {
                FacePhoto = Path.GetFileName(prefs.FacePhoto);
                try { PhotoHint = PaletteService.AnalyzeFile(path); }
                catch (Exception ex) { log.LogWarning(ex, "Could not analyse the face photo."); }
            }
        }

        if (answers is { Count: > 0 })
        {
            Prefill = answers;
            // Only show the breakdown when it matches the saved season (it may have been changed manually).
            var s = PaletteService.Score(answers);
            if (showScore && s.Season == Palette?.Season) Score = s;
        }
        else if (PhotoHint != null)
        {
            Prefill["fundo"] = PhotoHint.SkinBase;
            Prefill["pele"] = PhotoHint.SkinDepth;
        }
    }

    private static Dictionary<string, string>? Parse(string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return null;
        return raw.Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Split('=', 2)).Where(p => p.Length == 2)
            .GroupBy(p => p[0]).ToDictionary(g => g.Key, g => g.First()[1]);
    }
}
