using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Time.Testing;
using MiniCambio.Orders.Api.Application.Payments;
using MiniCambio.Orders.Api.Application.Rates;
using MiniCambio.Orders.Api.Infrastructure.Messaging;
using MiniCambio.Orders.Api.Infrastructure.Reconciliation;
using Npgsql;
using Testcontainers.PostgreSql;

[assembly: AssemblyFixture(typeof(MiniCambio.Orders.IntegrationTests.OrdersApiFactory))]

namespace MiniCambio.Orders.IntegrationTests;

/// <summary>
/// Sobe a API contra um Postgres real (Testcontainers), compartilhado por todos os testes do assembly.
/// O Rates e o Kafka são substituídos por fakes; o worker de reconciliação e o consumidor do Kafka saem: os testes
/// rodam o ciclo e entregam os resultados de pagamento diretamente quando precisam.
/// </summary>
public sealed class OrdersApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly PostgreSqlContainer database = new PostgreSqlBuilder("postgres:17-alpine").Build();

    public async ValueTask InitializeAsync() => await database.StartAsync();

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        await database.DisposeAsync();
    }

    /// <summary>
    /// Outra instância da API com banco próprio (um database novo no mesmo container) e relógio controlado.
    /// Usada pelos testes que dependem do tempo, que precisam contar as ordens do banco ou que rodam o ciclo de
    /// reconciliação (que processa todas as ordens do banco).
    /// </summary>
    public IsolatedOrdersApi CreateIsolated()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 5, 10, 12, 0, 0, TimeSpan.Zero));
        var connectionString = new NpgsqlConnectionStringBuilder(database.GetConnectionString())
        {
            Database = $"orders_{Guid.NewGuid():N}",
        }.ConnectionString;

        var factory = WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:Default", connectionString);
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<TimeProvider>();
                services.AddSingleton<TimeProvider>(time);
            });
        });

        return new IsolatedOrdersApi(factory, time, connectionString);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.UseSetting("ConnectionStrings:Default", database.GetConnectionString());
        builder.UseSetting("Database:ApplyMigrationsOnStartup", "true");

        builder.ConfigureTestServices(services =>
        {
            services.Remove(services.Single(descriptor =>
                descriptor.ServiceType == typeof(IHostedService)
                && descriptor.ImplementationType == typeof(OrderReconciliationWorker)));

            // Sem Kafka nos testes: o consumidor sai e os testes chamam o PaymentResultHandler diretamente.
            services.Remove(services.Single(descriptor =>
                descriptor.ServiceType == typeof(IHostedService)
                && descriptor.ImplementationType == typeof(PaymentResultsConsumer)));

            services.RemoveAll<IRatesClient>();
            services.AddSingleton<FakeRatesClient>();
            services.AddSingleton<IRatesClient>(provider => provider.GetRequiredService<FakeRatesClient>());

            services.RemoveAll<IPaymentRequestPublisher>();
            services.AddSingleton<FakePaymentRequestPublisher>();
            services.AddSingleton<IPaymentRequestPublisher>(provider => provider.GetRequiredService<FakePaymentRequestPublisher>());
        });
    }

    public FakePaymentRequestPublisher PaymentRequests => Services.GetRequiredService<FakePaymentRequestPublisher>();

    /// <summary>Entrega um resultado de pagamento como o consumidor do Kafka faria.</summary>
    public Task<PaymentResultOutcome> ApplyPaymentResultAsync(PaymentResultMessage message) =>
        ApplyPaymentResultAsync(Services, message);

    internal static async Task<PaymentResultOutcome> ApplyPaymentResultAsync(IServiceProvider services, PaymentResultMessage message)
    {
        await using var scope = services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<PaymentResultHandler>()
            .HandleAsync(message, TestContext.Current.CancellationToken);
    }
}

/// <summary>API com banco e relógio próprios. Ao descartar, encerra o host e fecha as conexões do banco.</summary>
public sealed class IsolatedOrdersApi(WebApplicationFactory<Program> factory, FakeTimeProvider time, string connectionString)
    : IAsyncDisposable
{
    private HttpClient? client;

    public FakeTimeProvider Time { get; } = time;

    public HttpClient Client => client ??= factory.CreateClient();

    public FakeRatesClient Rates => Services.GetRequiredService<FakeRatesClient>();

    public FakePaymentRequestPublisher PaymentRequests => Services.GetRequiredService<FakePaymentRequestPublisher>();

    /// <summary>Entrega um resultado de pagamento como o consumidor do Kafka faria.</summary>
    public Task<PaymentResultOutcome> ApplyPaymentResultAsync(PaymentResultMessage message) =>
        OrdersApiFactory.ApplyPaymentResultAsync(Services, message);

    private IServiceProvider Services
    {
        get
        {
            _ = Client; // garante que o host subiu (e aplicou as migrations)
            return factory.Services;
        }
    }

    /// <summary>Roda um ciclo de reconciliação, como o worker faria.</summary>
    public async Task<ReconciliationResult> RunReconciliationAsync()
    {
        await using var scope = Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<OrderReconciliationCycle>()
            .RunOnceAsync(TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        client?.Dispose();
        await factory.DisposeAsync();
        using var connection = new NpgsqlConnection(connectionString);
        NpgsqlConnection.ClearPool(connection);
    }
}
