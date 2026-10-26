using MiniCambio.Orders.Api.Domain;

namespace MiniCambio.Orders.UnitTests;

/// <summary>Cria ordens em qualquer status, sempre passando pelas transições válidas da entidade.</summary>
internal static class TestOrders
{
    public static readonly DateTimeOffset Now = new(2026, 5, 10, 12, 0, 0, TimeSpan.Zero);
    public static readonly TimeSpan QuoteTtl = TimeSpan.FromSeconds(60);

    public static Order Quoted(DateTimeOffset? createdAt = null, string? idempotencyKey = null)
    {
        var now = createdAt ?? Now;
        return Order.CreateQuoted(
            idempotencyKey ?? Guid.NewGuid().ToString(),
            "USD",
            OrderSide.Buy,
            100m,
            new OrderQuote(Guid.NewGuid(), 5.427m, 542.70m, now.Add(QuoteTtl)),
            new Beneficiary("Maria Silva", "BR", "12345-6"),
            now);
    }

    public static Order InStatus(OrderStatus status, DateTimeOffset? createdAt = null)
    {
        var now = createdAt ?? Now;
        var order = Quoted(now);
        var later = now.AddSeconds(1);

        switch (status)
        {
            case OrderStatus.Quoted:
                break;
            case OrderStatus.Confirmed:
                order.Confirm(later);
                break;
            case OrderStatus.PaymentRequested:
                order.Confirm(later);
                order.MarkPaymentRequested(Guid.NewGuid(), later);
                break;
            case OrderStatus.Completed:
                order.Confirm(later);
                order.MarkPaymentRequested(Guid.NewGuid(), later);
                order.MarkCompleted(later);
                break;
            case OrderStatus.Failed:
                order.Confirm(later);
                order.MarkPaymentRequested(Guid.NewGuid(), later);
                order.MarkFailed("BENEFICIARY_REJECTED", later);
                break;
            case OrderStatus.Cancelled:
                order.Cancel(later);
                break;
            case OrderStatus.Expired:
                order.MarkExpired(now.Add(QuoteTtl));
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(status), status, null);
        }

        return order;
    }
}
