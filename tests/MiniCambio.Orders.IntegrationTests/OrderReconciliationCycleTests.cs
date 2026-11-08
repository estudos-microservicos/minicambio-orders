using System.Text.Json;
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

        Assert.Equal(new ReconciliationResult(1, 0), result);
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
    public async Task RunOnceAsync_ConfirmedOrder_PublishesPaymentRequestAndMarksPaymentRequested()
    {
        await using var api = factory.CreateIsolated();
        var created = await api.Client.CreateOrderAsync();
        api.PaymentRequests.Unavailable = true;
        await api.Client.ConfirmOrderAsync(created.Id()); // Kafka fora: fica Confirmed
        api.PaymentRequests.Unavailable = false;
        api.PaymentRequests.CorrelationIds.Clear();

        var result = await api.RunReconciliationAsync();

        Assert.Equal(1, result.PaymentsRequested);
        var order = await api.Client.GetOrderAsync(created.Id());
        Assert.Equal("PaymentRequested", order.Status());
        Assert.Equal(JsonValueKind.Null, order.GetProperty("paymentId").ValueKind); // só chega com o resultado

        var message = Assert.Single(api.PaymentRequests.Messages);
        Assert.Equal(created.Id(), message.OrderId);
        Assert.Equal("USD", message.Currency);
        Assert.Equal(100m, message.ForeignAmount);
        Assert.Equal("Maria Silva", message.Beneficiary.Name);

        // O ciclo define um correlation id próprio, que vai como header na mensagem publicada.
        Assert.True(Guid.TryParse(Assert.Single(api.PaymentRequests.CorrelationIds), out _));
    }

    [Fact]
    public async Task RunOnceAsync_KafkaUnavailable_KeepsOrdersConfirmedAndStopsTheBatch()
    {
        await using var api = factory.CreateIsolated();
        api.PaymentRequests.Unavailable = true;
        var ids = new List<Guid>();
        for (var i = 0; i < 3; i++)
        {
            var created = await api.Client.CreateOrderAsync();
            await api.Client.ConfirmOrderAsync(created.Id());
            ids.Add(created.Id());
        }

        api.PaymentRequests.CorrelationIds.Clear();

        var result = await api.RunReconciliationAsync();

        Assert.False(result.HasChanges);
        Assert.Single(api.PaymentRequests.CorrelationIds); // desistiu do lote na primeira falha
        foreach (var id in ids)
        {
            Assert.Equal("Confirmed", (await api.Client.GetOrderAsync(id)).Status());
        }
    }

    [Fact]
    public async Task RunOnceAsync_TwoCyclesInParallel_PublishEachPaymentRequestOnlyOnce()
    {
        await using var api = factory.CreateIsolated();
        var ids = new List<Guid>();
        api.PaymentRequests.Unavailable = true;
        for (var i = 0; i < 6; i++)
        {
            var created = await api.Client.CreateOrderAsync();
            await api.Client.ConfirmOrderAsync(created.Id());
            ids.Add(created.Id());
        }

        api.PaymentRequests.Unavailable = false;
        api.PaymentRequests.Delay = TimeSpan.FromMilliseconds(100); // mantém o lote travado enquanto o outro ciclo roda

        var results = await Task.WhenAll(api.RunReconciliationAsync(), api.RunReconciliationAsync());

        // SKIP LOCKED: o segundo ciclo pula as linhas travadas pelo primeiro, e cada ordem é publicada uma vez só.
        Assert.Equal(ids.Count, results.Sum(x => x.PaymentsRequested));
        Assert.All(ids, id => Assert.Equal(1, api.PaymentRequests.CountFor(id)));
        foreach (var id in ids)
        {
            Assert.Equal("PaymentRequested", (await api.Client.GetOrderAsync(id)).Status());
        }
    }
}
