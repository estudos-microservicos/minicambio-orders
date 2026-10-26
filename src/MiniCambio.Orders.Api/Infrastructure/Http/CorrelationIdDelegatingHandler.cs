using MiniCambio.Orders.Api.Infrastructure.Correlation;
using MiniCambio.Orders.Api.Infrastructure.Web;

namespace MiniCambio.Orders.Api.Infrastructure.Http;

/// <summary>
/// Propaga o <c>X-Correlation-Id</c> da execução atual em toda chamada HTTP de saída (Rates e Payments),
/// para que os logs dos três serviços possam ser ligados pelo mesmo id.
/// </summary>
public sealed class CorrelationIdDelegatingHandler(CorrelationIdAccessor correlationIdAccessor) : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (!request.Headers.Contains(CorrelationIdMiddleware.HeaderName))
        {
            // Sem id na execução atual (não deveria acontecer): gera um, para a chamada não sair sem rastreio.
            var correlationId = correlationIdAccessor.CorrelationId ?? Guid.NewGuid().ToString();
            request.Headers.TryAddWithoutValidation(CorrelationIdMiddleware.HeaderName, correlationId);
        }

        return base.SendAsync(request, cancellationToken);
    }
}
