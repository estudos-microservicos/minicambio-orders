namespace MiniCambio.Orders.Api.Application.Payments;

/// <summary>Cliente do serviço Payments (implementação em Infrastructure).</summary>
public interface IPaymentsClient
{
    /// <summary>
    /// Solicita o pagamento (<c>POST /api/v1/payments</c>). O Payments é idempotente por <c>orderId</c>: repetir a
    /// chamada devolve o pagamento já existente. Lança <see cref="ServiceUnavailableException"/> em caso de falha.
    /// </summary>
    Task<PaymentResponse> RequestPaymentAsync(CreatePaymentRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// Consulta um pagamento (<c>GET /api/v1/payments/{id}</c>); <c>null</c> se não existir.
    /// Lança <see cref="ServiceUnavailableException"/> em caso de falha.
    /// </summary>
    Task<PaymentResponse?> GetPaymentAsync(Guid paymentId, CancellationToken cancellationToken);
}
