using MiniCambio.Orders.Api.Domain;

namespace MiniCambio.Orders.Api.Application.Orders;

/// <summary>
/// Ordem como o frontend a enxerga. <see cref="CanConfirm"/> e <see cref="CanCancel"/> vêm da própria entidade,
/// para que o frontend não precise duplicar a regra de negócio.
/// </summary>
public sealed record OrderResponse(
    Guid Id,
    string Currency,
    OrderSide Side,
    decimal ForeignAmount,
    Guid QuoteId,
    decimal Rate,
    decimal BrlAmount,
    DateTimeOffset QuoteExpiresAt,
    BeneficiaryResponse Beneficiary,
    OrderStatus Status,
    Guid? PaymentId,
    string? FailureReason,
    DateTimeOffset? ConfirmedAt,
    DateTimeOffset? CancelledAt,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    bool CanConfirm,
    bool CanCancel,
    string ServedBy)
{
    public static OrderResponse From(Order order, DateTimeOffset now) => new(
        order.Id,
        order.Currency,
        order.Side,
        order.ForeignAmount,
        order.QuoteId,
        order.Rate,
        order.BrlAmount,
        order.QuoteExpiresAt,
        new BeneficiaryResponse(order.BeneficiaryName, order.BeneficiaryCountry, order.BeneficiaryAccount),
        order.Status,
        order.PaymentId,
        order.FailureReason,
        order.ConfirmedAt,
        order.CancelledAt,
        order.CreatedAt,
        order.UpdatedAt,
        order.CanBeConfirmed(now),
        order.CanBeCancelled(),
        Environment.MachineName);
}

public sealed record BeneficiaryResponse(string Name, string Country, string AccountNumber);

/// <summary>Página de resultados (<c>items</c>, <c>page</c>, <c>pageSize</c>, <c>totalCount</c>).</summary>
public sealed record PagedResponse<T>(
    IReadOnlyList<T> Items,
    int Page,
    int PageSize,
    int TotalCount,
    string ServedBy);
