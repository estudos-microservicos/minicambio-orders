using FluentValidation;

namespace MiniCambio.Orders.Api.Endpoints;

/// <summary>Valida o argumento <typeparamref name="T"/> com FluentValidation e devolve 400 com os erros por campo.</summary>
public sealed class ValidationFilter<T> : IEndpointFilter
    where T : class
{
    public const string Title = "Dados inválidos";
    public const string Detail = "Corrija os campos destacados e tente novamente.";

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var argument = context.Arguments.OfType<T>().FirstOrDefault();
        if (argument is null)
        {
            return TypedResults.Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: "Requisição inválida",
                detail: "O corpo da requisição é obrigatório.");
        }

        var validator = context.HttpContext.RequestServices.GetRequiredService<IValidator<T>>();
        var result = await validator.ValidateAsync(argument, context.HttpContext.RequestAborted);
        if (!result.IsValid)
        {
            return TypedResults.ValidationProblem(result.ToDictionary(), title: Title, detail: Detail);
        }

        return await next(context);
    }
}
