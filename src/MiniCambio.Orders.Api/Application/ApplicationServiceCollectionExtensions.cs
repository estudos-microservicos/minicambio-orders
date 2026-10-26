using System.Text.Json;
using FluentValidation;
using FluentValidation.Internal;
using MiniCambio.Orders.Api.Application.Orders;

namespace MiniCambio.Orders.Api.Application;

public static class ApplicationServiceCollectionExtensions
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        services.AddOptions<OrdersOptions>()
            .BindConfiguration(OrdersOptions.SectionName)
            .ValidateDataAnnotations()
            .ValidateOnStart();

        // Nomes de campo nos erros seguem o JSON (camelCase, com ponto nos aninhados: beneficiary.name),
        // para o cliente mapear cada erro ao seu campo.
        ValidatorOptions.Global.PropertyNameResolver = (_, member, expression) =>
        {
            var path = expression is null ? member?.Name : PropertyChain.FromExpression(expression).ToString();
            return path is null ? null : string.Join('.', path.Split('.').Select(JsonNamingPolicy.CamelCase.ConvertName));
        };
        services.AddValidatorsFromAssemblyContaining<CreateOrderRequestValidator>();

        services.AddScoped<CreateOrderHandler>();
        services.AddScoped<ConfirmOrderHandler>();
        services.AddScoped<CancelOrderHandler>();
        services.AddScoped<GetOrderHandler>();
        services.AddScoped<ListOrdersHandler>();

        return services;
    }
}
