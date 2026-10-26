using FluentValidation;
using MiniCambio.Orders.Api.Domain;

namespace MiniCambio.Orders.Api.Application.Orders;

public sealed class ListOrdersQueryValidator : AbstractValidator<ListOrdersQuery>
{
    public ListOrdersQueryValidator()
    {
        RuleFor(x => x.Status)
            .IsEnumName(typeof(OrderStatus), caseSensitive: false)
            .When(x => !string.IsNullOrEmpty(x.Status))
            .WithMessage($"Status inválido. Use um destes: {string.Join(", ", Enum.GetNames<OrderStatus>())}.");

        RuleFor(x => x.Page)
            .GreaterThanOrEqualTo(1).WithMessage("A página deve ser maior ou igual a 1.");

        RuleFor(x => x.PageSize)
            .InclusiveBetween(1, ListOrdersQuery.MaxPageSize)
            .WithMessage($"O tamanho da página deve estar entre 1 e {ListOrdersQuery.MaxPageSize}.");
    }
}
