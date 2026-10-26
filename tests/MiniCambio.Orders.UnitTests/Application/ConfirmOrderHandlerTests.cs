using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using MiniCambio.Orders.Api.Application;
using MiniCambio.Orders.Api.Application.Orders;
using MiniCambio.Orders.Api.Application.Payments;
using MiniCambio.Orders.Api.Domain;

namespace MiniCambio.Orders.UnitTests.Application;

public class ConfirmOrderHandlerTests
{
    private readonly FakeTimeProvider time = new(TestOrders.Now);
    private readonly InMemoryOrderRepository orders = new();
    private readonly FakePaymentsClient payments = new();
    private readonly ConfirmOrderHandler handler;

    public ConfirmOrderHandlerTests()
    {
        handler = new ConfirmOrderHandler(orders, payments, time, NullLogger<ConfirmOrderHandler>.Instance);
    }

    [Fact]
    public async Task HandleAsync_QuotedWithValidQuote_ConfirmsAndRequestsPayment()
    {
        var order = orders.Seed(TestOrders.Quoted());
        time.Advance(TimeSpan.FromSeconds(10));

        var response = await handler.HandleAsync(order.Id, CancellationToken.None);

        var request = Assert.Single(payments.Requests);
        Assert.Equal(
            new CreatePaymentRequest(order.Id, "USD", 100m, new PaymentBeneficiary("Maria Silva", "BR", "12345-6")),
            request);

        Assert.Equal(OrderStatus.PaymentRequested, response.Status);
        Assert.NotNull(response.PaymentId);
        Assert.Equal(time.GetUtcNow(), response.ConfirmedAt);
        Assert.False(response.CanConfirm);
        Assert.False(response.CanCancel);
        Assert.Equal(OrderStatus.PaymentRequested, orders.PersistedStatus(order.Id));
    }

    [Fact]
    public async Task HandleAsync_PaymentsUnavailable_KeepsConfirmedAndReturnsOrder()
    {
        var order = orders.Seed(TestOrders.Quoted());
        payments.FailWith = new ServiceUnavailableException("Payments fora");

        var response = await handler.HandleAsync(order.Id, CancellationToken.None);

        Assert.Single(payments.Requests);
        Assert.Equal(OrderStatus.Confirmed, response.Status);
        Assert.Null(response.PaymentId);
        Assert.False(response.CanConfirm);
        Assert.False(response.CanCancel);
        Assert.Equal(OrderStatus.Confirmed, orders.PersistedStatus(order.Id));
    }

    [Theory]
    [InlineData(OrderStatus.Confirmed)]
    [InlineData(OrderStatus.PaymentRequested)]
    [InlineData(OrderStatus.Completed)]
    public async Task HandleAsync_AlreadyConfirmed_ReturnsOrderWithoutChanges(OrderStatus status)
    {
        var order = orders.Seed(TestOrders.InStatus(status));

        var response = await handler.HandleAsync(order.Id, CancellationToken.None);

        Assert.Equal(status, response.Status);
        Assert.Equal(order.PaymentId, response.PaymentId);
        Assert.Empty(payments.Requests);
        Assert.Equal(0, orders.SaveCount);
    }

    [Fact]
    public async Task HandleAsync_QuoteExpired_MarksExpiredSavesAndThrowsQuoteExpired()
    {
        var order = orders.Seed(TestOrders.Quoted());
        time.Advance(TestOrders.QuoteTtl);

        var exception = await Assert.ThrowsAsync<QuoteExpiredException>(
            () => handler.HandleAsync(order.Id, CancellationToken.None));

        Assert.Equal("A cotação expirou. Crie uma nova ordem.", exception.Message);
        Assert.Equal(OrderStatus.Expired, orders.PersistedStatus(order.Id));
        Assert.Empty(payments.Requests);
    }

    [Theory]
    [InlineData(OrderStatus.Cancelled, "Esta ordem foi cancelada e não pode mais ser confirmada.")]
    [InlineData(OrderStatus.Expired, "A cotação expirou. Crie uma nova ordem.")]
    [InlineData(OrderStatus.Failed, "O pagamento desta ordem falhou. Crie uma nova ordem.")]
    public async Task HandleAsync_CancelledExpiredOrFailed_ThrowsDomainExceptionWithoutChanges(OrderStatus status, string expectedMessage)
    {
        var order = orders.Seed(TestOrders.InStatus(status));
        time.Advance(TimeSpan.FromMinutes(5));

        var exception = await Assert.ThrowsAnyAsync<DomainException>(
            () => handler.HandleAsync(order.Id, CancellationToken.None));

        Assert.Equal(expectedMessage, exception.Message);
        Assert.Equal(status, orders.PersistedStatus(order.Id));
        Assert.Empty(payments.Requests);
    }

    [Fact]
    public async Task HandleAsync_UnknownOrder_ThrowsResourceNotFound()
    {
        var exception = await Assert.ThrowsAsync<ResourceNotFoundException>(
            () => handler.HandleAsync(Guid.NewGuid(), CancellationToken.None));

        Assert.Equal("Ordem não encontrada.", exception.Message);
    }
}
