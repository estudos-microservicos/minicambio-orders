namespace MiniCambio.Orders.Api.Infrastructure.Correlation;

/// <summary>
/// Correlation id da execução atual, guardado num <see cref="AsyncLocal{T}"/>: o valor acompanha o fluxo
/// assíncrono em que foi definido. É preenchido pelo <c>CorrelationIdMiddleware</c> (requisições HTTP) e pelo ciclo
/// de reconciliação (um id por ciclo), e lido pelo <c>CorrelationIdDelegatingHandler</c> nas chamadas de saída.
/// Diferente do <c>IHttpContextAccessor</c>, funciona também fora de uma requisição (no worker).
/// </summary>
public sealed class CorrelationIdAccessor
{
    private readonly AsyncLocal<string?> current = new();

    public string? CorrelationId
    {
        get => current.Value;
        set => current.Value = value;
    }
}
