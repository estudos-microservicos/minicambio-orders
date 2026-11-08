using MiniCambio.Orders.Api.Domain;

namespace MiniCambio.Orders.Api.Application.Payments;

/// <summary>O que o <see cref="PaymentResultHandler"/> fez com uma mensagem.</summary>
public enum PaymentResultOutcome
{
    /// <summary>A ordem foi finalizada (<c>Completed</c> ou <c>Failed</c>).</summary>
    Applied,

    /// <summary>A ordem já estava finalizada: mensagem repetida, nada muda.</summary>
    AlreadyFinished,

    /// <summary>Mensagem sem efeito (ordem inexistente, status inesperado); fica só no log.</summary>
    Ignored,
}

/// <summary>
/// Aplica na ordem o resultado de pagamento publicado pelo Payments no Kafka. O consumidor entrega cada mensagem
/// <b>pelo menos uma vez</b>, então o handler precisa ser idempotente: receber o mesmo resultado de novo não muda nada.
/// </summary>
public sealed class PaymentResultHandler(
    IOrderRepository orders,
    TimeProvider timeProvider,
    ILogger<PaymentResultHandler> logger)
{
    /// <summary>Motivo usado se o Payments disser <c>Failed</c> sem informar o código.</summary>
    public const string UnknownFailureReason = "PAYMENT_FAILED";

    public async Task<PaymentResultOutcome> HandleAsync(PaymentResultMessage message, CancellationToken cancellationToken)
    {
        var order = await orders.GetAsync(message.OrderId, cancellationToken);
        if (order is null)
        {
            logger.LogWarning(
                "Resultado do pagamento {PaymentId} ignorado: a ordem {OrderId} não existe", message.PaymentId, message.OrderId);
            return PaymentResultOutcome.Ignored;
        }

        if (order.Status is OrderStatus.Completed or OrderStatus.Failed)
        {
            logger.LogInformation(
                "Resultado do pagamento {PaymentId} repetido: a ordem {OrderId} já está {Status}",
                message.PaymentId,
                order.Id,
                order.Status);
            return PaymentResultOutcome.AlreadyFinished;
        }

        if (order.Status is not (OrderStatus.Confirmed or OrderStatus.PaymentRequested)
            || message.Status is not (PaymentStatus.Completed or PaymentStatus.Failed))
        {
            logger.LogWarning(
                "Resultado {PaymentStatus} do pagamento {PaymentId} ignorado: a ordem {OrderId} está {Status}",
                message.Status,
                message.PaymentId,
                order.Id,
                order.Status);
            return PaymentResultOutcome.Ignored;
        }

        var now = timeProvider.GetUtcNow();

        // Corrida possível: o pedido foi publicado, mas a gravação de PaymentRequested falhou (ou ainda não aconteceu)
        // e o resultado chegou antes. Se o Payments respondeu, o pedido com certeza foi feito: registra antes de finalizar.
        if (order.Status == OrderStatus.Confirmed)
        {
            order.MarkPaymentRequested(now);
        }

        if (message.Status == PaymentStatus.Completed)
        {
            order.MarkCompleted(message.PaymentId, now);
        }
        else
        {
            order.MarkFailed(message.PaymentId, message.FailureReason ?? UnknownFailureReason, now);
        }

        await orders.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Ordem {OrderId} agora está {Status} (pagamento {PaymentId})", order.Id, order.Status, message.PaymentId);
        return PaymentResultOutcome.Applied;
    }
}
