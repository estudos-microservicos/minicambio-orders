using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace MiniCambio.Orders.IntegrationTests;

/// <summary>Atalhos para chamar a API de ordens nos testes.</summary>
internal static class OrdersApi
{
    public static object ValidBody(string currency = "USD", string side = "Buy", decimal foreignAmount = 100m, string name = "Maria Silva") =>
        new
        {
            currency,
            side,
            foreignAmount,
            beneficiary = new { name, country = "BR", accountNumber = "12345-6" },
        };

    public static async Task<HttpResponseMessage> PostOrderAsync(this HttpClient client, object body, string? idempotencyKey)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/orders") { Content = JsonContent.Create(body) };
        if (idempotencyKey is not null)
        {
            request.Headers.Add("Idempotency-Key", idempotencyKey);
        }

        return await client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    /// <summary>Cria uma ordem válida e devolve o JSON dela (falha o teste se não vier 201).</summary>
    public static async Task<JsonElement> CreateOrderAsync(this HttpClient client, object? body = null)
    {
        var response = await client.PostOrderAsync(body ?? ValidBody(), Guid.NewGuid().ToString());
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await response.ReadJsonAsync();
    }

    public static Task<HttpResponseMessage> ConfirmOrderAsync(this HttpClient client, Guid id) =>
        client.PostAsync($"/api/v1/orders/{id}/confirm", content: null, TestContext.Current.CancellationToken);

    public static Task<HttpResponseMessage> CancelOrderAsync(this HttpClient client, Guid id) =>
        client.DeleteAsync($"/api/v1/orders/{id}", TestContext.Current.CancellationToken);

    public static async Task<JsonElement> GetOrderAsync(this HttpClient client, Guid id)
    {
        var response = await client.GetAsync($"/api/v1/orders/{id}", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.ReadJsonAsync();
    }

    public static Task<JsonElement> ReadJsonAsync(this HttpResponseMessage response) =>
        response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);

    public static Guid Id(this JsonElement order) => order.GetProperty("id").GetGuid();

    public static string? Status(this JsonElement order) => order.GetProperty("status").GetString();
}
