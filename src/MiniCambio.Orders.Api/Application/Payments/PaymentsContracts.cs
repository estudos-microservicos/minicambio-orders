using MiniCambio.Orders.Api.Domain;

namespace MiniCambio.Orders.Api.Application.Payments;

// Cópias locais das mensagens trocadas com o Payments pelo Kafka (sem pacote compartilhado entre repositórios, de
// propósito). O JSON é camelCase, com os enums como texto.

public sealed record PaymentBeneficiary(string Name, string Country, string AccountNumber);

/// <summary>
/// Pedido de pagamento: o Orders publica no tópico <c>minicambio.payment-requests</c> ao confirmar a ordem, com o
/// <c>orderId</c> como chave da mensagem. O Payments é idempotente por <c>orderId</c>, então publicar de novo é seguro.
/// </summary>
public sealed record PaymentRequestedMessage(
    Guid OrderId,
    string Currency,
    decimal ForeignAmount,
    PaymentBeneficiary Beneficiary)
{
    public static PaymentRequestedMessage From(Order order) => new(
        order.Id,
        order.Currency,
        order.ForeignAmount,
        new PaymentBeneficiary(order.BeneficiaryName, order.BeneficiaryCountry, order.BeneficiaryAccount));
}

public enum PaymentStatus
{
    Pending,
    Processing,
    Completed,
    Failed,
}

/// <summary>
/// Resultado do pagamento: o Payments publica no tópico <c>minicambio.payment-results</c> quando o pagamento chega a
/// <c>Completed</c> ou <c>Failed</c>; o Orders consome e finaliza a ordem.
/// </summary>
public sealed record PaymentResultMessage(
    Guid PaymentId,
    Guid OrderId,
    PaymentStatus Status,
    string? FailureReason,
    string? ProviderReference);
