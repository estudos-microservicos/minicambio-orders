using System.Collections.Concurrent;
using MiniCambio.Orders.Api.Application;
using MiniCambio.Orders.Api.Application.Payments;
using MiniCambio.Orders.Api.Application.Rates;
using MiniCambio.Orders.Api.Infrastructure.Correlation;
using MiniCambio.Orders.Api.Infrastructure.Http;
using MiniCambio.Orders.Api.Infrastructure.Messaging;

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
/// Publicador falso no lugar do Kafka: guarda cada pedido de pagamento publicado e pode ser "desligado" com
/// <see cref="Unavailable"/>, como o Kafka fora do ar.
/// </summary>
public sealed class FakePaymentRequestPublisher(CorrelationIdAccessor correlationIdAccessor) : IPaymentRequestPublisher
{
    public bool Unavailable { get; set; }

    /// <summary>Atraso artificial em cada publicação (para testar ciclos concorrentes).</summary>
    public TimeSpan Delay { get; set; }

    public ConcurrentQueue<PaymentRequestedMessage> Messages { get; } = new();

    /// <summary>Correlation id visto em cada publicação (vem do <see cref="CorrelationIdAccessor"/>).</summary>
    public ConcurrentBag<string?> CorrelationIds { get; } = [];

    public int CountFor(Guid orderId) => Messages.Count(x => x.OrderId == orderId);

    public async Task PublishAsync(PaymentRequestedMessage message, CancellationToken cancellationToken)
    {
        CorrelationIds.Add(correlationIdAccessor.CorrelationId);
        if (Delay > TimeSpan.Zero)
        {
            await Task.Delay(Delay, cancellationToken);
        }

        if (Unavailable)
        {
            throw new ServiceUnavailableException(KafkaPaymentRequestPublisher.UnavailableMessage);
        }

        Messages.Enqueue(message);
    }
}
