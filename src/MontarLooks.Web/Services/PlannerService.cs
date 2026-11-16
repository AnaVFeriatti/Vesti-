using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using MontarLooks.Web.Models;

namespace MontarLooks.Web.Services;

/// <summary>Saved week plan (JsonStore key <see cref="JsonStore.Keys.WeekPlan"/>).</summary>
public class WeekPlan
{
    public List<PlanDay> Days { get; set; } = [];

    public PlanDay? Get(DateOnly date) => Days.FirstOrDefault(d => d.Date == date);

    public PlanDay GetOrAdd(DateOnly date)
    {
        var d = Get(date);
        if (d != null) return d;
        d = new PlanDay { Date = date };
        Days.Add(d);
        return d;
    }

    /// <summary>Drops days older than <paramref name="keepFrom"/> so the document stays small.</summary>
    public void Prune(DateOnly keepFrom) => Days.RemoveAll(d => d.Date < keepFrom);
}

public class PlanDay
{
    public DateOnly Date { get; set; }
    /// <summary>Occasion picked by the user (null = automatic: agenda hint or the day's default).</summary>
    public Occasion? Occasion { get; set; }
    public string? Agenda { get; set; }
    public bool Locked { get; set; }
    public List<int> ItemIds { get; set; } = [];
    public string? Title { get; set; }
    public int Seed { get; set; }
}

/// <summary>What the planner needs to dress one day.</summary>
public record PlanDayInput(DateOnly Date, Occasion Occasion, double TemperatureC, bool IsRainy, bool Locked, IReadOnlyList<int> LockedItemIds, int Seed = 0);

public record PlannedLook(DateOnly Date, IReadOnlyList<ClothingItem> Items, string? Title);

/// <summary>
/// Week planner logic, no I/O: dates of the week, agenda hints and one look per day
/// without repeating main pieces (top, bottom, one piece) across the week.
/// </summary>
public class PlannerService(OutfitGenerator generator)
{
    /// <summary>Score points a look loses for each main piece already used in the week.</summary>
    public const double RepeatPenalty = 2.5;

    /// <summary>Monday of the week to plan: this week on weekdays, next week on Saturday/Sunday.</summary>
    public static DateOnly WeekStart(DateOnly today, bool next = false)
    {
        var offset = ((int)today.DayOfWeek + 6) % 7; // Monday = 0
        var monday = today.AddDays(-offset);
        if (today.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) monday = monday.AddDays(7);
        return next ? monday.AddDays(7) : monday;
    }

    public static IReadOnlyList<DateOnly> WeekDates(DateOnly today, bool includeWeekend, bool next = false)
    {
        var start = WeekStart(today, next);
        return Enumerable.Range(0, includeWeekend ? 7 : 5).Select(start.AddDays).ToList();
    }

    public static Occasion DefaultOccasion(DateOnly date) =>
        date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday ? Occasion.Casual : Occasion.Work;

    // Keyword → occasion. Checked in order (first match wins), compared without accents.
    public static readonly (string[] Words, Occasion Occasion)[] Hints =
    [
        (["casamento", "formatura", "gala", "batizado", "cerimonia"], Occasion.Formal),
        (["festa", "aniversario", "balada", "show", "niver"], Occasion.Party),
        (["encontro", "jantar", "date", "romantico"], Occasion.Date),
        (["academia", "treino", "corrida", "pilates", "yoga", "crossfit", "funcional"], Occasion.Gym),
        (["reuniao", "cliente", "apresentacao", "entrevista", "escritorio", "trabalho", "palestra"], Occasion.Work),
        (["aula", "faculdade", "prova", "escola", "curso"], Occasion.Study),
        (["praia", "piscina", "clube"], Occasion.Beach),
        (["home office", "em casa", "descanso", "faxina"], Occasion.Home),
    ];

    /// <summary>Occasion suggested by words in the agenda ("reunião com cliente" → Trabalho), or null.</summary>
    public static Occasion? GuessOccasion(string? agenda)
    {
        if (string.IsNullOrWhiteSpace(agenda)) return null;
        var text = RemoveAccents(agenda.ToLowerInvariant());
        // Words must start at a word boundary ("prova" should not match "aprovação").
        foreach (var (words, occasion) in Hints)
            if (words.Any(w => Regex.IsMatch(text, @"\b" + Regex.Escape(w)))) return occasion;
        return null;
    }

    /// <summary>Occasion used for a day: user's choice, then agenda hint, then weekday default.</summary>
    public static Occasion EffectiveOccasion(PlanDay day) =>
        day.Occasion ?? GuessOccasion(day.Agenda) ?? DefaultOccasion(day.Date);

    public static string RemoveAccents(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var c in s.Normalize(NormalizationForm.FormD))
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark) sb.Append(c);
        return sb.ToString().Normalize(NormalizationForm.FormC);
    }

    public static bool IsMain(ClothingItem i) => i.Category is Category.Top or Category.Bottom or Category.OnePiece;

    /// <summary>
    /// One look per day. Locked days keep their pieces; the other days get looks whose main
    /// pieces are not used anywhere else in the week (falls back to repeating only when the
    /// wardrobe is too small). <paramref name="onlyDate"/> re-rolls a single day.
    /// </summary>
    public IReadOnlyList<PlannedLook> Plan(
        IReadOnlyList<ClothingItem> wardrobe,
        IReadOnlyDictionary<int, DateOnly> lastWorn,
        IReadOnlyList<PlanDayInput> days,
        StylePreferences prefs,
        DateOnly? onlyDate = null,
        IReadOnlyDictionary<DateOnly, IReadOnlyList<int>>? currentItems = null)
    {
        var byId = wardrobe.ToDictionary(i => i.Id);
        var result = new Dictionary<DateOnly, PlannedLook>();
        // Pieces kept as they are: locked days, and every other day when re-rolling just one.
        foreach (var d in days)
        {
            var keep = d.Locked || (onlyDate.HasValue && d.Date != onlyDate.Value);
            if (!keep) continue;
            var ids = d.Locked ? d.LockedItemIds : currentItems?.GetValueOrDefault(d.Date) ?? d.LockedItemIds;
            result[d.Date] = new PlannedLook(d.Date, ids.Where(byId.ContainsKey).Select(id => byId[id]).ToList(), null);
        }

        foreach (var d in days.OrderBy(d => d.Date))
        {
            if (result.ContainsKey(d.Date)) continue;
            var usedMain = result.Values.SelectMany(l => l.Items).Where(IsMain).Select(i => i.Id).ToHashSet();

            // "Worn" dates include the rest of the plan, so shoes and accessories also rotate.
            var worn = new Dictionary<int, DateOnly>(lastWorn);
            foreach (var l in result.Values.Where(l => l.Date < d.Date))
                foreach (var i in l.Items)
                    if (!worn.TryGetValue(i.Id, out var w) || w < l.Date) worn[i.Id] = l.Date;

            var req = new OutfitRequest(d.Occasion, d.TemperatureC, d.IsRainy, d.Date, 1, d.Seed + d.Date.DayNumber, prefs);
            var fresh = wardrobe.Where(i => !(IsMain(i) && usedMain.Contains(i.Id))).ToList();
            // Prefer fresh main pieces, but repeating one beats a clearly worse look
            // (small wardrobes run out of fresh pieces). Each repeated main piece costs points.
            var options = generator.Suggest(fresh, worn, req with { Count = 1 })
                .Concat(generator.Suggest(wardrobe, worn, req with { Count = 4 }));
            var look = options
                .OrderByDescending(o => o.Score - RepeatPenalty * o.Items.Count(i => IsMain(i) && usedMain.Contains(i.Id)))
                .FirstOrDefault();
            result[d.Date] = new PlannedLook(d.Date, look?.Items ?? [], look?.Title);
        }

        return days.Select(d => result[d.Date]).ToList();
    }

    /// <summary>Main piece ids that appear on more than one day of the plan.</summary>
    public static HashSet<int> RepeatedMainPieces(IEnumerable<PlannedLook> looks) =>
        looks.SelectMany(l => l.Items.Where(IsMain).Select(i => i.Id).Distinct())
            .GroupBy(id => id).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet();
}
