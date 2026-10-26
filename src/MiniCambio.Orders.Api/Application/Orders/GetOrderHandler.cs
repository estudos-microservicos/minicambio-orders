namespace MiniCambio.Orders.Api.Application.Orders;

public sealed class GetOrderHandler(IOrderRepository orders, TimeProvider timeProvider)
{
    public async Task<OrderResponse> HandleAsync(Guid id, CancellationToken cancellationToken)
    {
        var order = await orders.GetAsync(id, cancellationToken)
            ?? throw new ResourceNotFoundException(OrderMessages.NotFound);

        return OrderResponse.From(order, timeProvider.GetUtcNow());
    }
}

internal static class OrderMessages
{
    public const string NotFound = "Ordem não encontrada.";
}
