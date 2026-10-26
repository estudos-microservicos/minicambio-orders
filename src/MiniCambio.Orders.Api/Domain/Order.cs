namespace MiniCambio.Orders.Api.Domain;

/// <summary>Dados do beneficiário do pagamento.</summary>
public sealed record Beneficiary(string Name, string Country, string AccountNumber);

/// <summary>Cotação obtida no Rates e congelada na ordem.</summary>
public sealed record OrderQuote(Guid QuoteId, decimal Rate, decimal BrlAmount, DateTimeOffset ExpiresAt);

/// <summary>
/// Ordem de câmbio. O status só muda pelos métodos de transição, que seguem a máquina de estados descrita em
/// <see cref="OrderStatus"/> e lançam <see cref="DomainException"/> (422 na API) em qualquer transição fora dela.
/// </summary>
public sealed class Order
{
    public const string CannotCancelMessage = "Só é possível cancelar ordens que ainda não foram confirmadas.";

    public const int MaxIdempotencyKeyLength = 100;

    // Construtor usado pelo EF Core.
    private Order()
    {
        IdempotencyKey = string.Empty;
        Currency = string.Empty;
        BeneficiaryName = string.Empty;
        BeneficiaryCountry = string.Empty;
        BeneficiaryAccount = string.Empty;
    }

    private Order(
        Guid id,
        string idempotencyKey,
        string currency,
        OrderSide side,
        decimal foreignAmount,
        OrderQuote quote,
        Beneficiary beneficiary,
        DateTimeOffset now)
    {
        Id = id;
        IdempotencyKey = idempotencyKey;
        Currency = currency;
        Side = side;
        ForeignAmount = foreignAmount;
        QuoteId = quote.QuoteId;
        Rate = quote.Rate;
        BrlAmount = quote.BrlAmount;
        QuoteExpiresAt = quote.ExpiresAt;
        BeneficiaryName = beneficiary.Name;
        BeneficiaryCountry = beneficiary.Country;
        BeneficiaryAccount = beneficiary.AccountNumber;
        Status = OrderStatus.Quoted;
        CreatedAt = now;
        UpdatedAt = now;
    }

    public Guid Id { get; private set; }

    /// <summary>Chave enviada pelo cliente no header <c>Idempotency-Key</c> (única).</summary>
    public string IdempotencyKey { get; private set; }

    public string Currency { get; private set; }

    public OrderSide Side { get; private set; }

    public decimal ForeignAmount { get; private set; }

    public Guid QuoteId { get; private set; }

    public decimal Rate { get; private set; }

    public decimal BrlAmount { get; private set; }

    public DateTimeOffset QuoteExpiresAt { get; private set; }

    public string BeneficiaryName { get; private set; }

    public string BeneficiaryCountry { get; private set; }

    public string BeneficiaryAccount { get; private set; }

    public OrderStatus Status { get; private set; }

    public Guid? PaymentId { get; private set; }

    /// <summary>Código de falha devolvido pelo Payments (ex.: <c>BENEFICIARY_REJECTED</c>).</summary>
    public string? FailureReason { get; private set; }

    public DateTimeOffset? ConfirmedAt { get; private set; }

    public DateTimeOffset? CancelledAt { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>Cria a ordem em <see cref="OrderStatus.Quoted"/> com os dados da cotação.</summary>
    public static Order CreateQuoted(
        string idempotencyKey,
        string currency,
        OrderSide side,
        decimal foreignAmount,
        OrderQuote quote,
        Beneficiary beneficiary,
        DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(idempotencyKey.Length, MaxIdempotencyKeyLength, nameof(idempotencyKey));
        ArgumentException.ThrowIfNullOrWhiteSpace(currency);
        ArgumentNullException.ThrowIfNull(quote);
        ArgumentNullException.ThrowIfNull(beneficiary);

        if (foreignAmount <= 0)
        {
            throw new DomainException("O valor em moeda estrangeira deve ser maior que zero.");
        }

        return new Order(Guid.CreateVersion7(now), idempotencyKey, currency, side, foreignAmount, quote, beneficiary, now);
    }

    /// <summary>A cotação vence no instante <see cref="QuoteExpiresAt"/> (mesma regra do Rates).</summary>
    public bool IsQuoteExpired(DateTimeOffset now) => now >= QuoteExpiresAt;

    /// <summary>Só ordens cotadas e com a cotação ainda válida podem ser confirmadas.</summary>
    public bool CanBeConfirmed(DateTimeOffset now) => Status == OrderStatus.Quoted && !IsQuoteExpired(now);

    /// <summary>Só ordens cotadas (ainda não confirmadas) podem ser canceladas.</summary>
    public bool CanBeCancelled() => Status == OrderStatus.Quoted;

    /// <summary>Quoted → Confirmed, desde que a cotação ainda esteja válida.</summary>
    public void Confirm(DateTimeOffset now)
    {
        if (Status != OrderStatus.Quoted)
        {
            throw new DomainException(Status switch
            {
                OrderStatus.Confirmed or OrderStatus.PaymentRequested or OrderStatus.Completed =>
                    "Esta ordem já foi confirmada.",
                OrderStatus.Cancelled => "Esta ordem foi cancelada e não pode mais ser confirmada.",
                OrderStatus.Expired => QuoteExpiredException.DefaultMessage,
                OrderStatus.Failed => "O pagamento desta ordem falhou. Crie uma nova ordem.",
                _ => "Esta ordem não pode ser confirmada.",
            });
        }

        if (IsQuoteExpired(now))
        {
            throw new QuoteExpiredException();
        }

        Status = OrderStatus.Confirmed;
        ConfirmedAt = now;
        UpdatedAt = now;
    }

    /// <summary>Quoted → Cancelled (cancelamento lógico: a ordem continua existindo).</summary>
    public void Cancel(DateTimeOffset now)
    {
        EnsureStatus(OrderStatus.Quoted, CannotCancelMessage);

        Status = OrderStatus.Cancelled;
        CancelledAt = now;
        UpdatedAt = now;
    }

    /// <summary>Quoted → Expired, quando o prazo da cotação já venceu.</summary>
    public void MarkExpired(DateTimeOffset now)
    {
        EnsureStatus(OrderStatus.Quoted, "Só ordens aguardando confirmação podem expirar.");

        if (!IsQuoteExpired(now))
        {
            throw new DomainException("A cotação desta ordem ainda está válida.");
        }

        Status = OrderStatus.Expired;
        UpdatedAt = now;
    }

    /// <summary>Confirmed → PaymentRequested, guardando o id do pagamento criado no Payments.</summary>
    public void MarkPaymentRequested(Guid paymentId, DateTimeOffset now)
    {
        if (paymentId == Guid.Empty)
        {
            throw new ArgumentException("O id do pagamento é obrigatório.", nameof(paymentId));
        }

        EnsureStatus(OrderStatus.Confirmed, "O pagamento só pode ser solicitado para ordens confirmadas.");

        Status = OrderStatus.PaymentRequested;
        PaymentId = paymentId;
        UpdatedAt = now;
    }

    /// <summary>PaymentRequested → Completed.</summary>
    public void MarkCompleted(DateTimeOffset now)
    {
        EnsureStatus(OrderStatus.PaymentRequested, "Só ordens com pagamento solicitado podem ser concluídas.");

        Status = OrderStatus.Completed;
        UpdatedAt = now;
    }

    /// <summary>PaymentRequested → Failed, com o motivo informado pelo Payments.</summary>
    public void MarkFailed(string reason, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        EnsureStatus(OrderStatus.PaymentRequested, "Só ordens com pagamento solicitado podem ser marcadas como falhas.");

        Status = OrderStatus.Failed;
        FailureReason = reason;
        UpdatedAt = now;
    }

    private void EnsureStatus(OrderStatus expected, string message)
    {
        if (Status != expected)
        {
            throw new DomainException(message);
        }
    }
}
