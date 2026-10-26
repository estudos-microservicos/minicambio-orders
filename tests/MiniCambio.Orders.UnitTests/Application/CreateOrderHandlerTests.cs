using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using MiniCambio.Orders.Api.Application;
using MiniCambio.Orders.Api.Application.Orders;
using MiniCambio.Orders.Api.Application.Rates;
using MiniCambio.Orders.Api.Domain;

namespace MiniCambio.Orders.UnitTests.Application;

public class CreateOrderHandlerTests
{
    private readonly FakeTimeProvider time = new(TestOrders.Now);
    private readonly InMemoryOrderRepository orders = new();
    private readonly FakeRatesClient rates;
    private readonly CreateOrderHandler handler;

    public CreateOrderHandlerTests()
    {
        rates = new FakeRatesClient(time);
        handler = new CreateOrderHandler(orders, rates, time, NullLogger<CreateOrderHandler>.Instance);
    }

    [Fact]
    public async Task HandleAsync_NewKey_QuotesAndPersistsQuotedOrder()
    {
        var result = await handler.HandleAsync("chave-1", ValidRequest(side: "buy"), CancellationToken.None);

        Assert.True(result.Created);
        var stored = Assert.Single(orders.Orders);
        Assert.Equal(OrderStatus.Quoted, orders.PersistedStatus(stored.Id));
        Assert.Equal("chave-1", stored.IdempotencyKey);

        var quoteRequest = Assert.Single(rates.Requests);
        Assert.Equal(new CreateQuoteRequest("USD", OrderSide.Buy, 100m), quoteRequest);

        var order = result.Order;
        Assert.Equal(stored.Id, order.Id);
        Assert.Equal(OrderStatus.Quoted, order.Status);
        Assert.Equal(OrderSide.Buy, order.Side);
        Assert.Equal(FakeRatesClient.Rate, order.Rate);
        Assert.Equal(542.70m, order.BrlAmount);
        Assert.Equal(time.GetUtcNow().AddSeconds(60), order.QuoteExpiresAt);
        Assert.Equal(new BeneficiaryResponse("Maria Silva", "BR", "12345-6"), order.Beneficiary);
        Assert.Null(order.PaymentId);
        Assert.True(order.CanConfirm);
        Assert.True(order.CanCancel);
        Assert.False(string.IsNullOrWhiteSpace(order.ServedBy));
    }

    [Fact]
    public async Task HandleAsync_SameKeyTwice_ReturnsExistingOrderWithoutQuotingAgain()
    {
        var first = await handler.HandleAsync("chave-repetida", ValidRequest(), CancellationToken.None);

        var second = await handler.HandleAsync("chave-repetida", ValidRequest(), CancellationToken.None);

        Assert.False(second.Created);
        Assert.Equal(first.Order.Id, second.Order.Id);
        Assert.Single(orders.Orders);
        Assert.Single(rates.Requests);
    }

    [Fact]
    public async Task HandleAsync_ConcurrentInsertWithSameKey_ReturnsWinningOrder()
    {
        var winner = TestOrders.Quoted(idempotencyKey: "chave-disputada");
        orders.ConcurrentInsertOnNextAdd = winner;

        var result = await handler.HandleAsync("chave-disputada", ValidRequest(), CancellationToken.None);

        Assert.False(result.Created);
        Assert.Equal(winner.Id, result.Order.Id);
        Assert.Equal(winner.Id, Assert.Single(orders.Orders).Id);
    }

    [Fact]
    public async Task HandleAsync_RatesUnavailable_ThrowsServiceUnavailableAndPersistsNothing()
    {
        rates.FailWith = new ServiceUnavailableException("Rates fora");

        await Assert.ThrowsAsync<ServiceUnavailableException>(
            () => handler.HandleAsync("chave-1", ValidRequest(), CancellationToken.None));

        Assert.Empty(orders.Orders);
        Assert.Equal(0, orders.SaveCount);
    }

    [Fact]
    public async Task HandleAsync_RatesRejectsQuote_ThrowsQuoteRejectedWithRatesMessageAndPersistsNothing()
    {
        rates.FailWith = new QuoteRejectedException("A moeda XYZ não é suportada.");

        var exception = await Assert.ThrowsAsync<QuoteRejectedException>(
            () => handler.HandleAsync("chave-1", ValidRequest(currency: "XYZ"), CancellationToken.None));

        // QuoteRejectedException é DomainException: vira 422 com a mensagem do Rates.
        Assert.IsAssignableFrom<DomainException>(exception);
        Assert.Equal("A moeda XYZ não é suportada.", exception.Message);
        Assert.Empty(orders.Orders);
    }

    [Fact]
    public async Task HandleAsync_RatesUnavailableThenAvailable_SameKeyCreatesOrder()
    {
        rates.FailWith = new ServiceUnavailableException("Rates fora");
        await Assert.ThrowsAsync<ServiceUnavailableException>(
            () => handler.HandleAsync("chave-1", ValidRequest(), CancellationToken.None));

        rates.FailWith = null;
        var result = await handler.HandleAsync("chave-1", ValidRequest(), CancellationToken.None);

        Assert.True(result.Created);
        Assert.Single(orders.Orders);
    }

    private static CreateOrderRequest ValidRequest(string currency = "USD", string side = "Buy") =>
        new(currency, side, 100m, new BeneficiaryRequest("Maria Silva", "BR", "12345-6"));
}
