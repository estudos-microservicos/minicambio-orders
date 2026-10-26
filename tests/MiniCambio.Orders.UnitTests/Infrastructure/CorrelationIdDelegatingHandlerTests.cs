using System.Net;
using MiniCambio.Orders.Api.Infrastructure.Correlation;
using MiniCambio.Orders.Api.Infrastructure.Http;

namespace MiniCambio.Orders.UnitTests.Infrastructure;

public class CorrelationIdDelegatingHandlerTests
{
    private const string HeaderName = "X-Correlation-Id";

    private readonly CorrelationIdAccessor accessor = new();
    private readonly StubHttpMessageHandler inner = StubHttpMessageHandler.Returning(HttpStatusCode.OK);
    private readonly HttpMessageInvoker invoker;

    public CorrelationIdDelegatingHandlerTests()
    {
        invoker = new HttpMessageInvoker(new CorrelationIdDelegatingHandler(accessor) { InnerHandler = inner });
    }

    [Fact]
    public async Task SendAsync_CorrelationIdInCurrentFlow_PropagatesHeader()
    {
        accessor.CorrelationId = "pedido-123";

        await SendAsync();

        Assert.Equal("pedido-123", Assert.Single(Assert.Single(inner.Requests).Headers.GetValues(HeaderName)));
    }

    [Fact]
    public async Task SendAsync_CorrelationIdSetInAsyncCaller_FlowsToOutgoingCall()
    {
        // Simula o middleware/ciclo: o id é definido num método assíncrono que, depois, faz a chamada de saída.
        async Task CallerAsync()
        {
            accessor.CorrelationId = "ciclo-42";
            await Task.Yield();
            await SendAsync();
        }

        await CallerAsync();

        Assert.Equal("ciclo-42", Assert.Single(Assert.Single(inner.Requests).Headers.GetValues(HeaderName)));
        Assert.Null(accessor.CorrelationId); // o valor não vaza para fora do fluxo que o definiu
    }

    [Fact]
    public async Task SendAsync_WithoutCorrelationId_GeneratesOne()
    {
        await SendAsync();

        var value = Assert.Single(Assert.Single(inner.Requests).Headers.GetValues(HeaderName));
        Assert.True(Guid.TryParse(value, out _));
    }

    [Fact]
    public async Task SendAsync_RequestAlreadyHasHeader_KeepsExistingValue()
    {
        accessor.CorrelationId = "do-contexto";
        using var request = new HttpRequestMessage(HttpMethod.Get, "http://servico-falso/");
        request.Headers.Add(HeaderName, "explicito");

        await invoker.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal("explicito", Assert.Single(Assert.Single(inner.Requests).Headers.GetValues(HeaderName)));
    }

    private async Task SendAsync()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "http://servico-falso/");
        using var response = await invoker.SendAsync(request, TestContext.Current.CancellationToken);
    }
}
