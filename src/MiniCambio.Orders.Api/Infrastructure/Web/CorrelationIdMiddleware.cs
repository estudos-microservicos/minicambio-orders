using System.Text.RegularExpressions;
using MiniCambio.Orders.Api.Infrastructure.Correlation;

namespace MiniCambio.Orders.Api.Infrastructure.Web;

/// <summary>
/// Lê o <c>X-Correlation-Id</c> da requisição (ou gera um novo), devolve no response, coloca no escopo de log e no
/// <see cref="CorrelationIdAccessor"/>, de onde o <c>CorrelationIdDelegatingHandler</c> o propaga para o Rates e o
/// Payments. Assim todas as linhas de log da requisição, nos três serviços, carregam o mesmo id.
/// </summary>
public sealed partial class CorrelationIdMiddleware(
    RequestDelegate next,
    CorrelationIdAccessor correlationIdAccessor,
    ILogger<CorrelationIdMiddleware> logger)
{
    public const string HeaderName = "X-Correlation-Id";

    public async Task InvokeAsync(HttpContext context)
    {
        var correlationId = context.Request.Headers[HeaderName].FirstOrDefault();

        // Só aceita ids "bem comportados" vindos de fora, para não levar lixo para os logs.
        if (correlationId is null || !ValidCorrelationId().IsMatch(correlationId))
        {
            correlationId = Guid.NewGuid().ToString();
        }

        context.Items[HeaderName] = correlationId;
        correlationIdAccessor.CorrelationId = correlationId;
        context.Response.OnStarting(() =>
        {
            context.Response.Headers[HeaderName] = correlationId;
            return Task.CompletedTask;
        });

        using (logger.BeginScope(new Dictionary<string, object> { ["CorrelationId"] = correlationId }))
        {
            await next(context);
        }
    }

    public static string? GetCorrelationId(HttpContext context) => context.Items[HeaderName] as string;

    [GeneratedRegex("^[A-Za-z0-9._-]{1,64}$")]
    private static partial Regex ValidCorrelationId();
}
