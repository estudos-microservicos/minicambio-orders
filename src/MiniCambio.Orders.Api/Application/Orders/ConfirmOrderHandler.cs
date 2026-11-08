using MiniCambio.Orders.Api.Application.Payments;
using MiniCambio.Orders.Api.Domain;

namespace MiniCambio.Orders.Api.Application.Orders;

public sealed class ConfirmOrderHandler(
    IOrderRepository orders,
    IPaymentRequestPublisher paymentRequests,
    TimeProvider timeProvider,
    ILogger<ConfirmOrderHandler> logger)
{
    public async Task<OrderResponse> HandleAsync(Guid id, CancellationToken cancellationToken)
    {
        // 1. Carrega a ordem.
        var order = await orders.GetAsync(id, cancellationToken)
            ?? throw new ResourceNotFoundException(OrderMessages.NotFound);

        // 2. Já confirmada antes: devolve como está (confirmação idempotente).
        if (order.Status is OrderStatus.Confirmed or OrderStatus.PaymentRequested or OrderStatus.Completed)
        {
            return OrderResponse.From(order, timeProvider.GetUtcNow());
        }

        // 3. Cotação vencida e o worker ainda não percebeu: expira agora e recusa.
        var now = timeProvider.GetUtcNow();
        if (order.Status == OrderStatus.Quoted && order.IsQuoteExpired(now))
        {
            order.MarkExpired(now);
            await orders.SaveChangesAsync(cancellationToken);
            logger.LogInformation("Ordem {OrderId} expirou ao tentar confirmar", order.Id);
            throw new QuoteExpiredException();
        }

        // 4. Confirma (lança DomainException -> 422 se o status não permitir, ex.: cancelada).
        order.Confirm(now);
        await orders.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Ordem {OrderId} confirmada", order.Id);

        // 5. Publica o pedido de pagamento no Kafka. A ordem já está gravada como Confirmed: se a publicação falhar,
        // ela fica assim e o worker de reconciliação publica de novo (o Confirmed funciona como uma "caixa de saída").
        try
        {
            await paymentRequests.PublishAsync(PaymentRequestedMessage.From(order), cancellationToken);
            order.MarkPaymentRequested(timeProvider.GetUtcNow());
            await orders.SaveChangesAsync(cancellationToken);
            logger.LogInformation("Pedido de pagamento da ordem {OrderId} publicado", order.Id);
        }
        catch (ServiceUnavailableException ex)
        {
            logger.LogWarning(
                ex,
                "Não foi possível publicar o pedido de pagamento da ordem {OrderId}; ela fica em Confirmed e o worker tentará de novo",
                order.Id);
        }

        // 6. Devolve a ordem no status em que ficou (PaymentRequested ou Confirmed).
        return OrderResponse.From(order, timeProvider.GetUtcNow());
    }
}
