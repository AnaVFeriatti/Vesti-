using Microsoft.EntityFrameworkCore;
using MontarLooks.Web.Data;
using MontarLooks.Web.Models;

namespace MontarLooks.Web.Services;

/// <summary>Loads (and removes) a sample wardrobe with fake history, to try the app and the dashboard.</summary>
public class DemoDataService(AppDbContext db, OutfitGenerator generator)
{
    public async Task<int> LoadAsync(DateOnly today)
    {
        if (await db.ClothingItems.AnyAsync(i => i.IsDemo)) return 0;

        ClothingItem I(string name, Category c, string sub, string color, Style s, int warmth, int formality,
            bool rain = false, decimal? price = null) => new()
        {
            Name = name, Category = c, SubType = sub, PrimaryColor = color, Style = s,
            Warmth = warmth, Formality = formality, RainFriendly = rain, Price = price, IsDemo = true,
            CreatedAt = DateTime.UtcNow.AddDays(-90)
        };

        var items = new List<ClothingItem>
        {
            I("Camiseta branca básica", Category.Top, "camiseta", "branco", Style.Casual, 1, 1, price: 49),
            I("Camiseta preta oversized", Category.Top, "camiseta", "preto", Style.Streetwear, 1, 1, price: 79),
            I("Camiseta estampada banda", Category.Top, "camiseta", "estampado", Style.Streetwear, 1, 1, price: 89),
            I("Polo azul-marinho", Category.Top, "polo", "marinho", Style.SmartCasual, 2, 3, price: 129),
            I("Camisa social branca", Category.Top, "camisa", "branco", Style.Formal, 2, 5, price: 189),
            I("Camisa linho bege", Category.Top, "camisa", "bege", Style.SmartCasual, 1, 3, price: 159),
            I("Camisa xadrez vinho", Category.Top, "camisa", "vinho", Style.Casual, 3, 2, price: 139),
            I("Moletom cinza", Category.Top, "moletom", "cinza", Style.Casual, 4, 1, price: 149),
            I("Suéter verde-oliva", Category.Top, "suéter", "verde-oliva", Style.SmartCasual, 4, 3, price: 199),
            I("Regata dry-fit", Category.Top, "regata", "azul", Style.Sporty, 1, 1, price: 59),
            I("Calça jeans escura", Category.Bottom, "jeans", "jeans", Style.Casual, 3, 2, price: 199),
            I("Calça alfaiataria preta", Category.Bottom, "alfaiataria", "preto", Style.Formal, 3, 5, price: 249),
            I("Calça chino caqui", Category.Bottom, "chino", "caqui", Style.SmartCasual, 3, 3, price: 179),
            I("Bermuda jeans clara", Category.Bottom, "bermuda", "azul-claro", Style.Casual, 1, 1, price: 99),
            I("Short de treino", Category.Bottom, "short", "preto", Style.Sporty, 1, 1, price: 69),
            I("Calça moletom", Category.Bottom, "jogger", "cinza", Style.Casual, 4, 1, price: 119),
            I("Jaqueta jeans", Category.Outerwear, "jaqueta", "jeans", Style.Casual, 3, 2, price: 259),
            I("Blazer azul-marinho", Category.Outerwear, "blazer", "marinho", Style.Formal, 3, 5, price: 399),
            I("Corta-vento preto", Category.Outerwear, "corta-vento", "preto", Style.Sporty, 2, 1, rain: true, price: 189),
            I("Jaqueta de couro", Category.Outerwear, "jaqueta", "marrom", Style.Streetwear, 4, 3, price: 499),
            I("Tênis branco", Category.Shoes, "tênis", "branco", Style.Casual, 2, 2, price: 299),
            I("Tênis de corrida", Category.Shoes, "tênis", "laranja", Style.Sporty, 2, 1, price: 399),
            I("Sapato social", Category.Shoes, "sapato", "preto", Style.Formal, 2, 5, price: 349),
            I("Bota de couro", Category.Shoes, "bota", "marrom", Style.SmartCasual, 3, 3, rain: true, price: 449),
            I("Chinelo", Category.Shoes, "chinelo", "preto", Style.Beach, 1, 1, rain: true, price: 39),
            I("Relógio prata", Category.Accessory, "relógio", "cinza", Style.SmartCasual, 1, 4, price: 350),
            I("Boné preto", Category.Accessory, "boné", "preto", Style.Streetwear, 1, 1, price: 89),
        };
        db.ClothingItems.AddRange(items);
        await db.SaveChangesAsync();

        // About 45 days of history, generated with the real engine so the dashboard is realistic.
        var rng = new Random(42);
        var lastWorn = new Dictionary<int, DateOnly>();
        var occasions = new[] { Occasion.Work, Occasion.Work, Occasion.Work, Occasion.Casual, Occasion.Study, Occasion.Gym, Occasion.Date, Occasion.Party };
        for (int d = 45; d >= 1; d--)
        {
            if (rng.NextDouble() < 0.2) continue; // some days not logged
            var day = today.AddDays(-d);
            var temp = 22 + 7 * Math.Sin(d / 7.0) + rng.NextDouble() * 4 - 2;
            var occasion = occasions[rng.Next(occasions.Length)];
            var rainy = rng.NextDouble() < 0.25;
            var s = generator.Suggest(items, lastWorn, new OutfitRequest(occasion, temp, rainy, day, 1, rng.Next())).FirstOrDefault();
            if (s is null) continue;
            db.Outfits.Add(new Outfit
            {
                WornOn = day, Occasion = occasion, City = "Exemplo", TemperatureC = Math.Round(temp, 1),
                WeatherDescription = rainy ? "Chuva" : "Parcialmente nublado", Title = s.Title,
                Rating = rng.NextDouble() < 0.6 ? rng.Next(3, 6) : null,
                Items = s.Items.Select(i => new OutfitItem { ClothingItemId = i.Id }).ToList()
            });
            foreach (var i in s.Items) lastWorn[i.Id] = day;
        }
        await db.SaveChangesAsync();
        return items.Count;
    }

    public async Task RemoveAsync()
    {
        var demoIds = await db.ClothingItems.Where(i => i.IsDemo).Select(i => i.Id).ToListAsync();
        var outfitIds = await db.OutfitItems.Where(oi => demoIds.Contains(oi.ClothingItemId)).Select(oi => oi.OutfitId).Distinct().ToListAsync();
        db.Outfits.RemoveRange(await db.Outfits.Where(o => outfitIds.Contains(o.Id)).ToListAsync());
        db.ClothingItems.RemoveRange(await db.ClothingItems.Where(i => i.IsDemo).ToListAsync());
        await db.SaveChangesAsync();
    }
}
