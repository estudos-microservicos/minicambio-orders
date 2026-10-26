using MiniCambio.Orders.Api.Domain;

namespace MiniCambio.Orders.Api.Application.Orders;

public sealed class CancelOrderHandler(
    IOrderRepository orders,
    TimeProvider timeProvider,
    ILogger<CancelOrderHandler> logger)
{
    /// <summary>Cancelamento lógico: a ordem continua existindo, com status <see cref="OrderStatus.Cancelled"/>.</summary>
    public async Task HandleAsync(Guid id, CancellationToken cancellationToken)
    {
        // 1. Carrega a ordem.
        var order = await orders.GetAsync(id, cancellationToken)
            ?? throw new ResourceNotFoundException(OrderMessages.NotFound);

        // 2. Já cancelada: nada a fazer (cancelamento idempotente).
        if (order.Status == OrderStatus.Cancelled)
        {
            return;
        }

        // 3. Quoted -> Cancelled. 4. Qualquer outro status lança DomainException (422).
        order.Cancel(timeProvider.GetUtcNow());
        await orders.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Ordem {OrderId} cancelada", order.Id);
    }
}
