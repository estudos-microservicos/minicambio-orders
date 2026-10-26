using System.Net;
using System.Text.Json;

namespace MiniCambio.Orders.IntegrationTests;

/// <summary>Cenários que precisam de banco próprio, relógio controlado ou de "derrubar" o Rates/Payments.</summary>
public class IsolatedScenariosTests(OrdersApiFactory factory)
{
    [Fact]
    public async Task CreateOrder_RatesUnavailable_Returns503AndPersistsNothing()
    {
        await using var api = factory.CreateIsolated();
        api.Rates.Unavailable = true;
        var key = Guid.NewGuid().ToString();

        var response = await api.Client.PostOrderAsync(OrdersApi.ValidBody(), key);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var problem = await response.ReadJsonAsync();
        Assert.Equal("Serviço indisponível", problem.GetProperty("title").GetString());
        Assert.False(string.IsNullOrWhiteSpace(problem.GetProperty("detail").GetString()));
        Assert.Equal(0, (await ListAsync(api, "")).GetProperty("totalCount").GetInt32());

        // Nada ficou gravado com essa chave: com o Rates de volta, a mesma chave cria a ordem (201, não 200).
        api.Rates.Unavailable = false;
        var retry = await api.Client.PostOrderAsync(OrdersApi.ValidBody(), key);
        Assert.Equal(HttpStatusCode.Created, retry.StatusCode);
    }

    [Fact]
    public async Task ConfirmOrder_PaymentsUnavailable_Returns200AndStaysConfirmed()
    {
        await using var api = factory.CreateIsolated();
        var created = await api.Client.CreateOrderAsync();
        api.Payments.Unavailable = true;

        var response = await api.Client.ConfirmOrderAsync(created.Id());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var order = await response.ReadJsonAsync();
        Assert.Equal("Confirmed", order.Status());
        Assert.Equal(JsonValueKind.Null, order.GetProperty("paymentId").ValueKind);
        Assert.False(order.GetProperty("canConfirm").GetBoolean());
        Assert.False(order.GetProperty("canCancel").GetBoolean());
        Assert.Equal("Confirmed", (await api.Client.GetOrderAsync(created.Id())).Status());
    }

    [Fact]
    public async Task ConfirmOrder_QuoteExpired_Returns422AndOrderBecomesExpired()
    {
        await using var api = factory.CreateIsolated();
        var created = await api.Client.CreateOrderAsync();
        api.Time.Advance(TimeSpan.FromSeconds(60));

        Assert.False((await api.Client.GetOrderAsync(created.Id())).GetProperty("canConfirm").GetBoolean());

        var response = await api.Client.ConfirmOrderAsync(created.Id());

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("A cotação expirou. Crie uma nova ordem.", (await response.ReadJsonAsync()).GetProperty("detail").GetString());
        Assert.Equal("Expired", (await api.Client.GetOrderAsync(created.Id())).Status());
        Assert.Equal(0, api.Payments.RequestCount);
    }

    [Fact]
    public async Task ListOrders_StatusFilterPaginationAndOrdering_ReturnsExpectedPages()
    {
        await using var api = factory.CreateIsolated();
        var ids = new List<Guid>();
        for (var i = 0; i < 5; i++)
        {
            ids.Add((await api.Client.CreateOrderAsync()).Id());
            api.Time.Advance(TimeSpan.FromSeconds(1));
        }

        await api.Client.CancelOrderAsync(ids[0]);
        await api.Client.CancelOrderAsync(ids[3]);
        var newestFirst = Enumerable.Reverse(ids).ToList();

        var firstPage = await ListAsync(api, "?page=1&pageSize=2");
        Assert.Equal(5, firstPage.GetProperty("totalCount").GetInt32());
        Assert.Equal(1, firstPage.GetProperty("page").GetInt32());
        Assert.Equal(2, firstPage.GetProperty("pageSize").GetInt32());
        Assert.False(string.IsNullOrWhiteSpace(firstPage.GetProperty("servedBy").GetString()));
        Assert.Equal(newestFirst[..2], ItemIds(firstPage));

        var lastPage = await ListAsync(api, "?page=3&pageSize=2");
        Assert.Equal(newestFirst[4..], ItemIds(lastPage));

        var beyondEnd = await ListAsync(api, "?page=4&pageSize=2");
        Assert.Empty(ItemIds(beyondEnd));
        Assert.Equal(5, beyondEnd.GetProperty("totalCount").GetInt32());

        var cancelled = await ListAsync(api, "?status=cancelled");
        Assert.Equal(2, cancelled.GetProperty("totalCount").GetInt32());
        Assert.Equal([ids[3], ids[0]], ItemIds(cancelled));
        Assert.All(cancelled.GetProperty("items").EnumerateArray(), x => Assert.Equal("Cancelled", x.Status()));

        // Status vazio significa "todos"; sem page/pageSize valem os padrões (página 1, 20 itens).
        var defaults = await ListAsync(api, "?status=");
        Assert.Equal(5, defaults.GetProperty("totalCount").GetInt32());
        Assert.Equal(1, defaults.GetProperty("page").GetInt32());
        Assert.Equal(20, defaults.GetProperty("pageSize").GetInt32());
        Assert.Equal(newestFirst, ItemIds(defaults));
    }

    private static async Task<JsonElement> ListAsync(IsolatedOrdersApi api, string query)
    {
        var response = await api.Client.GetAsync($"/api/v1/orders{query}", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.ReadJsonAsync();
    }

    private static List<Guid> ItemIds(JsonElement page) =>
        [.. page.GetProperty("items").EnumerateArray().Select(x => x.Id())];
}
