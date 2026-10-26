namespace MiniCambio.Orders.Api.Application.Rates;

/// <summary>Cliente do serviço Rates (implementação em Infrastructure).</summary>
public interface IRatesClient
{
    /// <summary>
    /// Emite uma cotação (<c>POST /api/v1/quotes</c>).
    /// Lança <see cref="QuoteRejectedException"/> quando o Rates recusa o pedido (422/400) e
    /// <see cref="ServiceUnavailableException"/> quando ele está indisponível.
    /// </summary>
    Task<QuoteResponse> CreateQuoteAsync(CreateQuoteRequest request, CancellationToken cancellationToken);
}
