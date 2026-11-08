namespace MiniCambio.Orders.Api.Application.Payments;

/// <summary>Publica pedidos de pagamento para o Payments (implementação com Kafka em Infrastructure).</summary>
public interface IPaymentRequestPublisher
{
    /// <summary>
    /// Publica o pedido e espera a confirmação do broker. Lança <see cref="ServiceUnavailableException"/> se o Kafka
    /// não confirmar a gravação a tempo.
    /// </summary>
    Task PublishAsync(PaymentRequestedMessage message, CancellationToken cancellationToken);
}
