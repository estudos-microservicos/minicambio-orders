using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using MiniCambio.Orders.Api.Application;
using MiniCambio.Orders.Api.Application.Orders;
using MiniCambio.Orders.Api.Domain;

namespace MiniCambio.Orders.UnitTests.Application;

public class CancelOrderHandlerTests
{
    private readonly FakeTimeProvider time = new(TestOrders.Now);
    private readonly InMemoryOrderRepository orders = new();
    private readonly CancelOrderHandler handler;

    public CancelOrderHandlerTests()
    {
        handler = new CancelOrderHandler(orders, time, NullLogger<CancelOrderHandler>.Instance);
    }

    [Fact]
    public async Task HandleAsync_Quoted_CancelsAndSaves()
    {
        var order = orders.Seed(TestOrders.Quoted());
        time.Advance(TimeSpan.FromSeconds(5));

        await handler.HandleAsync(order.Id, CancellationToken.None);

        Assert.Equal(OrderStatus.Cancelled, orders.PersistedStatus(order.Id));
        Assert.Equal(time.GetUtcNow(), order.CancelledAt);
    }

    [Fact]
    public async Task HandleAsync_AlreadyCancelled_DoesNothing()
    {
        var order = orders.Seed(TestOrders.InStatus(OrderStatus.Cancelled));
        var cancelledAt = order.CancelledAt;
        time.Advance(TimeSpan.FromMinutes(1));

        await handler.HandleAsync(order.Id, CancellationToken.None);

        Assert.Equal(OrderStatus.Cancelled, order.Status);
        Assert.Equal(cancelledAt, order.CancelledAt);
        Assert.Equal(0, orders.SaveCount);
    }

    [Theory]
    [InlineData(OrderStatus.Confirmed)]
    [InlineData(OrderStatus.PaymentRequested)]
    [InlineData(OrderStatus.Completed)]
    [InlineData(OrderStatus.Failed)]
    [InlineData(OrderStatus.Expired)]
    public async Task HandleAsync_NotQuoted_ThrowsDomainExceptionAndKeepsStatus(OrderStatus status)
    {
        var order = orders.Seed(TestOrders.InStatus(status));

        var exception = await Assert.ThrowsAsync<DomainException>(() => handler.HandleAsync(order.Id, CancellationToken.None));

        Assert.Equal("Só é possível cancelar ordens que ainda não foram confirmadas.", exception.Message);
        Assert.Equal(status, orders.PersistedStatus(order.Id));
        Assert.Equal(0, orders.SaveCount);
    }

    [Fact]
    public async Task HandleAsync_UnknownOrder_ThrowsResourceNotFound()
    {
        await Assert.ThrowsAsync<ResourceNotFoundException>(() => handler.HandleAsync(Guid.NewGuid(), CancellationToken.None));
    }
}
