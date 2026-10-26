using MiniCambio.Orders.Api.Application;
using MiniCambio.Orders.Api.Application.Payments;
using MiniCambio.Orders.Api.Application.Rates;
using MiniCambio.Orders.Api.Domain;

namespace MiniCambio.Orders.UnitTests.Application;

/// <summary>
/// Repositório em memória. Guarda uma "foto" do status de cada ordem a cada gravação, para os testes conferirem o
/// que de fato teria ido para o banco (e não só o estado do objeto em memória).
/// </summary>
internal sealed class InMemoryOrderRepository : IOrderRepository
{
    private readonly Dictionary<Guid, OrderStatus> persisted = [];

    public List<Order> Orders { get; } = [];

    public int SaveCount { get; private set; }

    /// <summary>Simula outra requisição gravando a mesma chave entre a consulta e o insert (corrida).</summary>
    public Order? ConcurrentInsertOnNextAdd { get; set; }

    public OrderStatus? PersistedStatus(Guid id) => persisted.TryGetValue(id, out var status) ? status : null;

    public Order Seed(Order order)
    {
        Orders.Add(order);
        persisted[order.Id] = order.Status;
        return order;
    }

    public Task<Order?> GetAsync(Guid id, CancellationToken cancellationToken) =>
        Task.FromResult(Orders.FirstOrDefault(x => x.Id == id));

    public Task<Order?> GetByIdempotencyKeyAsync(string idempotencyKey, CancellationToken cancellationToken) =>
        Task.FromResult(Orders.FirstOrDefault(x => x.IdempotencyKey == idempotencyKey));

    public Task AddAsync(Order order, CancellationToken cancellationToken)
    {
        if (ConcurrentInsertOnNextAdd is { } winner)
        {
            Seed(winner);
            ConcurrentInsertOnNextAdd = null;
        }

        if (Orders.Any(x => x.IdempotencyKey == order.IdempotencyKey))
        {
            throw new DuplicateIdempotencyKeyException(order.IdempotencyKey);
        }

        Orders.Add(order);
        return SaveChangesAsync(cancellationToken);
    }

    public Task SaveChangesAsync(CancellationToken cancellationToken)
    {
        SaveCount++;
        foreach (var order in Orders)
        {
            persisted[order.Id] = order.Status;
        }

        return Task.CompletedTask;
    }

    public Task<PagedResult<Order>> ListAsync(OrderStatus? status, int page, int pageSize, CancellationToken cancellationToken)
    {
        var filtered = Orders.Where(x => status is null || x.Status == status).ToList();
        var items = filtered
            .OrderByDescending(x => x.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToList();
        return Task.FromResult(new PagedResult<Order>(items, filtered.Count));
    }
}

internal sealed class FakeRatesClient(TimeProvider timeProvider) : IRatesClient
{
    public const decimal Rate = 5.427m;

    public List<CreateQuoteRequest> Requests { get; } = [];

    /// <summary>Quando preenchida, é lançada em vez de devolver a cotação.</summary>
    public Exception? FailWith { get; set; }

    public Task<QuoteResponse> CreateQuoteAsync(CreateQuoteRequest request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        if (FailWith is not null)
        {
            throw FailWith;
        }

        var now = timeProvider.GetUtcNow();
        return Task.FromResult(new QuoteResponse(
            Guid.NewGuid(),
            request.Currency,
            request.Side,
            request.ForeignAmount,
            Rate,
            Math.Round(request.ForeignAmount * Rate, 2, MidpointRounding.AwayFromZero),
            now,
            now.AddSeconds(60),
            IsExpired: false));
    }
}

internal sealed class FakePaymentsClient : IPaymentsClient
{
    private readonly Dictionary<Guid, PaymentResponse> paymentsByOrder = [];

    public List<CreatePaymentRequest> Requests { get; } = [];

    /// <summary>Quando preenchida, é lançada em vez de responder.</summary>
    public Exception? FailWith { get; set; }

    public Task<PaymentResponse> RequestPaymentAsync(CreatePaymentRequest request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        if (FailWith is not null)
        {
            throw FailWith;
        }

        // Idempotente por orderId, como o Payments real.
        if (!paymentsByOrder.TryGetValue(request.OrderId, out var payment))
        {
            payment = new PaymentResponse(Guid.NewGuid(), request.OrderId, PaymentStatus.Pending, null, null);
            paymentsByOrder[request.OrderId] = payment;
        }

        return Task.FromResult(payment);
    }

    public Task<PaymentResponse?> GetPaymentAsync(Guid paymentId, CancellationToken cancellationToken)
    {
        if (FailWith is not null)
        {
            throw FailWith;
        }

        return Task.FromResult(paymentsByOrder.Values.FirstOrDefault(x => x.Id == paymentId));
    }
}
