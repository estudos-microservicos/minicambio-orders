using System.ComponentModel.DataAnnotations;

namespace MiniCambio.Orders.Api.Infrastructure.Http;

/// <summary>
/// Seção <c>Services</c>: endereços dos serviços chamados por HTTP. Só o Rates: com o Payments a conversa é pelo
/// Kafka (seção <c>Kafka</c>).
/// </summary>
public sealed class ServicesOptions
{
    public const string SectionName = "Services";

    /// <summary>URL base do Rates (<c>Services__RatesApi</c>, ex.: <c>http://rates-api:8080</c>).</summary>
    [Required(ErrorMessage = "Configure a URL do Rates em Services__RatesApi.")]
    [Url(ErrorMessage = "Services__RatesApi deve ser uma URL http(s) absoluta.")]
    public string RatesApi { get; set; } = string.Empty;
}
