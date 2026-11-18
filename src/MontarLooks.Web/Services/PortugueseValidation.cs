using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.ModelBinding.Metadata;

namespace MontarLooks.Web.Services;

/// <summary>
/// Deixa em português as mensagens padrão de validação: as do model binding e as dos atributos
/// DataAnnotations sem ErrorMessage próprio, inclusive o [Required] implícito das propriedades não anuláveis.
/// </summary>
public static class PortugueseValidation
{
    public static void Apply(MvcOptions options)
    {
        var messages = options.ModelBindingMessageProvider;
        messages.SetMissingBindRequiredValueAccessor(campo => $"Informe um valor para {campo}.");
        messages.SetMissingKeyOrValueAccessor(() => "Informe um valor.");
        messages.SetMissingRequestBodyRequiredValueAccessor(() => "O envio não pode estar vazio.");
        messages.SetValueMustNotBeNullAccessor(valor => $"O valor \"{valor}\" não pode ficar vazio.");
        messages.SetAttemptedValueIsInvalidAccessor((valor, campo) => $"O valor \"{valor}\" não é válido para {campo}.");
        messages.SetNonPropertyAttemptedValueIsInvalidAccessor(valor => $"O valor \"{valor}\" não é válido.");
        messages.SetUnknownValueIsInvalidAccessor(campo => $"O valor informado não é válido para {campo}.");
        messages.SetNonPropertyUnknownValueIsInvalidAccessor(() => "O valor informado não é válido.");
        messages.SetValueIsInvalidAccessor(valor => $"O valor \"{valor}\" não é válido.");
        messages.SetValueMustBeANumberAccessor(campo => $"{campo} precisa ser um número.");
        messages.SetNonPropertyValueMustBeANumberAccessor(() => "Este campo precisa ser um número.");

        options.ModelMetadataDetailsProviders.Add(new AttributeMessages());
    }

    private static string? DefaultMessage(ValidationAttribute attribute) => attribute switch
    {
        RequiredAttribute => "Preencha o campo {0}.",
        StringLengthAttribute { MinimumLength: > 0 } => "{0} precisa ter entre {2} e {1} caracteres.",
        StringLengthAttribute => "{0} pode ter no máximo {1} caracteres.",
        MaxLengthAttribute => "{0} pode ter no máximo {1} itens ou caracteres.",
        MinLengthAttribute => "{0} precisa ter no mínimo {1} itens ou caracteres.",
        RangeAttribute => "{0} precisa estar entre {1} e {2}.",
        UrlAttribute => "{0} precisa ser um link válido (http:// ou https://).",
        EmailAddressAttribute => "{0} precisa ser um e-mail válido.",
        PhoneAttribute => "{0} precisa ser um telefone válido.",
        RegularExpressionAttribute => "{0} está em um formato inválido.",
        CompareAttribute => "{0} e {1} precisam ser iguais.",
        _ => null
    };

    private sealed class AttributeMessages : IValidationMetadataProvider
    {
        public void CreateValidationMetadata(ValidationMetadataProviderContext context)
        {
            var validators = context.ValidationMetadata.ValidatorMetadata;

            // Tipos de valor (int, bool, enum...) são obrigatórios sem atributo; sem isto, o ASP.NET
            // cria um [Required] próprio, com a mensagem em inglês, para a validação no navegador.
            var type = context.Key.ModelType;
            if (context.Key.MetadataKind == ModelMetadataKind.Property && type.IsValueType &&
                Nullable.GetUnderlyingType(type) is null && !validators.OfType<RequiredAttribute>().Any())
                validators.Add(new RequiredAttribute());

            foreach (var attribute in validators.OfType<ValidationAttribute>())
            {
                if (attribute.ErrorMessage is null && attribute.ErrorMessageResourceName is null)
                    attribute.ErrorMessage = DefaultMessage(attribute);
            }
        }
    }
}
