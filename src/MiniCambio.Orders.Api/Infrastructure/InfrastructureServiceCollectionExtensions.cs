using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Options;
using MiniCambio.Orders.Api.Application;
using MiniCambio.Orders.Api.Application.Payments;
using MiniCambio.Orders.Api.Application.Rates;
using MiniCambio.Orders.Api.Infrastructure.Correlation;
using MiniCambio.Orders.Api.Infrastructure.Http;
using MiniCambio.Orders.Api.Infrastructure.Persistence;
using MiniCambio.Orders.Api.Infrastructure.Reconciliation;

namespace MiniCambio.Orders.Api.Infrastructure;

public static class InfrastructureServiceCollectionExtensions
{
    public const string ReadyTag = "ready";

    public static IServiceCollection AddInfrastructure(this IServiceCollection services)
    {
        services.AddSingleton(TimeProvider.System);

        services.AddOptions<DatabaseOptions>()
            .BindConfiguration(DatabaseOptions.SectionName)
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddDbContext<OrdersDbContext>((serviceProvider, options) =>
        {
            var connectionString = serviceProvider.GetRequiredService<IConfiguration>().GetConnectionString("Default");
            if (string.IsNullOrWhiteSpace(connectionString))
            {
                throw new InvalidOperationException(
                    "Connection string 'Default' não configurada (variável ConnectionStrings__Default).");
            }

            options.UseNpgsql(connectionString).UseSnakeCaseNamingConvention();
        });

        services.AddScoped<IOrderRepository, OrderRepository>();

        AddHttpClients(services);

        services.AddScoped<OrderReconciliationCycle>();
        services.AddHostedService<OrderReconciliationWorker>();

        // Live não tem dependências (só "o processo responde"); ready inclui o banco.
        // Rates e Payments ficam de fora de propósito: um serviço fora não deve tirar o Orders do balanceamento.
        services.AddHealthChecks().AddDbContextCheck<OrdersDbContext>("database", tags: [ReadyTag]);

        return services;
    }

    private static void AddHttpClients(IServiceCollection services)
    {
        services.AddOptions<ServicesOptions>()
            .BindConfiguration(ServicesOptions.SectionName)
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddSingleton<CorrelationIdAccessor>();
        services.AddTransient<CorrelationIdDelegatingHandler>();

        services.AddHttpClient<IRatesClient, RatesClient>((serviceProvider, client) =>
                client.BaseAddress = new Uri(serviceProvider.GetRequiredService<IOptions<ServicesOptions>>().Value.RatesApi))
            .AddHttpMessageHandler<CorrelationIdDelegatingHandler>()
            .AddStandardResilienceHandler(ConfigureResilience);

        services.AddHttpClient<IPaymentsClient, PaymentsClient>((serviceProvider, client) =>
                client.BaseAddress = new Uri(serviceProvider.GetRequiredService<IOptions<ServicesOptions>>().Value.PaymentsApi))
            .AddHttpMessageHandler<CorrelationIdDelegatingHandler>()
            .AddStandardResilienceHandler(ConfigureResilience);
    }

    /// <summary>
    /// Retry com backoff exponencial, timeout por tentativa e total, e circuit breaker. Os tempos são menores que os
    /// padrões (10s por tentativa, 30s no total, 2s de espera inicial) para a tela de criar/confirmar ordem não ficar
    /// travada quando o Rates ou o Payments estiverem fora.
    /// </summary>
    private static void ConfigureResilience(HttpStandardResilienceOptions options)
    {
        options.AttemptTimeout.Timeout = TimeSpan.FromSeconds(5);
        options.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(15);
        options.Retry.Delay = TimeSpan.FromMilliseconds(500);

        // O circuit breaker exige SamplingDuration >= 2x o timeout por tentativa.
        options.CircuitBreaker.SamplingDuration = TimeSpan.FromSeconds(10);
    }
}
