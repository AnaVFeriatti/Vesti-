using Microsoft.EntityFrameworkCore;
using MontarLooks.Web.Models;

namespace MontarLooks.Web.Data;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<ClothingItem> ClothingItems => Set<ClothingItem>();
    public DbSet<Outfit> Outfits => Set<Outfit>();
    public DbSet<OutfitItem> OutfitItems => Set<OutfitItem>();
    public DbSet<AppSetting> Settings => Set<AppSetting>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<ClothingItem>(e =>
        {
            e.Property(x => x.Category).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.Style).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.PrimaryColor).HasMaxLength(30);
            e.Property(x => x.SecondaryColor).HasMaxLength(30);
            e.Property(x => x.Price).HasPrecision(10, 2);
            e.Property(x => x.Sleeve).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.Neckline).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.Length).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.Fit).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.Pattern).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.Fabric).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.Rise).HasConversion<string>().HasMaxLength(20);
            e.HasIndex(x => x.Category);
        });

        b.Entity<Outfit>(e =>
        {
            e.Property(x => x.Occasion).HasConversion<string>().HasMaxLength(20);
            e.HasIndex(x => x.WornOn);
        });

        b.Entity<OutfitItem>(e =>
        {
            e.HasKey(x => new { x.OutfitId, x.ClothingItemId });
            e.HasOne(x => x.Outfit).WithMany(o => o.Items).HasForeignKey(x => x.OutfitId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.ClothingItem).WithMany(c => c.OutfitItems).HasForeignKey(x => x.ClothingItemId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<AppSetting>().HasKey(x => x.Key);
    }
}
