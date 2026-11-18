using System.Globalization;
using MontarLooks.Web.Models;
using static MontarLooks.Web.Services.OutfitRules;
using static MontarLooks.Web.Services.OutfitCandidates;
using static MontarLooks.Web.Services.OutfitScoring;
using static MontarLooks.Web.Services.OutfitColors;
using static MontarLooks.Web.Services.OutfitWording;

namespace MontarLooks.Web.Services;

/// <summary>Aprende o gosto da pessoa a partir das avaliações dos looks.</summary>
internal static class OutfitTaste
{
    /// <summary>Learns from feedback: pairs of pieces liked together or rejected together.</summary>
    public class TasteModel
    {
        private readonly Dictionary<(int, int), double> _pairs = new();
        private readonly List<HashSet<int>> _never = new();
        private readonly Dictionary<int, double> _items = new();

        public static TasteModel? From(IReadOnlyList<LookFeedback>? feedback)
        {
            if (feedback == null || feedback.Count == 0) return null;
            var t = new TasteModel();
            foreach (var f in feedback)
            {
                var ids = f.ItemIds.Distinct().OrderBy(x => x).ToArray();
                var w = f.Liked ? 1.0 : -1.0;
                for (int a = 0; a < ids.Length; a++)
                {
                    t._items[ids[a]] = t._items.GetValueOrDefault(ids[a]) + w * 0.15;
                    for (int b = a + 1; b < ids.Length; b++)
                        t._pairs[(ids[a], ids[b])] = t._pairs.GetValueOrDefault((ids[a], ids[b])) + w;
                }
                if (!f.Liked) t._never.Add(ids.ToHashSet());
            }
            return t;
        }

        public double Score(List<ClothingItem> look, ReasonList reasons)
        {
            var ids = look.Select(i => i.Id).Distinct().OrderBy(x => x).ToArray();
            // "Nunca mais": the same main combination never comes back.
            var main = look.Where(i => i.Category is Category.Top or Category.Bottom or Category.OnePiece).Select(i => i.Id).ToHashSet();
            if (_never.Any(n => main.Count > 0 && main.All(n.Contains))) return -8;

            double s = 0, liked = 0;
            for (int a = 0; a < ids.Length; a++)
            {
                s += Math.Clamp(_items.GetValueOrDefault(ids[a]), -0.6, 0.6);
                for (int b = a + 1; b < ids.Length; b++)
                {
                    var w = _pairs.GetValueOrDefault((ids[a], ids[b]));
                    s += w * 0.4;
                    if (w > 0) liked += w;
                }
            }
            if (liked >= 1) reasons.Add(PTaste, "Você curtiu combinações parecidas ❤");
            return Math.Clamp(s, -3, 2.5);
        }
    }
}
