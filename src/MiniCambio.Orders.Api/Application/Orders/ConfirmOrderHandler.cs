using MiniCambio.Orders.Api.Application.Payments;
using MiniCambio.Orders.Api.Domain;

namespace MiniCambio.Orders.Api.Application.Orders;

public sealed class ConfirmOrderHandler(
    IOrderRepository orders,
    IPaymentsClient paymentsClient,
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

        // 5. Solicita o pagamento. Se falhar, a ordem fica em Confirmed e o worker de reconciliação tenta de novo.
        try
        {
            var payment = await paymentsClient.RequestPaymentAsync(CreatePaymentRequest.From(order), cancellationToken);
            order.MarkPaymentRequested(payment.Id, timeProvider.GetUtcNow());
            await orders.SaveChangesAsync(cancellationToken);
            logger.LogInformation("Pagamento {PaymentId} solicitado para a ordem {OrderId}", payment.Id, order.Id);
        }
        catch (ServiceUnavailableException ex)
        {
            logger.LogWarning(
                ex,
                "Não foi possível solicitar o pagamento da ordem {OrderId}; ela fica em Confirmed e o worker tentará de novo",
                order.Id);
        }

        // 6. Devolve a ordem no status em que ficou (PaymentRequested ou Confirmed).
        return OrderResponse.From(order, timeProvider.GetUtcNow());
    }
}
