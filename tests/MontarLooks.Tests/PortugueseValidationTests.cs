using System.Net;
using System.Text.RegularExpressions;

namespace MontarLooks.Tests;

/// <summary>As mensagens que chegam à pessoa ficam em português, inclusive as padrão do ASP.NET.</summary>
public class PortugueseValidationTests(AppFactory factory) : IClassFixture<AppFactory>
{
    private async Task<string> EditFormAsync()
    {
        var response = await factory.CreateClient().GetAsync("/Wardrobe/Edit");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("data-val-required=\"Preencha o campo Nome.\"")]
    [InlineData("data-val-length=\"Nome pode ter no máximo 80 caracteres.\"")]
    [InlineData("data-val-range=\"Quanto esquenta (1-5) precisa estar entre 1 e 5.\"")]
    [InlineData("data-val-required=\"Preencha o campo Categoria.\"")]
    public async Task Edit_form_uses_portuguese_messages(string expected) =>
        Assert.Contains(expected, await EditFormAsync());

    [Fact]
    public async Task Edit_form_has_no_default_english_message()
    {
        var html = await EditFormAsync();
        Assert.DoesNotMatch(new Regex("data-val-[a-z]+=\"[^\"]*(field|must be|is required|is invalid)"), html);
    }

    [Fact]
    public async Task Invalid_post_answers_in_portuguese()
    {
        var client = factory.CreateClient();
        var form = await client.GetStringAsync("/Wardrobe/Edit");
        var token = Regex.Match(form, "name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"");
        Assert.True(token.Success, "token antiforgery não encontrado");

        var response = await client.PostAsync("/Wardrobe/Edit", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = WebUtility.HtmlDecode(token.Groups[1].Value),
            ["Item.Name"] = "",
            ["Item.PrimaryColor"] = "preto",
            ["Item.Price"] = "caro"
        }));

        var html = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());
        Assert.Contains(">Preencha o campo Nome.<", html);
        Assert.Contains(">O valor \"caro\" não é válido para Preço pago (R$).<", html);
    }
}
