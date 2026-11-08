using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using MiniCambio.Orders.Api.Application.Payments;
using MiniCambio.Orders.Api.Domain;

namespace MiniCambio.Orders.UnitTests.Application;

public class PaymentResultHandlerTests
{
    private readonly FakeTimeProvider time = new(TestOrders.Now.AddMinutes(1));
    private readonly InMemoryOrderRepository orders = new();
    private readonly PaymentResultHandler handler;

    public PaymentResultHandlerTests()
    {
        handler = new PaymentResultHandler(orders, time, NullLogger<PaymentResultHandler>.Instance);
    }

    [Fact]
    public async Task HandleAsync_Completed_MarksOrderCompletedWithPaymentId()
    {
        var order = orders.Seed(TestOrders.InStatus(OrderStatus.PaymentRequested));
        var paymentId = Guid.NewGuid();

        var outcome = await handler.HandleAsync(Completed(order.Id, paymentId), CancellationToken.None);

        Assert.Equal(PaymentResultOutcome.Applied, outcome);
        Assert.Equal(OrderStatus.Completed, orders.PersistedStatus(order.Id));
        Assert.Equal(paymentId, order.PaymentId);
        Assert.Equal(time.GetUtcNow(), order.UpdatedAt);
    }

    [Fact]
    public async Task HandleAsync_Failed_MarksOrderFailedWithReason()
    {
        var order = orders.Seed(TestOrders.InStatus(OrderStatus.PaymentRequested));

        var outcome = await handler.HandleAsync(
            new PaymentResultMessage(Guid.NewGuid(), order.Id, PaymentStatus.Failed, "LIMIT_EXCEEDED", null),
            CancellationToken.None);

        Assert.Equal(PaymentResultOutcome.Applied, outcome);
        Assert.Equal(OrderStatus.Failed, orders.PersistedStatus(order.Id));
        Assert.Equal("LIMIT_EXCEEDED", order.FailureReason);
    }

    [Fact]
    public async Task HandleAsync_FailedWithoutReason_UsesGenericReason()
    {
        var order = orders.Seed(TestOrders.InStatus(OrderStatus.PaymentRequested));

        await handler.HandleAsync(
            new PaymentResultMessage(Guid.NewGuid(), order.Id, PaymentStatus.Failed, null, null), CancellationToken.None);

        Assert.Equal(PaymentResultHandler.UnknownFailureReason, order.FailureReason);
    }

    [Fact]
    public async Task HandleAsync_OrderStillConfirmed_RegistersRequestAndCompletes()
    {
        // O resultado chegou antes de o Orders gravar PaymentRequested (corrida entre publicar e gravar).
        var order = orders.Seed(TestOrders.InStatus(OrderStatus.Confirmed));

        var outcome = await handler.HandleAsync(Completed(order.Id, Guid.NewGuid()), CancellationToken.None);

        Assert.Equal(PaymentResultOutcome.Applied, outcome);
        Assert.Equal(OrderStatus.Completed, orders.PersistedStatus(order.Id));
    }

    [Theory]
    [InlineData(OrderStatus.Completed)]
    [InlineData(OrderStatus.Failed)]
    public async Task HandleAsync_OrderAlreadyFinished_ChangesNothing(OrderStatus status)
    {
        var order = orders.Seed(TestOrders.InStatus(status));
        var paymentIdBefore = order.PaymentId;

        var outcome = await handler.HandleAsync(Completed(order.Id, Guid.NewGuid()), CancellationToken.None);

        Assert.Equal(PaymentResultOutcome.AlreadyFinished, outcome);
        Assert.Equal(status, order.Status);
        Assert.Equal(paymentIdBefore, order.PaymentId);
        Assert.Equal(0, orders.SaveCount);
    }

    [Theory]
    [InlineData(OrderStatus.Quoted)]
    [InlineData(OrderStatus.Cancelled)]
    [InlineData(OrderStatus.Expired)]
    public async Task HandleAsync_OrderNotWaitingForPayment_IsIgnored(OrderStatus status)
    {
        var order = orders.Seed(TestOrders.InStatus(status));

        var outcome = await handler.HandleAsync(Completed(order.Id, Guid.NewGuid()), CancellationToken.None);

        Assert.Equal(PaymentResultOutcome.Ignored, outcome);
        Assert.Equal(status, order.Status);
        Assert.Equal(0, orders.SaveCount);
    }

    [Theory]
    [InlineData(PaymentStatus.Pending)]
    [InlineData(PaymentStatus.Processing)]
    public async Task HandleAsync_NonFinalPaymentStatus_IsIgnored(PaymentStatus paymentStatus)
    {
        var order = orders.Seed(TestOrders.InStatus(OrderStatus.PaymentRequested));

        var outcome = await handler.HandleAsync(
            new PaymentResultMessage(Guid.NewGuid(), order.Id, paymentStatus, null, null), CancellationToken.None);

        Assert.Equal(PaymentResultOutcome.Ignored, outcome);
        Assert.Equal(OrderStatus.PaymentRequested, order.Status);
    }

    [Fact]
    public async Task HandleAsync_UnknownOrder_IsIgnored()
    {
        var outcome = await handler.HandleAsync(Completed(Guid.NewGuid(), Guid.NewGuid()), CancellationToken.None);

        Assert.Equal(PaymentResultOutcome.Ignored, outcome);
        Assert.Equal(0, orders.SaveCount);
    }

    private static PaymentResultMessage Completed(Guid orderId, Guid paymentId) =>
        new(paymentId, orderId, PaymentStatus.Completed, null, "MOCK-123");
}
