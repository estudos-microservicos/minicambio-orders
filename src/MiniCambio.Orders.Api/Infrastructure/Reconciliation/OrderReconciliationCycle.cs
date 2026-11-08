using Microsoft.EntityFrameworkCore;
using MiniCambio.Orders.Api.Application;
using MiniCambio.Orders.Api.Application.Payments;
using MiniCambio.Orders.Api.Domain;
using MiniCambio.Orders.Api.Infrastructure.Correlation;
using MiniCambio.Orders.Api.Infrastructure.Persistence;

namespace MiniCambio.Orders.Api.Infrastructure.Reconciliation;

/// <summary>Quantas ordens mudaram de status em cada etapa de um ciclo.</summary>
public sealed record ReconciliationResult(int Expired, int PaymentsRequested)
{
    public bool HasChanges => Expired + PaymentsRequested > 0;
}

/// <summary>
/// Um ciclo do <see cref="OrderReconciliationWorker"/> (serviço scoped, chamado direto pelos testes):
/// <list type="number">
///   <item><c>Quoted</c> com a cotação vencida → <c>Expired</c>;</item>
///   <item><c>Confirmed</c> → publica de novo o pedido de pagamento no Kafka (seguro por causa da idempotência por
///   <c>orderId</c> no Payments); sucesso → <c>PaymentRequested</c>.</item>
/// </list>
/// O resultado do pagamento não é consultado aqui: o Payments o publica no Kafka e o
/// <see cref="Messaging.PaymentResultsConsumer"/> finaliza a ordem.
/// </summary>
public sealed class OrderReconciliationCycle(
    OrdersDbContext db,
    IPaymentRequestPublisher paymentRequests,
    CorrelationIdAccessor correlationIdAccessor,
    TimeProvider timeProvider,
    ILogger<OrderReconciliationCycle> logger)
{
    public const int BatchSize = 20;

    public async Task<ReconciliationResult> RunOnceAsync(CancellationToken cancellationToken)
    {
        // Um correlation id por ciclo: vai nos logs e, como header, nas mensagens publicadas no Kafka.
        var correlationId = Guid.NewGuid().ToString();
        correlationIdAccessor.CorrelationId = correlationId;
        using var scope = logger.BeginScope(new Dictionary<string, object> { ["CorrelationId"] = correlationId });

        var result = new ReconciliationResult(
            await ExpireQuotedOrdersAsync(cancellationToken),
            await RequestPendingPaymentsAsync(cancellationToken));

        if (result.HasChanges)
        {
            logger.LogInformation(
                "Reconciliação: {Expired} expiradas, {PaymentsRequested} pedidos de pagamento publicados",
                result.Expired,
                result.PaymentsRequested);
        }

        return result;
    }

    private Task<int> ExpireQuotedOrdersAsync(CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        return ProcessBatchAsync(
            db.Orders.FromSql($"""
                SELECT * FROM orders
                WHERE status = {nameof(OrderStatus.Quoted)} AND quote_expires_at <= {now}
                ORDER BY quote_expires_at
                LIMIT {BatchSize}
                FOR UPDATE SKIP LOCKED
                """),
            (order, _) =>
            {
                order.MarkExpired(now);
                return Task.FromResult(true);
            },
            cancellationToken);
    }

    private Task<int> RequestPendingPaymentsAsync(CancellationToken cancellationToken) =>
        ProcessBatchAsync(
            db.Orders.FromSql($"""
                SELECT * FROM orders
                WHERE status = {nameof(OrderStatus.Confirmed)}
                ORDER BY created_at
                LIMIT {BatchSize}
                FOR UPDATE SKIP LOCKED
                """),
            async (order, ct) =>
            {
                await paymentRequests.PublishAsync(PaymentRequestedMessage.From(order), ct);
                order.MarkPaymentRequested(timeProvider.GetUtcNow());
                return true;
            },
            cancellationToken);

    /// <summary>
    /// Trava um lote com <c>FOR UPDATE SKIP LOCKED</c> dentro de uma transação, aplica <paramref name="process"/> em
    /// cada ordem e grava tudo no fim. O <c>SKIP LOCKED</c> faz cada réplica (quando o serviço for escalado no
    /// Kubernetes) pular as linhas já travadas por outra, então duas réplicas nunca processam a mesma ordem.
    /// Erro em uma ordem é logado e não impede as demais; Kafka fora encerra o lote.
    /// </summary>
    private async Task<int> ProcessBatchAsync(
        IQueryable<Order> claimQuery,
        Func<Order, CancellationToken, Task<bool>> process,
        CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var batch = await claimQuery.ToListAsync(cancellationToken);

        var changed = 0;
        foreach (var order in batch)
        {
            try
            {
                if (await process(order, cancellationToken))
                {
                    changed++;
                }
            }
            catch (ServiceUnavailableException ex)
            {
                // Kafka fora: as próximas ordens do lote também falhariam (5 s cada), segurando as linhas travadas.
                // Para o lote aqui; o próximo ciclo tenta de novo.
                logger.LogWarning(ex, "Kafka indisponível ao reconciliar a ordem {OrderId}; tentando no próximo ciclo", order.Id);
                break;
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                logger.LogError(ex, "Erro ao reconciliar a ordem {OrderId}; seguindo para a próxima", order.Id);
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return changed;
    }
}
