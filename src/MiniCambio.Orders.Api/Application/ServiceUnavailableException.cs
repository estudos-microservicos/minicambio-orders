namespace MiniCambio.Orders.Api.Application;

/// <summary>
/// Dependência externa (Rates ou Payments) indisponível depois das tentativas de resiliência: falha de rede,
/// timeout, circuito aberto, 5xx ou resposta inesperada. Vira 503 na API, com <see cref="Exception.Message"/>
/// como detalhe.
/// </summary>
public sealed class ServiceUnavailableException(string message, Exception? innerException = null)
    : Exception(message, innerException);
