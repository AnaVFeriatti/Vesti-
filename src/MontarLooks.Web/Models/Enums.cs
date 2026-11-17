using System.ComponentModel.DataAnnotations;

namespace MontarLooks.Web.Models;

public enum Category
{
    [Display(Name = "Parte de cima")] Top,
    [Display(Name = "Parte de baixo")] Bottom,
    [Display(Name = "Vestido / Macacão")] OnePiece,
    [Display(Name = "Casaco / Jaqueta")] Outerwear,
    [Display(Name = "Calçado")] Shoes,
    [Display(Name = "Acessório")] Accessory
}

public enum Style
{
    [Display(Name = "Casual")] Casual,
    [Display(Name = "Esportivo")] Sporty,
    [Display(Name = "Urbano")] Streetwear,
    [Display(Name = "Social")] Formal,
    [Display(Name = "Esporte fino")] SmartCasual,
    [Display(Name = "Praia / Verão")] Beach,
    [Display(Name = "Festa")] Party
}

public enum Occasion
{
    [Display(Name = "Dia a dia / passeio")] Casual,
    [Display(Name = "Trabalho")] Work,
    [Display(Name = "Faculdade / aula")] Study,
    [Display(Name = "Academia / esporte")] Gym,
    [Display(Name = "Encontro")] Date,
    [Display(Name = "Festa / balada")] Party,
    [Display(Name = "Evento formal / casamento")] Formal,
    [Display(Name = "Praia / piscina")] Beach,
    [Display(Name = "Ficar em casa")] Home
}
