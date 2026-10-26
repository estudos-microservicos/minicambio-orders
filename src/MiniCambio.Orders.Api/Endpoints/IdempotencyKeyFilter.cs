using MiniCambio.Orders.Api.Domain;

namespace MiniCambio.Orders.Api.Endpoints;

/// <summary>
/// Exige o header <c>Idempotency-Key</c> (1 a 100 caracteres). Sem ele, devolve 400 com o erro em
/// <c>errors["Idempotency-Key"]</c>, no mesmo formato dos erros de campo do corpo.
/// </summary>
public sealed class IdempotencyKeyFilter : IEndpointFilter
{
    public const string HeaderName = "Idempotency-Key";

    public ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var value = context.HttpContext.Request.Headers[HeaderName].ToString();

        string? error = null;
        if (string.IsNullOrWhiteSpace(value))
        {
            error = "Informe o header Idempotency-Key com um identificador único para esta criação (ex.: um UUID).";
        }
        else if (value.Length > Order.MaxIdempotencyKeyLength)
        {
            error = $"O header Idempotency-Key deve ter no máximo {Order.MaxIdempotencyKeyLength} caracteres.";
        }

        if (error is not null)
        {
            return ValueTask.FromResult<object?>(TypedResults.ValidationProblem(
                new Dictionary<string, string[]> { [HeaderName] = [error] },
                title: ValidationFilter<object>.Title,
                detail: ValidationFilter<object>.Detail));
        }

        return next(context);
    }
}
