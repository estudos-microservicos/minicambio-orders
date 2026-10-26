using System.Net;
using MiniCambio.Orders.Api.Application;
using MiniCambio.Orders.Api.Application.Rates;

namespace MiniCambio.Orders.Api.Infrastructure.Http;

/// <summary>
/// Typed client do Rates. O <see cref="HttpClient"/> já vem com <c>BaseAddress</c>, o handler de correlation id e o
/// resilience handler (retry, timeouts e circuit breaker) configurados em <c>AddInfrastructure</c>.
/// </summary>
public sealed class RatesClient(HttpClient httpClient) : IRatesClient
{
    public const string UnavailableMessage =
        "O serviço de cotações está indisponível no momento. Tente novamente em instantes.";

    public const string RejectedFallbackMessage = "O serviço de cotações recusou o pedido de cotação.";

    public async Task<QuoteResponse> CreateQuoteAsync(CreateQuoteRequest request, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await httpClient.PostAsJsonAsync(
                "/api/v1/quotes", request, ExternalCall.JsonOptions, cancellationToken);

            // Recusa de negócio (ex.: moeda não suportada): repassa a mensagem do Rates.
            if (response.StatusCode is HttpStatusCode.UnprocessableEntity or HttpStatusCode.BadRequest)
            {
                var detail = await ExternalCall.ReadProblemDetailAsync(response, cancellationToken);
                throw new QuoteRejectedException(detail ?? RejectedFallbackMessage);
            }

            // 5xx (já depois das novas tentativas) ou qualquer outra resposta inesperada.
            if (!response.IsSuccessStatusCode)
            {
                throw new ServiceUnavailableException(UnavailableMessage, ExternalCall.UnexpectedStatus("Rates", response));
            }

            return await response.Content.ReadFromJsonAsync<QuoteResponse>(ExternalCall.JsonOptions, cancellationToken)
                ?? throw new ServiceUnavailableException(UnavailableMessage);
        }
        catch (Exception ex) when (ExternalCall.IsUnavailable(ex, cancellationToken))
        {
            throw new ServiceUnavailableException(UnavailableMessage, ex);
        }
    }
}
