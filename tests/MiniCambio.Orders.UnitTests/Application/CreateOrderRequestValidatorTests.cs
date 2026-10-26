using MiniCambio.Orders.Api.Application.Orders;

namespace MiniCambio.Orders.UnitTests.Application;

public class CreateOrderRequestValidatorTests
{
    private readonly CreateOrderRequestValidator validator = new();

    [Fact]
    public void Validate_ValidRequest_HasNoErrors()
    {
        var result = validator.Validate(Request());

        Assert.True(result.IsValid);
    }

    [Theory]
    [InlineData("Buy")]
    [InlineData("sell")]
    [InlineData("BUY")]
    public void Validate_SideInAnyCase_HasNoErrors(string side)
    {
        Assert.True(validator.Validate(Request(side: side)).IsValid);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("usd")]
    [InlineData("US")]
    [InlineData("USDT")]
    public void Validate_InvalidCurrency_HasCurrencyError(string? currency)
    {
        var result = validator.Validate(Request(currency: currency));

        Assert.Contains(result.Errors, e => HasName(e.PropertyName, "currency"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Hold")]
    public void Validate_InvalidSide_HasSideError(string? side)
    {
        var result = validator.Validate(Request(side: side));

        Assert.Contains(result.Errors, e => HasName(e.PropertyName, "side"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0.0)]
    [InlineData(-5.0)]
    [InlineData(10.123)]
    public void Validate_InvalidAmount_HasForeignAmountError(double? amount)
    {
        var result = validator.Validate(Request(amount: (decimal?)amount));

        Assert.Contains(result.Errors, e => HasName(e.PropertyName, "foreignAmount"));
    }

    [Fact]
    public void Validate_AmountWithTrailingZeros_HasNoErrors()
    {
        Assert.True(validator.Validate(Request(amount: 10.500m)).IsValid);
    }

    [Fact]
    public void Validate_MissingBeneficiary_HasBeneficiaryError()
    {
        var result = validator.Validate(new CreateOrderRequest("USD", "Buy", 10m, null));

        var error = Assert.Single(result.Errors);
        Assert.True(HasName(error.PropertyName, "beneficiary"));
        Assert.Equal("Informe o beneficiário.", error.ErrorMessage);
    }

    [Fact]
    public void Validate_EmptyBeneficiaryFields_HasNestedErrors()
    {
        var result = validator.Validate(Request(beneficiary: new BeneficiaryRequest(" ", "br", null)));

        Assert.Contains(result.Errors, e => HasName(e.PropertyName, "beneficiary.name"));
        Assert.Contains(result.Errors, e => HasName(e.PropertyName, "beneficiary.country"));
        Assert.Contains(result.Errors, e => HasName(e.PropertyName, "beneficiary.accountNumber"));
    }

    [Theory]
    [InlineData("B")]
    [InlineData("BRA")]
    public void Validate_CountryNotAlpha2_HasCountryError(string country)
    {
        var result = validator.Validate(Request(beneficiary: new BeneficiaryRequest("Maria", country, "1")));

        Assert.Contains(result.Errors, e => HasName(e.PropertyName, "beneficiary.country"));
    }

    [Fact]
    public void Validate_NameAndAccountAtMaxLength_HasNoErrors()
    {
        var beneficiary = new BeneficiaryRequest(new string('n', 120), "BR", new string('1', 34));

        Assert.True(validator.Validate(Request(beneficiary: beneficiary)).IsValid);
    }

    [Fact]
    public void Validate_NameAndAccountTooLong_HasErrors()
    {
        var beneficiary = new BeneficiaryRequest(new string('n', 121), "BR", new string('1', 35));

        var result = validator.Validate(Request(beneficiary: beneficiary));

        Assert.Contains(result.Errors, e => HasName(e.PropertyName, "beneficiary.name"));
        Assert.Contains(result.Errors, e => HasName(e.PropertyName, "beneficiary.accountNumber"));
    }

    // Sem o PropertyNameResolver global (registrado em AddApplication), o FluentValidation usa PascalCase.
    private static bool HasName(string propertyName, string expected) =>
        string.Equals(propertyName, expected, StringComparison.OrdinalIgnoreCase);

    private static CreateOrderRequest Request(
        string? currency = "USD",
        string? side = "Buy",
        decimal? amount = 1500.50m,
        BeneficiaryRequest? beneficiary = null) =>
        new(currency, side, amount, beneficiary ?? new BeneficiaryRequest("Maria Silva", "BR", "12345-6"));
}
