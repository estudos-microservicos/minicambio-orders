using System.Text.Json;
using System.Text.Json.Serialization;
using Polly;

namespace MiniCambio.Orders.Api.Infrastructure.Http;

/// <summary>Utilitários comuns aos clientes HTTP do Rates e do Payments.</summary>
internal static class ExternalCall
{
    /// <summary>JSON camelCase com enums como texto, igual ao dos outros serviços.</summary>
    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>
    /// Falhas que significam "o serviço não respondeu direito" depois das tentativas do resilience handler:
    /// rede (<see cref="HttpRequestException"/>), timeout ou circuito aberto (<see cref="ExecutionRejectedException"/>),
    /// timeout do próprio <see cref="HttpClient"/> e corpo ilegível. Cancelamento pedido pelo chamador não entra.
    /// </summary>
    public static bool IsUnavailable(Exception exception, CancellationToken cancellationToken) => exception switch
    {
        HttpRequestException or ExecutionRejectedException or JsonException => true,
        OperationCanceledException => !cancellationToken.IsCancellationRequested,
        _ => false,
    };

    /// <summary>Lê o <c>detail</c> de um ProblemDetails, se a resposta tiver um.</summary>
    public static async Task<string?> ReadProblemDetailAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            using var document = await JsonDocument.ParseAsync(
                await response.Content.ReadAsStreamAsync(cancellationToken),
                cancellationToken: cancellationToken);

            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("detail", out var detail)
                && detail.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(detail.GetString())
                    ? detail.GetString()
                    : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static HttpRequestException UnexpectedStatus(string service, HttpResponseMessage response) =>
        new($"{service} respondeu {(int)response.StatusCode} ({response.StatusCode}).", null, response.StatusCode);
}
