using Microsoft.AspNetCore.Mvc.RazorPages;
using MontarLooks.Web.Services;

namespace MontarLooks.Web.Pages;

public class DashboardModel(AnalyticsService analytics) : PageModel
{
    public WardrobeStats Stats { get; private set; } = null!;

    public async Task OnGetAsync() => Stats = await analytics.GetStatsAsync(LocalClock.Today);
}
