using MiniCambio.Orders.Api.Domain;

namespace MiniCambio.Orders.Api.Application.Orders;

public sealed class ListOrdersHandler(IOrderRepository orders, TimeProvider timeProvider)
{
    /// <summary>Lista paginada, da mais recente para a mais antiga. Espera uma query já validada.</summary>
    public async Task<PagedResponse<OrderResponse>> HandleAsync(ListOrdersQuery query, CancellationToken cancellationToken)
    {
        OrderStatus? status = string.IsNullOrEmpty(query.Status)
            ? null
            : Enum.Parse<OrderStatus>(query.Status, ignoreCase: true);
        var page = query.Page ?? ListOrdersQuery.DefaultPage;
        var pageSize = query.PageSize ?? ListOrdersQuery.DefaultPageSize;

        var result = await orders.ListAsync(status, page, pageSize, cancellationToken);

        var now = timeProvider.GetUtcNow();
        return new PagedResponse<OrderResponse>(
            [.. result.Items.Select(order => OrderResponse.From(order, now))],
            page,
            pageSize,
            result.TotalCount,
            Environment.MachineName);
    }
}
