using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;

namespace MiniCambio.Orders.Api.Infrastructure.Persistence;

/// <summary>Seção <c>Database</c> da configuração.</summary>
public sealed class DatabaseOptions
{
    public const string SectionName = "Database";

    /// <summary>Aplica as migrations pendentes na inicialização (<c>Database__ApplyMigrationsOnStartup</c>).</summary>
    public bool ApplyMigrationsOnStartup { get; set; }

    [Range(1, 60)]
    public int MigrationMaxAttempts { get; set; } = 10;
}

public static class DatabaseMigrator
{
    /// <summary>
    /// Aplica as migrations quando <c>Database__ApplyMigrationsOnStartup=true</c> (ligado no compose).
    /// No Kubernetes isso vai virar um Job/init container, para que várias réplicas subindo ao mesmo tempo
    /// não disputem a mesma migration.
    /// </summary>
    public static async Task ApplyMigrationsIfConfiguredAsync(this WebApplication app)
    {
        var options = app.Services.GetRequiredService<IOptions<DatabaseOptions>>().Value;
        if (!options.ApplyMigrationsOnStartup)
        {
            return;
        }

        var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(DatabaseMigrator));

        // O banco pode ainda estar terminando de subir: tenta algumas vezes antes de desistir.
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await using var scope = app.Services.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<OrdersDbContext>();
                await db.Database.MigrateAsync();
                logger.LogInformation("Migrations aplicadas");
                return;
            }
            catch (NpgsqlException ex) when (attempt < options.MigrationMaxAttempts)
            {
                logger.LogWarning(ex, "Banco indisponível ao aplicar migrations (tentativa {Attempt}); tentando de novo", attempt);
                await Task.Delay(TimeSpan.FromSeconds(2));
            }
        }
    }
}
