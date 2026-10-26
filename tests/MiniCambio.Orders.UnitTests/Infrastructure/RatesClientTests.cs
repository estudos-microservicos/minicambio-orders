using System.Net;
using System.Text.Json;
using MiniCambio.Orders.Api.Application;
using MiniCambio.Orders.Api.Application.Rates;
using MiniCambio.Orders.Api.Domain;
using MiniCambio.Orders.Api.Infrastructure.Http;
using Polly.CircuitBreaker;
using Polly.Timeout;

namespace MiniCambio.Orders.UnitTests.Infrastructure;

public class RatesClientTests
{
    private const string QuoteJson = """
        {
          "id": "0199a1b2-0000-7000-8000-000000000001",
          "currency": "USD",
          "side": "Buy",
          "foreignAmount": 100.00,
          "rate": 5.427,
          "brlAmount": 542.70,
          "createdAt": "2026-05-10T12:00:00+00:00",
          "expiresAt": "2026-05-10T12:01:00+00:00",
          "isExpired": false,
          "servedBy": "rates-1"
        }
        """;

    private static readonly CreateQuoteRequest Request = new("USD", OrderSide.Buy, 100m);

    [Fact]
    public async Task CreateQuoteAsync_Created_PostsCamelCaseJsonAndReturnsQuote()
    {
        var http = StubHttpMessageHandler.Returning(HttpStatusCode.Created, QuoteJson);
        var client = new RatesClient(http.CreateClient());

        var quote = await client.CreateQuoteAsync(Request, TestContext.Current.CancellationToken);

        var sent = Assert.Single(http.Requests);
        Assert.Equal(HttpMethod.Post, sent.Method);
        Assert.Equal("/api/v1/quotes", sent.RequestUri?.AbsolutePath);
        var body = JsonDocument.Parse(Assert.Single(http.RequestBodies)!).RootElement;
        Assert.Equal("USD", body.GetProperty("currency").GetString());
        Assert.Equal("Buy", body.GetProperty("side").GetString());
        Assert.Equal(100m, body.GetProperty("foreignAmount").GetDecimal());

        Assert.Equal(Guid.Parse("0199a1b2-0000-7000-8000-000000000001"), quote.Id);
        Assert.Equal(OrderSide.Buy, quote.Side);
        Assert.Equal(5.427m, quote.Rate);
        Assert.Equal(542.70m, quote.BrlAmount);
        Assert.Equal(new DateTimeOffset(2026, 5, 10, 12, 1, 0, TimeSpan.Zero), quote.ExpiresAt);
    }

    [Theory]
    [InlineData(HttpStatusCode.UnprocessableEntity)]
    [InlineData(HttpStatusCode.BadRequest)]
    public async Task CreateQuoteAsync_RatesRefuses_ThrowsQuoteRejectedWithRatesDetail(HttpStatusCode status)
    {
        var problem = """{ "title": "Operação não permitida", "status": 422, "detail": "A moeda XYZ não é suportada." }""";
        var client = new RatesClient(StubHttpMessageHandler.Returning(status, problem).CreateClient());

        var exception = await Assert.ThrowsAsync<QuoteRejectedException>(
            () => client.CreateQuoteAsync(Request, TestContext.Current.CancellationToken));

        Assert.Equal("A moeda XYZ não é suportada.", exception.Message);
    }

    [Fact]
    public async Task CreateQuoteAsync_RatesRefusesWithoutProblemDetails_ThrowsQuoteRejectedWithFallbackMessage()
    {
        var client = new RatesClient(StubHttpMessageHandler.Returning(HttpStatusCode.UnprocessableEntity, "não é json").CreateClient());

        var exception = await Assert.ThrowsAsync<QuoteRejectedException>(
            () => client.CreateQuoteAsync(Request, TestContext.Current.CancellationToken));

        Assert.Equal(RatesClient.RejectedFallbackMessage, exception.Message);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.NotFound)]
    public async Task CreateQuoteAsync_ErrorStatus_ThrowsServiceUnavailable(HttpStatusCode status)
    {
        var client = new RatesClient(StubHttpMessageHandler.Returning(status).CreateClient());

        var exception = await Assert.ThrowsAsync<ServiceUnavailableException>(
            () => client.CreateQuoteAsync(Request, TestContext.Current.CancellationToken));

        Assert.Equal(RatesClient.UnavailableMessage, exception.Message);
    }

    public static TheoryData<Exception> TransportFailures() =>
    [
        new HttpRequestException("Connection refused"),
        new TimeoutRejectedException("timeout da tentativa"),
        new BrokenCircuitException("circuito aberto"),
        new TaskCanceledException("HttpClient.Timeout"),
    ];

    [Theory]
    [MemberData(nameof(TransportFailures))]
    public async Task CreateQuoteAsync_TransportFailure_ThrowsServiceUnavailable(Exception failure)
    {
        var client = new RatesClient(StubHttpMessageHandler.Throwing(failure).CreateClient());

        var exception = await Assert.ThrowsAsync<ServiceUnavailableException>(
            () => client.CreateQuoteAsync(Request, TestContext.Current.CancellationToken));

        Assert.Same(failure, exception.InnerException);
    }

    [Fact]
    public async Task CreateQuoteAsync_InvalidJsonBody_ThrowsServiceUnavailable()
    {
        var client = new RatesClient(StubHttpMessageHandler.Returning(HttpStatusCode.Created, "{ quebrado").CreateClient());

        await Assert.ThrowsAsync<ServiceUnavailableException>(
            () => client.CreateQuoteAsync(Request, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CreateQuoteAsync_CallerCancels_PropagatesCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        var client = new RatesClient(StubHttpMessageHandler.Returning(HttpStatusCode.Created, QuoteJson).CreateClient());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.CreateQuoteAsync(Request, cancellation.Token));
    }
}
