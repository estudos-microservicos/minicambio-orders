using System.Text.Json;
using MiniCambio.Orders.Api.Application.Payments;
using MiniCambio.Orders.Api.Infrastructure.Reconciliation;

namespace MiniCambio.Orders.IntegrationTests;

/// <summary>
/// Ciclo de reconciliação contra o Postgres real (inclui o <c>FOR UPDATE SKIP LOCKED</c>), com relógio falso.
/// Cada teste usa um banco próprio, já que o ciclo processa todas as ordens do banco.
/// </summary>
public class OrderReconciliationCycleTests(OrdersApiFactory factory)
{
    [Fact]
    public async Task RunOnceAsync_QuotedWithExpiredQuote_MarksExpiredAndKeepsValidOnes()
    {
        await using var api = factory.CreateIsolated();
        var old = await api.Client.CreateOrderAsync();
        api.Time.Advance(TimeSpan.FromSeconds(30));
        var recent = await api.Client.CreateOrderAsync();
        api.Time.Advance(TimeSpan.FromSeconds(30)); // a primeira vence agora; a segunda ainda tem 30s

        var result = await api.RunReconciliationAsync();

        Assert.Equal(new ReconciliationResult(1, 0, 0), result);
        var expired = await api.Client.GetOrderAsync(old.Id());
        Assert.Equal("Expired", expired.Status());
        Assert.False(expired.GetProperty("canConfirm").GetBoolean());
        Assert.False(expired.GetProperty("canCancel").GetBoolean());
        Assert.Equal("Quoted", (await api.Client.GetOrderAsync(recent.Id())).Status());
    }

    [Fact]
    public async Task RunOnceAsync_MoreExpiredOrdersThanBatchSize_ProcessesInBatchesOf20()
    {
        await using var api = factory.CreateIsolated();
        for (var i = 0; i < OrderReconciliationCycle.BatchSize + 5; i++)
        {
            await api.Client.CreateOrderAsync();
        }

        api.Time.Advance(TimeSpan.FromMinutes(2));

        Assert.Equal(20, (await api.RunReconciliationAsync()).Expired);
        Assert.Equal(5, (await api.RunReconciliationAsync()).Expired);
        Assert.Equal(0, (await api.RunReconciliationAsync()).Expired);
    }

    [Fact]
    public async Task RunOnceAsync_ConfirmedOrder_RequestsPaymentAndMarksPaymentRequested()
    {
        await using var api = factory.CreateIsolated();
        var created = await api.Client.CreateOrderAsync();
        api.Payments.Unavailable = true;
        await api.Client.ConfirmOrderAsync(created.Id()); // fica Confirmed
        api.Payments.Unavailable = false;
        api.Payments.CorrelationIds.Clear();

        var result = await api.RunReconciliationAsync();

        Assert.Equal(1, result.PaymentsRequested);
        var order = await api.Client.GetOrderAsync(created.Id());
        Assert.Equal("PaymentRequested", order.Status());
        Assert.NotEqual(Guid.Empty, order.GetProperty("paymentId").GetGuid());

        // O ciclo define um correlation id próprio, que acompanha todas as chamadas dele ao Payments
        // (o POST da solicitação e o GET da consulta, já que a ordem passa a PaymentRequested no mesmo ciclo).
        Assert.True(Guid.TryParse(Assert.Single(api.Payments.CorrelationIds.Distinct()), out _));
    }

    [Fact]
    public async Task RunOnceAsync_PaymentCompleted_MarksOrderCompleted()
    {
        await using var api = factory.CreateIsolated();
        var id = await CreatePaymentRequestedOrderAsync(api);
        api.Payments.SetStatus(id, PaymentStatus.Completed);

        var result = await api.RunReconciliationAsync();

        Assert.Equal(1, result.PaymentsFinished);
        var order = await api.Client.GetOrderAsync(id);
        Assert.Equal("Completed", order.Status());
        Assert.Equal(JsonValueKind.Null, order.GetProperty("failureReason").ValueKind);
    }

    [Fact]
    public async Task RunOnceAsync_PaymentFailed_MarksOrderFailedWithPaymentsReason()
    {
        await using var api = factory.CreateIsolated();
        var id = await CreatePaymentRequestedOrderAsync(api);
        api.Payments.SetStatus(id, PaymentStatus.Failed, "BENEFICIARY_REJECTED");

        await api.RunReconciliationAsync();

        var order = await api.Client.GetOrderAsync(id);
        Assert.Equal("Failed", order.Status());
        Assert.Equal("BENEFICIARY_REJECTED", order.GetProperty("failureReason").GetString());
    }

    [Theory]
    [InlineData(PaymentStatus.Pending)]
    [InlineData(PaymentStatus.Processing)]
    public async Task RunOnceAsync_PaymentStillInProgress_KeepsPaymentRequested(PaymentStatus status)
    {
        await using var api = factory.CreateIsolated();
        var id = await CreatePaymentRequestedOrderAsync(api);
        api.Payments.SetStatus(id, status);

        var result = await api.RunReconciliationAsync();

        Assert.False(result.HasChanges);
        Assert.Equal("PaymentRequested", (await api.Client.GetOrderAsync(id)).Status());
    }

    [Fact]
    public async Task RunOnceAsync_PaymentsUnavailable_KeepsOrdersAndDoesNotThrow()
    {
        await using var api = factory.CreateIsolated();
        var requested = await CreatePaymentRequestedOrderAsync(api);
        var confirmed = await api.Client.CreateOrderAsync();
        api.Payments.Unavailable = true;
        await api.Client.ConfirmOrderAsync(confirmed.Id());

        var result = await api.RunReconciliationAsync();

        Assert.False(result.HasChanges);
        Assert.Equal("PaymentRequested", (await api.Client.GetOrderAsync(requested)).Status());
        Assert.Equal("Confirmed", (await api.Client.GetOrderAsync(confirmed.Id())).Status());
    }

    [Fact]
    public async Task RunOnceAsync_TwoCyclesInParallel_RequestEachPaymentOnlyOnce()
    {
        await using var api = factory.CreateIsolated();
        var ids = new List<Guid>();
        api.Payments.Unavailable = true;
        for (var i = 0; i < 6; i++)
        {
            var created = await api.Client.CreateOrderAsync();
            await api.Client.ConfirmOrderAsync(created.Id());
            ids.Add(created.Id());
        }

        api.Payments.Unavailable = false;
        api.Payments.Delay = TimeSpan.FromMilliseconds(100); // mantém o lote travado enquanto o outro ciclo roda

        var results = await Task.WhenAll(api.RunReconciliationAsync(), api.RunReconciliationAsync());

        // SKIP LOCKED: o segundo ciclo pula as linhas travadas pelo primeiro, e cada ordem é processada uma vez só.
        Assert.Equal(ids.Count, results.Sum(x => x.PaymentsRequested));
        Assert.All(ids, id => Assert.Equal(1, api.Payments.RequestsPerOrder[id]));
        foreach (var id in ids)
        {
            Assert.Equal("PaymentRequested", (await api.Client.GetOrderAsync(id)).Status());
        }
    }

    private static async Task<Guid> CreatePaymentRequestedOrderAsync(IsolatedOrdersApi api)
    {
        var created = await api.Client.CreateOrderAsync();
        var confirmed = await (await api.Client.ConfirmOrderAsync(created.Id())).ReadJsonAsync();
        Assert.Equal("PaymentRequested", confirmed.Status());
        return created.Id();
    }
}
