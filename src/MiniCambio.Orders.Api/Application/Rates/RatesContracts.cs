using MiniCambio.Orders.Api.Domain;

namespace MiniCambio.Orders.Api.Application.Rates;

// Cópias locais dos contratos do Rates (sem pacote compartilhado entre repositórios, de propósito).

/// <summary>Corpo do <c>POST /api/v1/quotes</c> do Rates.</summary>
public sealed record CreateQuoteRequest(string Currency, OrderSide Side, decimal ForeignAmount);

/// <summary>Cotação devolvida pelo Rates.</summary>
public sealed record QuoteResponse(
    Guid Id,
    string Currency,
    OrderSide Side,
    decimal ForeignAmount,
    decimal Rate,
    decimal BrlAmount,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt,
    bool IsExpired);

/// <summary>O Rates recusou a cotação (ex.: moeda não suportada). Vira 422 repassando a mensagem do Rates.</summary>
public sealed class QuoteRejectedException(string message) : DomainException(message);
