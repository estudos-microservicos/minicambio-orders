using Microsoft.EntityFrameworkCore;
using MiniCambio.Orders.Api.Application;
using MiniCambio.Orders.Api.Application.Payments;
using MiniCambio.Orders.Api.Domain;
using MiniCambio.Orders.Api.Infrastructure.Correlation;
using MiniCambio.Orders.Api.Infrastructure.Persistence;

namespace MiniCambio.Orders.Api.Infrastructure.Reconciliation;

/// <summary>Quantas ordens mudaram de status em cada etapa de um ciclo.</summary>
public sealed record ReconciliationResult(int Expired, int PaymentsRequested, int PaymentsFinished)
{
    public bool HasChanges => Expired + PaymentsRequested + PaymentsFinished > 0;
}

/// <summary>
/// Um ciclo do <see cref="OrderReconciliationWorker"/> (serviço scoped, chamado direto pelos testes):
/// <list type="number">
///   <item><c>Quoted</c> com a cotação vencida → <c>Expired</c>;</item>
///   <item><c>Confirmed</c> → solicita o pagamento (seguro por causa da idempotência por <c>orderId</c> no Payments);
///   sucesso → <c>PaymentRequested</c>;</item>
///   <item><c>PaymentRequested</c> → consulta o pagamento; <c>Completed</c>/<c>Failed</c> viram o status final da
///   ordem; <c>Pending</c>/<c>Processing</c> não mudam nada.</item>
/// </list>
/// Esse polling será substituído por eventos (mensageria) numa etapa futura: o Payments vai publicar o resultado e
/// o Orders vai reagir a ele, sem consultar.
/// </summary>
public sealed class OrderReconciliationCycle(
    OrdersDbContext db,
    IPaymentsClient paymentsClient,
    CorrelationIdAccessor correlationIdAccessor,
    TimeProvider timeProvider,
    ILogger<OrderReconciliationCycle> logger)
{
    public const int BatchSize = 20;

    /// <summary>Motivo usado se o Payments disser <c>Failed</c> sem informar o código.</summary>
    public const string UnknownFailureReason = "PAYMENT_FAILED";

    public async Task<ReconciliationResult> RunOnceAsync(CancellationToken cancellationToken)
    {
        // Um correlation id por ciclo: vai nos logs e, pelo CorrelationIdDelegatingHandler, nas chamadas ao Payments.
        var correlationId = Guid.NewGuid().ToString();
        correlationIdAccessor.CorrelationId = correlationId;
        using var scope = logger.BeginScope(new Dictionary<string, object> { ["CorrelationId"] = correlationId });

        var result = new ReconciliationResult(
            await ExpireQuotedOrdersAsync(cancellationToken),
            await RequestPendingPaymentsAsync(cancellationToken),
            await SyncPaymentResultsAsync(cancellationToken));

        if (result.HasChanges)
        {
            logger.LogInformation(
                "Reconciliação: {Expired} expiradas, {PaymentsRequested} pagamentos solicitados, {PaymentsFinished} pagamentos concluídos",
                result.Expired,
                result.PaymentsRequested,
                result.PaymentsFinished);
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
            ClaimByStatus(OrderStatus.Confirmed),
            async (order, ct) =>
            {
                var payment = await paymentsClient.RequestPaymentAsync(CreatePaymentRequest.From(order), ct);
                order.MarkPaymentRequested(payment.Id, timeProvider.GetUtcNow());
                return true;
            },
            cancellationToken);

    private Task<int> SyncPaymentResultsAsync(CancellationToken cancellationToken) =>
        ProcessBatchAsync(
            ClaimByStatus(OrderStatus.PaymentRequested),
            async (order, ct) =>
            {
                var payment = await paymentsClient.GetPaymentAsync(order.PaymentId!.Value, ct);
                if (payment is null)
                {
                    logger.LogWarning(
                        "Pagamento {PaymentId} da ordem {OrderId} não existe no Payments", order.PaymentId, order.Id);
                    return false;
                }

                switch (payment.Status)
                {
                    case PaymentStatus.Completed:
                        order.MarkCompleted(timeProvider.GetUtcNow());
                        return true;
                    case PaymentStatus.Failed:
                        order.MarkFailed(payment.FailureReason ?? UnknownFailureReason, timeProvider.GetUtcNow());
                        return true;
                    default:
                        return false; // Pending/Processing: ainda não há resultado.
                }
            },
            cancellationToken);

    private IQueryable<Order> ClaimByStatus(OrderStatus status) =>
        db.Orders.FromSql($"""
            SELECT * FROM orders
            WHERE status = {status.ToString()}
            ORDER BY created_at
            LIMIT {BatchSize}
            FOR UPDATE SKIP LOCKED
            """);

    /// <summary>
    /// Trava um lote com <c>FOR UPDATE SKIP LOCKED</c> dentro de uma transação, aplica <paramref name="process"/> em
    /// cada ordem e grava tudo no fim. O <c>SKIP LOCKED</c> faz cada réplica (quando o serviço for escalado no
    /// Kubernetes) pular as linhas já travadas por outra, então duas réplicas nunca processam a mesma ordem.
    /// Erro em uma ordem é logado e não impede as demais.
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
                logger.LogWarning(ex, "Payments indisponível ao reconciliar a ordem {OrderId}; tentando no próximo ciclo", order.Id);
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
