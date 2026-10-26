namespace MiniCambio.Orders.Api.Application.Orders;

/// <summary>
/// Query string do <c>GET /api/v1/orders</c>. <c>status</c> é texto (case-insensitive) para que um valor inválido
/// vire erro de campo; vazio ou ausente significa "todos".
/// </summary>
public sealed record ListOrdersQuery(string? Status, int? Page, int? PageSize)
{
    public const int DefaultPage = 1;
    public const int DefaultPageSize = 20;
    public const int MaxPageSize = 100;
}
