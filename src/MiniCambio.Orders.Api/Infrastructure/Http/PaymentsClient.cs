using System.Net;
using MiniCambio.Orders.Api.Application;
using MiniCambio.Orders.Api.Application.Payments;

namespace MiniCambio.Orders.Api.Infrastructure.Http;

/// <summary>
/// Typed client do Payments. O <see cref="HttpClient"/> já vem com <c>BaseAddress</c>, o handler de correlation id
/// e o resilience handler configurados em <c>AddInfrastructure</c>.
/// </summary>
public sealed class PaymentsClient(HttpClient httpClient) : IPaymentsClient
{
    public const string UnavailableMessage =
        "O serviço de pagamentos está indisponível no momento. Tente novamente em instantes.";

    public async Task<PaymentResponse> RequestPaymentAsync(CreatePaymentRequest request, CancellationToken cancellationToken)
    {
        try
        {
            // O Payments é idempotente por orderId: 202 quando cria, 200 com o pagamento já existente.
            // Por isso as novas tentativas do resilience handler (que também valem para POST) são seguras aqui.
            using var response = await httpClient.PostAsJsonAsync(
                "/api/v1/payments", request, ExternalCall.JsonOptions, cancellationToken);

            if (response.StatusCode is not (HttpStatusCode.Accepted or HttpStatusCode.OK))
            {
                throw new ServiceUnavailableException(UnavailableMessage, ExternalCall.UnexpectedStatus("Payments", response));
            }

            return await response.Content.ReadFromJsonAsync<PaymentResponse>(ExternalCall.JsonOptions, cancellationToken)
                ?? throw new ServiceUnavailableException(UnavailableMessage);
        }
        catch (Exception ex) when (ExternalCall.IsUnavailable(ex, cancellationToken))
        {
            throw new ServiceUnavailableException(UnavailableMessage, ex);
        }
    }

    public async Task<PaymentResponse?> GetPaymentAsync(Guid paymentId, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await httpClient.GetAsync($"/api/v1/payments/{paymentId}", cancellationToken);

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return null;
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new ServiceUnavailableException(UnavailableMessage, ExternalCall.UnexpectedStatus("Payments", response));
            }

            return await response.Content.ReadFromJsonAsync<PaymentResponse>(ExternalCall.JsonOptions, cancellationToken)
                ?? throw new ServiceUnavailableException(UnavailableMessage);
        }
        catch (Exception ex) when (ExternalCall.IsUnavailable(ex, cancellationToken))
        {
            throw new ServiceUnavailableException(UnavailableMessage, ex);
        }
    }
}
