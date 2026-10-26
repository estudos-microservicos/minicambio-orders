using System.Net;
using System.Text;

namespace MiniCambio.Orders.UnitTests.Infrastructure;

/// <summary>Handler HTTP falso: devolve a resposta configurada (ou lança a exceção) e guarda as requisições.</summary>
internal sealed class StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
{
    public List<HttpRequestMessage> Requests { get; } = [];

    public List<string?> RequestBodies { get; } = [];

    public static StubHttpMessageHandler Returning(HttpStatusCode status, string? json = null) =>
        new(_ => Json(status, json));

    public static StubHttpMessageHandler Throwing(Exception exception) => new(_ => throw exception);

    public static HttpResponseMessage Json(HttpStatusCode status, string? json) => new(status)
    {
        Content = json is null ? null : new StringContent(json, Encoding.UTF8, "application/json"),
    };

    public HttpClient CreateClient() => new(this) { BaseAddress = new Uri("http://servico-falso:8080") };

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        RequestBodies.Add(request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken));
        return respond(request);
    }
}
