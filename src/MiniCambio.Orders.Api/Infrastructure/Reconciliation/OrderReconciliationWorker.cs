using Microsoft.Extensions.Options;
using MiniCambio.Orders.Api.Application;

namespace MiniCambio.Orders.Api.Infrastructure.Reconciliation;

/// <summary>
/// Roda um <see cref="OrderReconciliationCycle"/> a cada <c>Orders__ReconciliationIntervalSeconds</c> (padrão 5s):
/// expira cotações vencidas, (re)solicita pagamentos de ordens confirmadas e acompanha os pagamentos solicitados.
/// Esse polling será substituído por eventos (mensageria) numa etapa futura. Graças ao <c>SKIP LOCKED</c> do ciclo,
/// várias réplicas no Kubernetes podem rodar o worker ao mesmo tempo sem processar a mesma ordem.
/// </summary>
public sealed class OrderReconciliationWorker(
    IServiceScopeFactory scopeFactory,
    IOptions<OrdersOptions> options,
    TimeProvider timeProvider,
    ILogger<OrderReconciliationWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(options.Value.ReconciliationIntervalSeconds);
        logger.LogInformation("Worker de reconciliação iniciado (intervalo de {Interval})", interval);

        using var timer = new PeriodicTimer(interval, timeProvider);
        try
        {
            do
            {
                await RunCycleAsync(stoppingToken);
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Encerramento normal da aplicação.
        }
    }

    private async Task RunCycleAsync(CancellationToken stoppingToken)
    {
        try
        {
            // Um escopo por ciclo: DbContext e clientes HTTP novos a cada execução.
            await using var scope = scopeFactory.CreateAsyncScope();
            var cycle = scope.ServiceProvider.GetRequiredService<OrderReconciliationCycle>();
            await cycle.RunOnceAsync(stoppingToken);
        }
        catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
        {
            // Nunca derruba o worker: o próximo ciclo tenta de novo.
            logger.LogError(ex, "Falha no ciclo de reconciliação; tentando de novo no próximo ciclo");
        }
    }
}
