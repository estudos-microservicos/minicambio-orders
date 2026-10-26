namespace MiniCambio.Orders.Api.Domain;

/// <summary>Violação de regra de negócio. Vira 422 na API, com <see cref="Exception.Message"/> como detalhe.</summary>
public class DomainException(string message) : Exception(message);

/// <summary>A cotação da ordem venceu antes da confirmação.</summary>
public sealed class QuoteExpiredException() : DomainException(DefaultMessage)
{
    public const string DefaultMessage = "A cotação expirou. Crie uma nova ordem.";
}
