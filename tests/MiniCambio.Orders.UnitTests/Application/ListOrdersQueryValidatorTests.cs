using MiniCambio.Orders.Api.Application.Orders;

namespace MiniCambio.Orders.UnitTests.Application;

public class ListOrdersQueryValidatorTests
{
    private readonly ListOrdersQueryValidator validator = new();

    [Fact]
    public void Validate_NoParameters_HasNoErrors()
    {
        Assert.True(validator.Validate(new ListOrdersQuery(null, null, null)).IsValid);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Quoted")]
    [InlineData("paymentrequested")]
    [InlineData("COMPLETED")]
    public void Validate_EmptyOrKnownStatusInAnyCase_HasNoErrors(string status)
    {
        Assert.True(validator.Validate(new ListOrdersQuery(status, 1, 20)).IsValid);
    }

    [Theory]
    [InlineData("Done")]
    [InlineData("1")]
    [InlineData(" Quoted")]
    public void Validate_UnknownStatus_HasStatusError(string status)
    {
        var result = validator.Validate(new ListOrdersQuery(status, null, null));

        var error = Assert.Single(result.Errors);
        Assert.Equal("status", error.PropertyName, ignoreCase: true);
        Assert.Contains("Quoted, Confirmed, PaymentRequested, Completed, Failed, Cancelled, Expired", error.ErrorMessage);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Validate_PageBelowOne_HasPageError(int page)
    {
        var result = validator.Validate(new ListOrdersQuery(null, page, null));

        Assert.Equal("page", Assert.Single(result.Errors).PropertyName, ignoreCase: true);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(101)]
    public void Validate_PageSizeOutOfRange_HasPageSizeError(int pageSize)
    {
        var result = validator.Validate(new ListOrdersQuery(null, null, pageSize));

        Assert.Equal("pageSize", Assert.Single(result.Errors).PropertyName, ignoreCase: true);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(100)]
    public void Validate_PageSizeAtLimits_HasNoErrors(int pageSize)
    {
        Assert.True(validator.Validate(new ListOrdersQuery(null, 1, pageSize)).IsValid);
    }
}
