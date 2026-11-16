using System.Globalization;
using Microsoft.AspNetCore.Mvc.RazorPages;
using MontarLooks.Web.Services;

namespace MontarLooks.Web.Pages;

public class WrappedModel(WrappedService wrapped) : PageModel
{
    public WrappedMonth Data { get; private set; } = null!;
    public IReadOnlyList<DateOnly> MonthsWithData { get; private set; } = [];
    public DateOnly? Previous { get; private set; }
    public DateOnly? Next { get; private set; }

    public async Task OnGetAsync(string? month)
    {
        var today = LocalClock.Today;
        var m = DateOnly.TryParseExact(month + "-01", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
            ? parsed
            : new DateOnly(today.Year, today.Month, 1);
        (Data, MonthsWithData) = await wrapped.BuildAsync(m);
        Previous = MonthsWithData.Where(d => d < Data.Month).OrderByDescending(d => d).Cast<DateOnly?>().FirstOrDefault();
        Next = MonthsWithData.Where(d => d > Data.Month).OrderBy(d => d).Cast<DateOnly?>().FirstOrDefault();
    }

    public static string Key(DateOnly d) => d.ToString("yyyy-MM", CultureInfo.InvariantCulture);

    public static string OccasionEmoji(Models.Occasion o) => o switch
    {
        Models.Occasion.Casual => "☀️",
        Models.Occasion.Work => "💼",
        Models.Occasion.Study => "📚",
        Models.Occasion.Gym => "🏃‍♀️",
        Models.Occasion.Date => "💘",
        Models.Occasion.Party => "🪩",
        Models.Occasion.Formal => "🥂",
        Models.Occasion.Beach => "🏖️",
        Models.Occasion.Home => "🛋️",
        _ => "✨"
    };
}
