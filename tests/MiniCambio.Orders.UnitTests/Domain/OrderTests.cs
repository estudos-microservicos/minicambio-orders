using MiniCambio.Orders.Api.Domain;

namespace MiniCambio.Orders.UnitTests.Domain;

public class OrderTests
{
    private static readonly DateTimeOffset Now = TestOrders.Now;
    private static readonly DateTimeOffset BeforeExpiration = Now.AddSeconds(59);
    private static readonly DateTimeOffset AtExpiration = Now.Add(TestOrders.QuoteTtl);

    // ---------------------------------------------------------------- criação

    [Fact]
    public void CreateQuoted_ValidData_StartsQuotedWithQuoteAndBeneficiary()
    {
        var quoteId = Guid.NewGuid();

        var order = Order.CreateQuoted(
            "chave-1",
            "EUR",
            OrderSide.Sell,
            250.50m,
            new OrderQuote(quoteId, 5.82075m, 1458.10m, Now.AddSeconds(60)),
            new Beneficiary("João Souza", "PT", "PT50000201231234567890154"),
            Now);

        Assert.NotEqual(Guid.Empty, order.Id);
        Assert.Equal("chave-1", order.IdempotencyKey);
        Assert.Equal("EUR", order.Currency);
        Assert.Equal(OrderSide.Sell, order.Side);
        Assert.Equal(250.50m, order.ForeignAmount);
        Assert.Equal(quoteId, order.QuoteId);
        Assert.Equal(5.82075m, order.Rate);
        Assert.Equal(1458.10m, order.BrlAmount);
        Assert.Equal(Now.AddSeconds(60), order.QuoteExpiresAt);
        Assert.Equal("João Souza", order.BeneficiaryName);
        Assert.Equal("PT", order.BeneficiaryCountry);
        Assert.Equal("PT50000201231234567890154", order.BeneficiaryAccount);
        Assert.Equal(OrderStatus.Quoted, order.Status);
        Assert.Null(order.PaymentId);
        Assert.Null(order.FailureReason);
        Assert.Null(order.ConfirmedAt);
        Assert.Null(order.CancelledAt);
        Assert.Equal(Now, order.CreatedAt);
        Assert.Equal(Now, order.UpdatedAt);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void CreateQuoted_NonPositiveAmount_ThrowsDomainException(decimal foreignAmount)
    {
        Assert.Throws<DomainException>(() => Order.CreateQuoted(
            "chave",
            "USD",
            OrderSide.Buy,
            foreignAmount,
            new OrderQuote(Guid.NewGuid(), 5.4m, 0m, Now.AddSeconds(60)),
            new Beneficiary("Maria", "BR", "1"),
            Now));
    }

    [Fact]
    public void CreateQuoted_IdempotencyKeyLongerThan100_ThrowsArgumentOutOfRange()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => TestOrders.Quoted(idempotencyKey: new string('k', 101)));
    }

    // ---------------------------------------------------------------- consultas de regra

    [Fact]
    public void IsQuoteExpired_BeforeExpiration_ReturnsFalse()
    {
        Assert.False(TestOrders.Quoted().IsQuoteExpired(BeforeExpiration));
    }

    [Fact]
    public void IsQuoteExpired_AtOrAfterExpiration_ReturnsTrue()
    {
        var order = TestOrders.Quoted();

        Assert.True(order.IsQuoteExpired(AtExpiration));
        Assert.True(order.IsQuoteExpired(AtExpiration.AddMinutes(5)));
    }

    [Fact]
    public void CanBeConfirmed_QuotedWithValidQuote_ReturnsTrue()
    {
        Assert.True(TestOrders.Quoted().CanBeConfirmed(BeforeExpiration));
    }

    [Fact]
    public void CanBeConfirmed_QuotedWithExpiredQuote_ReturnsFalse()
    {
        Assert.False(TestOrders.Quoted().CanBeConfirmed(AtExpiration));
    }

    [Theory]
    [InlineData(OrderStatus.Confirmed)]
    [InlineData(OrderStatus.PaymentRequested)]
    [InlineData(OrderStatus.Completed)]
    [InlineData(OrderStatus.Failed)]
    [InlineData(OrderStatus.Cancelled)]
    [InlineData(OrderStatus.Expired)]
    public void CanBeConfirmed_NotQuoted_ReturnsFalse(OrderStatus status)
    {
        Assert.False(TestOrders.InStatus(status).CanBeConfirmed(Now));
    }

    [Fact]
    public void CanBeCancelled_Quoted_ReturnsTrue()
    {
        Assert.True(TestOrders.Quoted().CanBeCancelled());
    }

    [Theory]
    [InlineData(OrderStatus.Confirmed)]
    [InlineData(OrderStatus.PaymentRequested)]
    [InlineData(OrderStatus.Completed)]
    [InlineData(OrderStatus.Failed)]
    [InlineData(OrderStatus.Cancelled)]
    [InlineData(OrderStatus.Expired)]
    public void CanBeCancelled_NotQuoted_ReturnsFalse(OrderStatus status)
    {
        Assert.False(TestOrders.InStatus(status).CanBeCancelled());
    }

    // ---------------------------------------------------------------- Quoted -> Confirmed

    [Fact]
    public void Confirm_QuotedWithValidQuote_MovesToConfirmed()
    {
        var order = TestOrders.Quoted();

        order.Confirm(BeforeExpiration);

        Assert.Equal(OrderStatus.Confirmed, order.Status);
        Assert.Equal(BeforeExpiration, order.ConfirmedAt);
        Assert.Equal(BeforeExpiration, order.UpdatedAt);
    }

    [Fact]
    public void Confirm_QuotedWithExpiredQuote_ThrowsQuoteExpiredAndKeepsQuoted()
    {
        var order = TestOrders.Quoted();

        var exception = Assert.Throws<QuoteExpiredException>(() => order.Confirm(AtExpiration));

        Assert.Equal("A cotação expirou. Crie uma nova ordem.", exception.Message);
        Assert.Equal(OrderStatus.Quoted, order.Status);
        Assert.Null(order.ConfirmedAt);
    }

    [Theory]
    [InlineData(OrderStatus.Confirmed, "Esta ordem já foi confirmada.")]
    [InlineData(OrderStatus.PaymentRequested, "Esta ordem já foi confirmada.")]
    [InlineData(OrderStatus.Completed, "Esta ordem já foi confirmada.")]
    [InlineData(OrderStatus.Failed, "O pagamento desta ordem falhou. Crie uma nova ordem.")]
    [InlineData(OrderStatus.Cancelled, "Esta ordem foi cancelada e não pode mais ser confirmada.")]
    [InlineData(OrderStatus.Expired, "A cotação expirou. Crie uma nova ordem.")]
    public void Confirm_NotQuoted_ThrowsDomainExceptionAndKeepsStatus(OrderStatus status, string expectedMessage)
    {
        var order = TestOrders.InStatus(status);

        var exception = Assert.ThrowsAny<DomainException>(() => order.Confirm(Now));

        Assert.Equal(expectedMessage, exception.Message);
        Assert.Equal(status, order.Status);
    }

    // ---------------------------------------------------------------- Quoted -> Cancelled

    [Fact]
    public void Cancel_Quoted_MovesToCancelled()
    {
        var order = TestOrders.Quoted();

        order.Cancel(BeforeExpiration);

        Assert.Equal(OrderStatus.Cancelled, order.Status);
        Assert.Equal(BeforeExpiration, order.CancelledAt);
        Assert.Equal(BeforeExpiration, order.UpdatedAt);
    }

    [Fact]
    public void Cancel_QuotedWithExpiredQuoteNotYetReconciled_MovesToCancelled()
    {
        var order = TestOrders.Quoted();

        order.Cancel(AtExpiration);

        Assert.Equal(OrderStatus.Cancelled, order.Status);
    }

    [Theory]
    [InlineData(OrderStatus.Confirmed)]
    [InlineData(OrderStatus.PaymentRequested)]
    [InlineData(OrderStatus.Completed)]
    [InlineData(OrderStatus.Failed)]
    [InlineData(OrderStatus.Cancelled)]
    [InlineData(OrderStatus.Expired)]
    public void Cancel_NotQuoted_ThrowsDomainExceptionAndKeepsStatus(OrderStatus status)
    {
        var order = TestOrders.InStatus(status);

        var exception = Assert.Throws<DomainException>(() => order.Cancel(Now));

        Assert.Equal("Só é possível cancelar ordens que ainda não foram confirmadas.", exception.Message);
        Assert.Equal(status, order.Status);
    }

    // ---------------------------------------------------------------- Quoted -> Expired

    [Fact]
    public void MarkExpired_QuotedWithExpiredQuote_MovesToExpired()
    {
        var order = TestOrders.Quoted();

        order.MarkExpired(AtExpiration);

        Assert.Equal(OrderStatus.Expired, order.Status);
        Assert.Equal(AtExpiration, order.UpdatedAt);
    }

    [Fact]
    public void MarkExpired_QuotedWithValidQuote_ThrowsDomainExceptionAndKeepsQuoted()
    {
        var order = TestOrders.Quoted();

        var exception = Assert.Throws<DomainException>(() => order.MarkExpired(BeforeExpiration));

        Assert.Equal("A cotação desta ordem ainda está válida.", exception.Message);
        Assert.Equal(OrderStatus.Quoted, order.Status);
    }

    [Theory]
    [InlineData(OrderStatus.Confirmed)]
    [InlineData(OrderStatus.PaymentRequested)]
    [InlineData(OrderStatus.Completed)]
    [InlineData(OrderStatus.Failed)]
    [InlineData(OrderStatus.Cancelled)]
    [InlineData(OrderStatus.Expired)]
    public void MarkExpired_NotQuoted_ThrowsDomainExceptionAndKeepsStatus(OrderStatus status)
    {
        var order = TestOrders.InStatus(status);

        var exception = Assert.Throws<DomainException>(() => order.MarkExpired(AtExpiration.AddMinutes(1)));

        Assert.Equal("Só ordens aguardando confirmação podem expirar.", exception.Message);
        Assert.Equal(status, order.Status);
    }

    // ---------------------------------------------------------------- Confirmed -> PaymentRequested

    [Fact]
    public void MarkPaymentRequested_Confirmed_MovesToPaymentRequestedWithPaymentId()
    {
        var order = TestOrders.InStatus(OrderStatus.Confirmed);
        var paymentId = Guid.NewGuid();

        order.MarkPaymentRequested(paymentId, AtExpiration);

        Assert.Equal(OrderStatus.PaymentRequested, order.Status);
        Assert.Equal(paymentId, order.PaymentId);
        Assert.Equal(AtExpiration, order.UpdatedAt);
    }

    [Fact]
    public void MarkPaymentRequested_EmptyPaymentId_ThrowsArgumentExceptionAndKeepsConfirmed()
    {
        var order = TestOrders.InStatus(OrderStatus.Confirmed);

        Assert.Throws<ArgumentException>(() => order.MarkPaymentRequested(Guid.Empty, Now));

        Assert.Equal(OrderStatus.Confirmed, order.Status);
        Assert.Null(order.PaymentId);
    }

    [Theory]
    [InlineData(OrderStatus.Quoted)]
    [InlineData(OrderStatus.PaymentRequested)]
    [InlineData(OrderStatus.Completed)]
    [InlineData(OrderStatus.Failed)]
    [InlineData(OrderStatus.Cancelled)]
    [InlineData(OrderStatus.Expired)]
    public void MarkPaymentRequested_NotConfirmed_ThrowsDomainExceptionAndKeepsStatus(OrderStatus status)
    {
        var order = TestOrders.InStatus(status);
        var paymentIdBefore = order.PaymentId;

        var exception = Assert.Throws<DomainException>(() => order.MarkPaymentRequested(Guid.NewGuid(), Now));

        Assert.Equal("O pagamento só pode ser solicitado para ordens confirmadas.", exception.Message);
        Assert.Equal(status, order.Status);
        Assert.Equal(paymentIdBefore, order.PaymentId);
    }

    // ---------------------------------------------------------------- PaymentRequested -> Completed

    [Fact]
    public void MarkCompleted_PaymentRequested_MovesToCompleted()
    {
        var order = TestOrders.InStatus(OrderStatus.PaymentRequested);

        order.MarkCompleted(AtExpiration);

        Assert.Equal(OrderStatus.Completed, order.Status);
        Assert.Null(order.FailureReason);
        Assert.Equal(AtExpiration, order.UpdatedAt);
    }

    [Theory]
    [InlineData(OrderStatus.Quoted)]
    [InlineData(OrderStatus.Confirmed)]
    [InlineData(OrderStatus.Completed)]
    [InlineData(OrderStatus.Failed)]
    [InlineData(OrderStatus.Cancelled)]
    [InlineData(OrderStatus.Expired)]
    public void MarkCompleted_NotPaymentRequested_ThrowsDomainExceptionAndKeepsStatus(OrderStatus status)
    {
        var order = TestOrders.InStatus(status);

        var exception = Assert.Throws<DomainException>(() => order.MarkCompleted(Now));

        Assert.Equal("Só ordens com pagamento solicitado podem ser concluídas.", exception.Message);
        Assert.Equal(status, order.Status);
    }

    // ---------------------------------------------------------------- PaymentRequested -> Failed

    [Fact]
    public void MarkFailed_PaymentRequested_MovesToFailedWithReason()
    {
        var order = TestOrders.InStatus(OrderStatus.PaymentRequested);

        order.MarkFailed("COMPLIANCE_BLOCKED", AtExpiration);

        Assert.Equal(OrderStatus.Failed, order.Status);
        Assert.Equal("COMPLIANCE_BLOCKED", order.FailureReason);
        Assert.Equal(AtExpiration, order.UpdatedAt);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void MarkFailed_BlankReason_ThrowsArgumentExceptionAndKeepsPaymentRequested(string reason)
    {
        var order = TestOrders.InStatus(OrderStatus.PaymentRequested);

        Assert.Throws<ArgumentException>(() => order.MarkFailed(reason, Now));

        Assert.Equal(OrderStatus.PaymentRequested, order.Status);
    }

    [Theory]
    [InlineData(OrderStatus.Quoted)]
    [InlineData(OrderStatus.Confirmed)]
    [InlineData(OrderStatus.Completed)]
    [InlineData(OrderStatus.Failed)]
    [InlineData(OrderStatus.Cancelled)]
    [InlineData(OrderStatus.Expired)]
    public void MarkFailed_NotPaymentRequested_ThrowsDomainExceptionAndKeepsStatus(OrderStatus status)
    {
        var order = TestOrders.InStatus(status);
        var reasonBefore = order.FailureReason;

        var exception = Assert.Throws<DomainException>(() => order.MarkFailed("LIMIT_EXCEEDED", Now));

        Assert.Equal("Só ordens com pagamento solicitado podem ser marcadas como falhas.", exception.Message);
        Assert.Equal(status, order.Status);
        Assert.Equal(reasonBefore, order.FailureReason);
    }
}
