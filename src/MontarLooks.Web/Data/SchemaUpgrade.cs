using Microsoft.EntityFrameworkCore;

namespace MontarLooks.Web.Data;

/// <summary>
/// EnsureCreated does not add columns to an existing database, so columns added after the
/// first run are created here. Safe to run on every start.
/// </summary>
public static class SchemaUpgrade
{
    private static readonly string[] DetailColumns = ["Sleeve", "Neckline", "Length", "Fit", "Pattern", "Fabric", "Rise"];

    public static void Apply(AppDbContext db)
    {
        var isPostgres = db.Database.ProviderName?.Contains("Npgsql") == true;
        foreach (var col in DetailColumns)
        {
            if (isPostgres)
            {
                db.Database.ExecuteSqlRaw($"ALTER TABLE \"ClothingItems\" ADD COLUMN IF NOT EXISTS \"{col}\" character varying(20) NULL");
            }
            else
            {
                try { db.Database.ExecuteSqlRaw($"ALTER TABLE \"ClothingItems\" ADD COLUMN \"{col}\" TEXT NULL"); }
                catch (Exception) { /* column already exists */ }
            }
        }
    }
}
