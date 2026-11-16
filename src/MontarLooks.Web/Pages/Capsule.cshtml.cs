using Microsoft.AspNetCore.Mvc.RazorPages;
using MontarLooks.Web.Services;

namespace MontarLooks.Web.Pages;

public class CapsuleModel(CapsuleService capsule) : PageModel
{
    public CapsuleReport Report { get; private set; } = null!;

    public async Task OnGetAsync() => Report = await capsule.BuildAsync();
}
