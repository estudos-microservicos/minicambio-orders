using FluentValidation;
using MiniCambio.Orders.Api.Domain;

namespace MiniCambio.Orders.Api.Application.Orders;

public sealed class CreateOrderRequestValidator : AbstractValidator<CreateOrderRequest>
{
    public const int MaxBeneficiaryNameLength = 120;
    public const int MaxAccountNumberLength = 34;

    public CreateOrderRequestValidator()
    {
        RuleFor(x => x.Currency)
            .NotEmpty().WithMessage("Informe a moeda.")
            .Matches("^[A-Z]{3}$").WithMessage("A moeda deve ser um código ISO 4217 com 3 letras maiúsculas (ex.: USD).");

        RuleFor(x => x.Side)
            .NotEmpty().WithMessage("Informe a operação.")
            .IsEnumName(typeof(OrderSide), caseSensitive: false).WithMessage("A operação deve ser Buy ou Sell.");

        RuleFor(x => x.ForeignAmount)
            .NotNull().WithMessage("Informe o valor.")
            .GreaterThan(0).WithMessage("O valor deve ser maior que zero.")
            .PrecisionScale(18, 2, ignoreTrailingZeros: true).WithMessage("O valor deve ter no máximo 2 casas decimais.");

        RuleFor(x => x.Beneficiary)
            .NotNull().WithMessage("Informe o beneficiário.");

        // O validador filho ignora beneficiário nulo (já reportado acima).
        RuleFor(x => x.Beneficiary!)
            .SetValidator(new BeneficiaryRequestValidator());
    }
}

/// <summary>Erros do beneficiário saem como <c>beneficiary.name</c>, <c>beneficiary.country</c> etc.</summary>
public sealed class BeneficiaryRequestValidator : AbstractValidator<BeneficiaryRequest>
{
    public BeneficiaryRequestValidator()
    {
        RuleFor(x => x.Name)
            .NotEmpty().WithMessage("Informe o nome do beneficiário.")
            .MaximumLength(CreateOrderRequestValidator.MaxBeneficiaryNameLength)
            .WithMessage($"O nome do beneficiário deve ter no máximo {CreateOrderRequestValidator.MaxBeneficiaryNameLength} caracteres.");

        RuleFor(x => x.Country)
            .NotEmpty().WithMessage("Informe o país do beneficiário.")
            .Matches("^[A-Z]{2}$").WithMessage("O país deve ser um código ISO 3166-1 alpha-2 com 2 letras maiúsculas (ex.: BR).");

        RuleFor(x => x.AccountNumber)
            .NotEmpty().WithMessage("Informe a conta do beneficiário.")
            .MaximumLength(CreateOrderRequestValidator.MaxAccountNumberLength)
            .WithMessage($"A conta do beneficiário deve ter no máximo {CreateOrderRequestValidator.MaxAccountNumberLength} caracteres.");
    }
}
