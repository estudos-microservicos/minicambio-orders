namespace MiniCambio.Orders.Api.Application.Orders;

/// <summary>
/// Corpo do <c>POST /api/v1/orders</c>. Os campos são anuláveis e o <c>side</c> é texto para que a validação
/// devolva mensagens por campo em vez de um erro genérico de desserialização.
/// </summary>
public sealed record CreateOrderRequest(
    string? Currency,
    string? Side,
    decimal? ForeignAmount,
    BeneficiaryRequest? Beneficiary);

public sealed record BeneficiaryRequest(string? Name, string? Country, string? AccountNumber);
