namespace MontarLooks.Web.Models;

/// <summary>What the "provador" partial needs: the pieces of a look and the avatar settings.</summary>
public record MannequinView(IReadOnlyList<ClothingItem> Items, StylePreferences Prefs, bool Compact = false);
