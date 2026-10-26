using System.Net;
using System.Text.Json;
using MiniCambio.Orders.Api.Application;
using MiniCambio.Orders.Api.Application.Payments;
using MiniCambio.Orders.Api.Infrastructure.Http;

namespace MiniCambio.Orders.UnitTests.Infrastructure;

public class PaymentsClientTests
{
    private static readonly Guid PaymentId = Guid.Parse("0199a1b2-0000-7000-8000-0000000000aa");
    private static readonly Guid OrderId = Guid.Parse("0199a1b2-0000-7000-8000-0000000000bb");

    private static readonly CreatePaymentRequest Request =
        new(OrderId, "USD", 100m, new PaymentBeneficiary("Maria Silva", "BR", "12345-6"));

    [Theory]
    [InlineData(HttpStatusCode.Accepted)]
    [InlineData(HttpStatusCode.OK)]
    public async Task RequestPaymentAsync_AcceptedOrExisting_PostsContractAndReturnsPayment(HttpStatusCode status)
    {
        var http = StubHttpMessageHandler.Returning(status, PaymentJson("Pending", failureReason: null));
        var client = new PaymentsClient(http.CreateClient());

        var payment = await client.RequestPaymentAsync(Request, TestContext.Current.CancellationToken);

        var sent = Assert.Single(http.Requests);
        Assert.Equal(HttpMethod.Post, sent.Method);
        Assert.Equal("/api/v1/payments", sent.RequestUri?.AbsolutePath);
        var body = JsonDocument.Parse(Assert.Single(http.RequestBodies)!).RootElement;
        Assert.Equal(OrderId, body.GetProperty("orderId").GetGuid());
        Assert.Equal("USD", body.GetProperty("currency").GetString());
        Assert.Equal(100m, body.GetProperty("foreignAmount").GetDecimal());
        var beneficiary = body.GetProperty("beneficiary");
        Assert.Equal("Maria Silva", beneficiary.GetProperty("name").GetString());
        Assert.Equal("BR", beneficiary.GetProperty("country").GetString());
        Assert.Equal("12345-6", beneficiary.GetProperty("accountNumber").GetString());

        Assert.Equal(PaymentId, payment.Id);
        Assert.Equal(OrderId, payment.OrderId);
        Assert.Equal(PaymentStatus.Pending, payment.Status);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadRequest)]
    public async Task RequestPaymentAsync_ErrorStatus_ThrowsServiceUnavailable(HttpStatusCode status)
    {
        var client = new PaymentsClient(StubHttpMessageHandler.Returning(status).CreateClient());

        await Assert.ThrowsAsync<ServiceUnavailableException>(
            () => client.RequestPaymentAsync(Request, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RequestPaymentAsync_NetworkFailure_ThrowsServiceUnavailable()
    {
        var client = new PaymentsClient(StubHttpMessageHandler.Throwing(new HttpRequestException("refused")).CreateClient());

        await Assert.ThrowsAsync<ServiceUnavailableException>(
            () => client.RequestPaymentAsync(Request, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task GetPaymentAsync_Failed_ReturnsStatusAndFailureReason()
    {
        var http = StubHttpMessageHandler.Returning(HttpStatusCode.OK, PaymentJson("Failed", "COMPLIANCE_BLOCKED"));
        var client = new PaymentsClient(http.CreateClient());

        var payment = await client.GetPaymentAsync(PaymentId, TestContext.Current.CancellationToken);

        Assert.Equal($"/api/v1/payments/{PaymentId}", Assert.Single(http.Requests).RequestUri?.AbsolutePath);
        Assert.NotNull(payment);
        Assert.Equal(PaymentStatus.Failed, payment.Status);
        Assert.Equal("COMPLIANCE_BLOCKED", payment.FailureReason);
    }

    [Fact]
    public async Task GetPaymentAsync_NotFound_ReturnsNull()
    {
        var client = new PaymentsClient(StubHttpMessageHandler.Returning(HttpStatusCode.NotFound).CreateClient());

        Assert.Null(await client.GetPaymentAsync(PaymentId, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task GetPaymentAsync_ServerError_ThrowsServiceUnavailable()
    {
        var client = new PaymentsClient(StubHttpMessageHandler.Returning(HttpStatusCode.BadGateway).CreateClient());

        await Assert.ThrowsAsync<ServiceUnavailableException>(
            () => client.GetPaymentAsync(PaymentId, TestContext.Current.CancellationToken));
    }

    private static string PaymentJson(string status, string? failureReason) => $$"""
        {
          "id": "{{PaymentId}}",
          "orderId": "{{OrderId}}",
          "currency": "USD",
          "foreignAmount": 100.00,
          "beneficiary": { "name": "Maria Silva", "country": "BR", "accountNumber": "12345-6" },
          "status": "{{status}}",
          "failureReason": {{(failureReason is null ? "null" : $"\"{failureReason}\"")}},
          "providerReference": null,
          "processingStartedAt": null,
          "createdAt": "2026-05-10T12:00:00+00:00",
          "updatedAt": "2026-05-10T12:00:00+00:00",
          "servedBy": "payments-1"
        }
        """;
}
