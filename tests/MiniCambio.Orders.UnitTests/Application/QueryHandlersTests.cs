using Microsoft.Extensions.Time.Testing;
using MiniCambio.Orders.Api.Application;
using MiniCambio.Orders.Api.Application.Orders;
using MiniCambio.Orders.Api.Domain;

namespace MiniCambio.Orders.UnitTests.Application;

public class QueryHandlersTests
{
    private readonly FakeTimeProvider time = new(TestOrders.Now);
    private readonly InMemoryOrderRepository orders = new();

    [Fact]
    public async Task GetOrder_QuotedWithValidQuote_ReturnsAllFieldsAndFlags()
    {
        var order = orders.Seed(TestOrders.Quoted());
        var handler = new GetOrderHandler(orders, time);

        var response = await handler.HandleAsync(order.Id, CancellationToken.None);

        Assert.Equal(order.Id, response.Id);
        Assert.Equal(order.QuoteId, response.QuoteId);
        Assert.Equal(order.QuoteExpiresAt, response.QuoteExpiresAt);
        Assert.Equal(new BeneficiaryResponse("Maria Silva", "BR", "12345-6"), response.Beneficiary);
        Assert.Equal(OrderStatus.Quoted, response.Status);
        Assert.True(response.CanConfirm);
        Assert.True(response.CanCancel);
        Assert.Equal(Environment.MachineName, response.ServedBy);
    }

    [Fact]
    public async Task GetOrder_QuotedWithExpiredQuoteNotYetReconciled_CannotConfirmButCanCancel()
    {
        var order = orders.Seed(TestOrders.Quoted());
        time.Advance(TestOrders.QuoteTtl);
        var handler = new GetOrderHandler(orders, time);

        var response = await handler.HandleAsync(order.Id, CancellationToken.None);

        Assert.Equal(OrderStatus.Quoted, response.Status);
        Assert.False(response.CanConfirm);
        Assert.True(response.CanCancel);
    }

    [Fact]
    public async Task GetOrder_Failed_ReturnsFailureReason()
    {
        var order = orders.Seed(TestOrders.InStatus(OrderStatus.Failed));
        var handler = new GetOrderHandler(orders, time);

        var response = await handler.HandleAsync(order.Id, CancellationToken.None);

        Assert.Equal(OrderStatus.Failed, response.Status);
        Assert.Equal("BENEFICIARY_REJECTED", response.FailureReason);
        Assert.NotNull(response.PaymentId);
        Assert.False(response.CanConfirm);
        Assert.False(response.CanCancel);
    }

    [Fact]
    public async Task GetOrder_UnknownId_ThrowsResourceNotFound()
    {
        var handler = new GetOrderHandler(orders, time);

        await Assert.ThrowsAsync<ResourceNotFoundException>(() => handler.HandleAsync(Guid.NewGuid(), CancellationToken.None));
    }

    [Fact]
    public async Task ListOrders_NoParameters_UsesDefaultPagination()
    {
        orders.Seed(TestOrders.Quoted());
        var handler = new ListOrdersHandler(orders, time);

        var response = await handler.HandleAsync(new ListOrdersQuery(null, null, null), CancellationToken.None);

        Assert.Equal(1, response.Page);
        Assert.Equal(20, response.PageSize);
        Assert.Equal(1, response.TotalCount);
        Assert.Single(response.Items);
        Assert.Equal(Environment.MachineName, response.ServedBy);
    }

    [Fact]
    public async Task ListOrders_StatusInAnyCase_FiltersAndPaginatesNewestFirst()
    {
        var oldest = orders.Seed(TestOrders.InStatus(OrderStatus.Cancelled, TestOrders.Now));
        var middle = orders.Seed(TestOrders.InStatus(OrderStatus.Cancelled, TestOrders.Now.AddMinutes(1)));
        var newest = orders.Seed(TestOrders.InStatus(OrderStatus.Cancelled, TestOrders.Now.AddMinutes(2)));
        orders.Seed(TestOrders.Quoted(TestOrders.Now.AddMinutes(3)));
        var handler = new ListOrdersHandler(orders, time);

        var firstPage = await handler.HandleAsync(new ListOrdersQuery("cancelled", 1, 2), CancellationToken.None);
        var secondPage = await handler.HandleAsync(new ListOrdersQuery("CANCELLED", 2, 2), CancellationToken.None);

        Assert.Equal(3, firstPage.TotalCount);
        Assert.Equal([newest.Id, middle.Id], firstPage.Items.Select(x => x.Id));
        Assert.Equal([oldest.Id], secondPage.Items.Select(x => x.Id));
        Assert.Equal(2, secondPage.Page);
        Assert.Equal(2, secondPage.PageSize);
    }
}
