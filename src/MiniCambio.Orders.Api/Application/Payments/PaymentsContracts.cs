using MiniCambio.Orders.Api.Domain;

namespace MiniCambio.Orders.Api.Application.Payments;

// Cópias locais dos contratos do Payments (sem pacote compartilhado entre repositórios, de propósito).

public sealed record PaymentBeneficiary(string Name, string Country, string AccountNumber);

/// <summary>Corpo do <c>POST /api/v1/payments</c> do Payments.</summary>
public sealed record CreatePaymentRequest(
    Guid OrderId,
    string Currency,
    decimal ForeignAmount,
    PaymentBeneficiary Beneficiary)
{
    public static CreatePaymentRequest From(Order order) => new(
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

/// <summary>Pagamento devolvido pelo Payments.</summary>
public sealed record PaymentResponse(
    Guid Id,
    Guid OrderId,
    PaymentStatus Status,
    string? FailureReason,
    string? ProviderReference);
