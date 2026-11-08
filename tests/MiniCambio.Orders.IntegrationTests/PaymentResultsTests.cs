using System.Text.Json;
using MiniCambio.Orders.Api.Application.Payments;

namespace MiniCambio.Orders.IntegrationTests;

/// <summary>
/// Resultado de pagamento chegando pelo Kafka (entregue direto ao handler, como o consumidor faria) e gravado no
/// Postgres real. Os casos de borda do handler ficam nos testes unitários.
/// </summary>
public class PaymentResultsTests(OrdersApiFactory factory)
{
    private readonly HttpClient client = factory.CreateClient();

    [Fact]
    public async Task PaymentCompleted_MarksOrderCompletedWithPaymentId()
    {
        var id = await CreatePaymentRequestedOrderAsync();
        var paymentId = Guid.NewGuid();

        var outcome = await factory.ApplyPaymentResultAsync(
            new PaymentResultMessage(paymentId, id, PaymentStatus.Completed, null, "MOCK-123"));

        Assert.Equal(PaymentResultOutcome.Applied, outcome);
        var order = await client.GetOrderAsync(id);
        Assert.Equal("Completed", order.Status());
        Assert.Equal(paymentId, order.GetProperty("paymentId").GetGuid());
        Assert.Equal(JsonValueKind.Null, order.GetProperty("failureReason").ValueKind);
    }

    [Fact]
    public async Task PaymentFailed_MarksOrderFailedWithPaymentsReason()
    {
        var id = await CreatePaymentRequestedOrderAsync();

        await factory.ApplyPaymentResultAsync(
            new PaymentResultMessage(Guid.NewGuid(), id, PaymentStatus.Failed, "BENEFICIARY_REJECTED", null));

        var order = await client.GetOrderAsync(id);
        Assert.Equal("Failed", order.Status());
        Assert.Equal("BENEFICIARY_REJECTED", order.GetProperty("failureReason").GetString());
    }

    [Fact]
    public async Task SameResultTwice_SecondIsIgnored()
    {
        var id = await CreatePaymentRequestedOrderAsync();
        var message = new PaymentResultMessage(Guid.NewGuid(), id, PaymentStatus.Completed, null, "MOCK-123");
        await factory.ApplyPaymentResultAsync(message);
        var before = await client.GetOrderAsync(id);

        var outcome = await factory.ApplyPaymentResultAsync(message);

        Assert.Equal(PaymentResultOutcome.AlreadyFinished, outcome);
        var after = await client.GetOrderAsync(id);
        Assert.Equal("Completed", after.Status());
        Assert.Equal(before.GetProperty("updatedAt").GetDateTimeOffset(), after.GetProperty("updatedAt").GetDateTimeOffset());
    }

    private async Task<Guid> CreatePaymentRequestedOrderAsync()
    {
        var created = await client.CreateOrderAsync();
        var confirmed = await (await client.ConfirmOrderAsync(created.Id())).ReadJsonAsync();
        Assert.Equal("PaymentRequested", confirmed.Status());
        return created.Id();
    }
}
