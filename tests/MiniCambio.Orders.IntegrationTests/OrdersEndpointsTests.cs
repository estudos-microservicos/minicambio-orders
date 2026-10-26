using System.Net;
using System.Text;
using System.Text.Json;

namespace MiniCambio.Orders.IntegrationTests;

/// <summary>Endpoints de ordens contra o banco compartilhado (cada teste só olha as ordens que ele mesmo criou).</summary>
public class OrdersEndpointsTests(OrdersApiFactory factory)
{
    private readonly HttpClient client = factory.CreateClient();

    // ---------------------------------------------------------------- POST /orders

    [Fact]
    public async Task CreateOrder_ValidRequest_Returns201WithLocationAndQuotedOrder()
    {
        var response = await client.PostOrderAsync(OrdersApi.ValidBody(side: "sell", foreignAmount: 100m), Guid.NewGuid().ToString());

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var order = await response.ReadJsonAsync();
        var id = order.Id();
        Assert.Equal($"/api/v1/orders/{id}", response.Headers.Location?.OriginalString);

        Assert.Equal("Quoted", order.Status());
        Assert.Equal("USD", order.GetProperty("currency").GetString());
        Assert.Equal("Sell", order.GetProperty("side").GetString());
        Assert.Equal(100m, order.GetProperty("foreignAmount").GetDecimal());
        Assert.NotEqual(Guid.Empty, order.GetProperty("quoteId").GetGuid());
        Assert.Equal(FakeRatesClient.Rate, order.GetProperty("rate").GetDecimal());
        Assert.Equal(542.70m, order.GetProperty("brlAmount").GetDecimal());
        Assert.InRange(
            order.GetProperty("quoteExpiresAt").GetDateTimeOffset() - order.GetProperty("createdAt").GetDateTimeOffset(),
            TimeSpan.FromSeconds(59),
            TimeSpan.FromSeconds(60));

        var beneficiary = order.GetProperty("beneficiary");
        Assert.Equal("Maria Silva", beneficiary.GetProperty("name").GetString());
        Assert.Equal("BR", beneficiary.GetProperty("country").GetString());
        Assert.Equal("12345-6", beneficiary.GetProperty("accountNumber").GetString());

        Assert.Equal(JsonValueKind.Null, order.GetProperty("paymentId").ValueKind);
        Assert.Equal(JsonValueKind.Null, order.GetProperty("failureReason").ValueKind);
        Assert.Equal(JsonValueKind.Null, order.GetProperty("confirmedAt").ValueKind);
        Assert.Equal(JsonValueKind.Null, order.GetProperty("cancelledAt").ValueKind);
        Assert.True(order.GetProperty("canConfirm").GetBoolean());
        Assert.True(order.GetProperty("canCancel").GetBoolean());
        Assert.False(string.IsNullOrWhiteSpace(order.GetProperty("servedBy").GetString()));
        Assert.False(order.TryGetProperty("idempotencyKey", out _));
    }

    [Fact]
    public async Task CreateOrder_SameIdempotencyKey_Returns200WithSameOrder()
    {
        var key = Guid.NewGuid().ToString();
        var first = await client.PostOrderAsync(OrdersApi.ValidBody(), key);

        var second = await client.PostOrderAsync(OrdersApi.ValidBody(), key);

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal((await first.ReadJsonAsync()).Id(), (await second.ReadJsonAsync()).Id());
    }

    [Fact]
    public async Task CreateOrder_SameKeyInParallel_CreatesSingleOrder()
    {
        var key = Guid.NewGuid().ToString();

        var responses = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => client.PostOrderAsync(OrdersApi.ValidBody(), key)));

        Assert.All(responses, r => Assert.True(r.StatusCode is HttpStatusCode.Created or HttpStatusCode.OK, r.StatusCode.ToString()));
        Assert.Single(responses, r => r.StatusCode == HttpStatusCode.Created);
        var ids = await Task.WhenAll(responses.Select(async r => (await r.ReadJsonAsync()).Id()));
        Assert.Single(ids.Distinct());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("   ")]
    public async Task CreateOrder_MissingIdempotencyKey_Returns400WithHeaderError(string? key)
    {
        var response = await client.PostOrderAsync(OrdersApi.ValidBody(), key);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.ReadJsonAsync();
        Assert.Equal("Dados inválidos", problem.GetProperty("title").GetString());
        Assert.True(problem.GetProperty("errors").TryGetProperty("Idempotency-Key", out _));
    }

    [Fact]
    public async Task CreateOrder_IdempotencyKeyTooLong_Returns400WithHeaderError()
    {
        var response = await client.PostOrderAsync(OrdersApi.ValidBody(), new string('k', 101));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True((await response.ReadJsonAsync()).GetProperty("errors").TryGetProperty("Idempotency-Key", out _));
    }

    [Fact]
    public async Task CreateOrder_InvalidBody_Returns400WithCamelCaseFieldErrors()
    {
        var body = new
        {
            currency = "usd",
            side = "Hold",
            foreignAmount = 10.123m,
            beneficiary = new { name = "", country = "Brasil", accountNumber = new string('1', 35) },
        };

        var response = await client.PostOrderAsync(body, Guid.NewGuid().ToString());

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.ReadJsonAsync();
        Assert.Equal("Dados inválidos", problem.GetProperty("title").GetString());
        Assert.Equal("Corrija os campos destacados e tente novamente.", problem.GetProperty("detail").GetString());
        var errors = problem.GetProperty("errors");
        Assert.Equal(
            ["beneficiary.accountNumber", "beneficiary.country", "beneficiary.name", "currency", "foreignAmount", "side"],
            errors.EnumerateObject().Select(x => x.Name).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task CreateOrder_MissingBeneficiary_Returns400WithBeneficiaryError()
    {
        var response = await client.PostOrderAsync(new { currency = "USD", side = "Buy", foreignAmount = 1m }, Guid.NewGuid().ToString());

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True((await response.ReadJsonAsync()).GetProperty("errors").TryGetProperty("beneficiary", out _));
    }

    [Fact]
    public async Task CreateOrder_MalformedJson_Returns400ProblemDetails()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/orders")
        {
            Content = new StringContent("{ \"currency\": ", Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());

        var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("Requisição inválida", (await response.ReadJsonAsync()).GetProperty("title").GetString());
    }

    [Fact]
    public async Task CreateOrder_RatesRejectsCurrency_Returns422WithRatesDetail()
    {
        var response = await client.PostOrderAsync(OrdersApi.ValidBody(currency: "XYZ"), Guid.NewGuid().ToString());

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var problem = await response.ReadJsonAsync();
        Assert.Equal("Operação não permitida", problem.GetProperty("title").GetString());
        Assert.Equal("A moeda XYZ não é suportada.", problem.GetProperty("detail").GetString());
    }

    // ---------------------------------------------------------------- POST /orders/{id}/confirm

    [Fact]
    public async Task ConfirmOrder_Quoted_Returns200WithPaymentRequested()
    {
        var created = await client.CreateOrderAsync();

        var response = await client.ConfirmOrderAsync(created.Id());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var order = await response.ReadJsonAsync();
        Assert.Equal("PaymentRequested", order.Status());
        Assert.NotEqual(Guid.Empty, order.GetProperty("paymentId").GetGuid());
        Assert.Equal(JsonValueKind.String, order.GetProperty("confirmedAt").ValueKind);
        Assert.False(order.GetProperty("canConfirm").GetBoolean());
        Assert.False(order.GetProperty("canCancel").GetBoolean());

        Assert.Equal("PaymentRequested", (await client.GetOrderAsync(created.Id())).Status());
    }

    [Fact]
    public async Task ConfirmOrder_Twice_Returns200WithSamePayment()
    {
        var created = await client.CreateOrderAsync();
        var first = await (await client.ConfirmOrderAsync(created.Id())).ReadJsonAsync();

        var response = await client.ConfirmOrderAsync(created.Id());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var second = await response.ReadJsonAsync();
        Assert.Equal("PaymentRequested", second.Status());
        Assert.Equal(first.GetProperty("paymentId").GetGuid(), second.GetProperty("paymentId").GetGuid());
    }

    [Fact]
    public async Task ConfirmOrder_Cancelled_Returns422()
    {
        var created = await client.CreateOrderAsync();
        await client.CancelOrderAsync(created.Id());

        var response = await client.ConfirmOrderAsync(created.Id());

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var problem = await response.ReadJsonAsync();
        Assert.Equal("Operação não permitida", problem.GetProperty("title").GetString());
        Assert.Equal("Esta ordem foi cancelada e não pode mais ser confirmada.", problem.GetProperty("detail").GetString());
        Assert.Equal("Cancelled", (await client.GetOrderAsync(created.Id())).Status());
    }

    [Fact]
    public async Task ConfirmOrder_Unknown_Returns404()
    {
        var response = await client.ConfirmOrderAsync(Guid.NewGuid());

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ---------------------------------------------------------------- DELETE /orders/{id}

    [Fact]
    public async Task CancelOrder_Quoted_Returns204AndOrderBecomesCancelled()
    {
        var created = await client.CreateOrderAsync();

        var response = await client.CancelOrderAsync(created.Id());

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var order = await client.GetOrderAsync(created.Id());
        Assert.Equal("Cancelled", order.Status());
        Assert.Equal(JsonValueKind.String, order.GetProperty("cancelledAt").ValueKind);
        Assert.False(order.GetProperty("canConfirm").GetBoolean());
        Assert.False(order.GetProperty("canCancel").GetBoolean());
    }

    [Fact]
    public async Task CancelOrder_Twice_Returns204()
    {
        var created = await client.CreateOrderAsync();
        await client.CancelOrderAsync(created.Id());

        var response = await client.CancelOrderAsync(created.Id());

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    [Fact]
    public async Task CancelOrder_Confirmed_Returns422()
    {
        var created = await client.CreateOrderAsync();
        await client.ConfirmOrderAsync(created.Id());

        var response = await client.CancelOrderAsync(created.Id());

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal(
            "Só é possível cancelar ordens que ainda não foram confirmadas.",
            (await response.ReadJsonAsync()).GetProperty("detail").GetString());
        Assert.Equal("PaymentRequested", (await client.GetOrderAsync(created.Id())).Status());
    }

    [Fact]
    public async Task CancelOrder_Unknown_Returns404()
    {
        var response = await client.CancelOrderAsync(Guid.NewGuid());

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ---------------------------------------------------------------- GET

    [Fact]
    public async Task GetOrder_Unknown_Returns404ProblemDetails()
    {
        var response = await client.GetAsync($"/api/v1/orders/{Guid.NewGuid()}", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var problem = await response.ReadJsonAsync();
        Assert.Equal("Recurso não encontrado", problem.GetProperty("title").GetString());
        Assert.Equal("Ordem não encontrada.", problem.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task ListOrders_InvalidParameters_Returns400WithFieldErrors()
    {
        var response = await client.GetAsync("/api/v1/orders?status=Done&page=0&pageSize=101", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var errors = (await response.ReadJsonAsync()).GetProperty("errors");
        Assert.Equal(["page", "pageSize", "status"], errors.EnumerateObject().Select(x => x.Name).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task ListOrders_NonNumericPage_Returns400()
    {
        var response = await client.GetAsync("/api/v1/orders?page=abc", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("Requisição inválida", (await response.ReadJsonAsync()).GetProperty("title").GetString());
    }

    // ---------------------------------------------------------------- infraestrutura HTTP

    [Fact]
    public async Task Request_WithCorrelationId_EchoesHeaderAndAddsItToProblemDetails()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/orders/{Guid.NewGuid()}");
        request.Headers.Add("X-Correlation-Id", "teste-123");

        var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal("teste-123", Assert.Single(response.Headers.GetValues("X-Correlation-Id")));
        Assert.Equal("teste-123", (await response.ReadJsonAsync()).GetProperty("correlationId").GetString());
    }

    [Fact]
    public async Task Request_WithoutCorrelationId_GeneratesOne()
    {
        var response = await client.GetAsync("/api/v1/orders", TestContext.Current.CancellationToken);

        Assert.True(Guid.TryParse(Assert.Single(response.Headers.GetValues("X-Correlation-Id")), out _));
    }

    [Theory]
    [InlineData("/health/live")]
    [InlineData("/health/ready")]
    public async Task HealthChecks_DatabaseUp_ReturnHealthy(string path)
    {
        var response = await client.GetAsync(path, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Healthy", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }
}
