namespace MontarLooks.Web.Services;

/// <summary>"Today" in the user's time zone (default America/Sao_Paulo), not the server's.</summary>
public static class LocalClock
{
    private static readonly TimeZoneInfo Zone = Resolve();

    private static TimeZoneInfo Resolve()
    {
        var id = Environment.GetEnvironmentVariable("TZ") ?? "America/Sao_Paulo";
        try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
        catch (TimeZoneNotFoundException) { return TimeZoneInfo.CreateCustomTimeZone("BRT", TimeSpan.FromHours(-3), "BRT", "BRT"); }
    }

    public static DateTime Now => TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, Zone);
    public static DateOnly Today => DateOnly.FromDateTime(Now);
}
