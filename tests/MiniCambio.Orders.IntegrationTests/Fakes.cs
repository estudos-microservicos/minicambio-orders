using System.Collections.Concurrent;
using MiniCambio.Orders.Api.Application;
using MiniCambio.Orders.Api.Application.Payments;
using MiniCambio.Orders.Api.Application.Rates;
using MiniCambio.Orders.Api.Infrastructure.Correlation;
using MiniCambio.Orders.Api.Infrastructure.Http;

namespace MiniCambio.Orders.IntegrationTests;

/// <summary>
/// Rates falso: cota com taxa fixa e validade de 60s a partir do <see cref="TimeProvider"/> da API.
/// Recusa a moeda <c>XYZ</c> como o Rates real e pode ser "desligado" com <see cref="Unavailable"/>.
/// </summary>
public sealed class FakeRatesClient(TimeProvider timeProvider) : IRatesClient
{
    public const decimal Rate = 5.427m;
    public const string UnsupportedCurrency = "XYZ";

    public bool Unavailable { get; set; }

    public Task<QuoteResponse> CreateQuoteAsync(CreateQuoteRequest request, CancellationToken cancellationToken)
    {
        if (Unavailable)
        {
            throw new ServiceUnavailableException(RatesClient.UnavailableMessage);
        }

        if (request.Currency == UnsupportedCurrency)
        {
            throw new QuoteRejectedException($"A moeda {request.Currency} não é suportada.");
        }

        var now = timeProvider.GetUtcNow();
        return Task.FromResult(new QuoteResponse(
            Guid.CreateVersion7(),
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

/// <summary>
/// Payments falso, idempotente por <c>orderId</c>. Os testes definem o resultado de cada pagamento com
/// <see cref="SetStatus"/> e podem "desligá-lo" com <see cref="Unavailable"/>.
/// </summary>
public sealed class FakePaymentsClient(CorrelationIdAccessor correlationIdAccessor) : IPaymentsClient
{
    private readonly ConcurrentDictionary<Guid, PaymentResponse> paymentsByOrder = new();
    private int requestCount;

    public bool Unavailable { get; set; }

    /// <summary>Atraso artificial em cada chamada (para testar ciclos concorrentes).</summary>
    public TimeSpan Delay { get; set; }

    public int RequestCount => requestCount;

    /// <summary>Correlation id visto em cada chamada (vem do <see cref="CorrelationIdAccessor"/>).</summary>
    public ConcurrentBag<string?> CorrelationIds { get; } = [];

    public ConcurrentDictionary<Guid, int> RequestsPerOrder { get; } = new();

    public async Task<PaymentResponse> RequestPaymentAsync(CreatePaymentRequest request, CancellationToken cancellationToken)
    {
        await SimulateCallAsync(cancellationToken);
        Interlocked.Increment(ref requestCount);
        RequestsPerOrder.AddOrUpdate(request.OrderId, 1, (_, count) => count + 1);

        return paymentsByOrder.GetOrAdd(
            request.OrderId,
            orderId => new PaymentResponse(Guid.CreateVersion7(), orderId, PaymentStatus.Pending, null, null));
    }

    public async Task<PaymentResponse?> GetPaymentAsync(Guid paymentId, CancellationToken cancellationToken)
    {
        await SimulateCallAsync(cancellationToken);
        return paymentsByOrder.Values.FirstOrDefault(x => x.Id == paymentId);
    }

    public void SetStatus(Guid orderId, PaymentStatus status, string? failureReason = null) =>
        paymentsByOrder[orderId] = paymentsByOrder[orderId] with { Status = status, FailureReason = failureReason };

    private async Task SimulateCallAsync(CancellationToken cancellationToken)
    {
        CorrelationIds.Add(correlationIdAccessor.CorrelationId);
        if (Delay > TimeSpan.Zero)
        {
            await Task.Delay(Delay, cancellationToken);
        }

        if (Unavailable)
        {
            throw new ServiceUnavailableException(PaymentsClient.UnavailableMessage);
        }
    }
}
