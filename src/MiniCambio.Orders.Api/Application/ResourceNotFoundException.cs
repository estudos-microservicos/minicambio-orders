namespace MiniCambio.Orders.Api.Application;

/// <summary>Recurso inexistente. Vira 404 na API, com <see cref="Exception.Message"/> como detalhe.</summary>
public sealed class ResourceNotFoundException(string message) : Exception(message);
