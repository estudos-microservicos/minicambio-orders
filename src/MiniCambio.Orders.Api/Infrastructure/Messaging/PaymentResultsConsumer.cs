using System.Text;
using System.Text.Json;
using Confluent.Kafka;
using Microsoft.Extensions.Options;
using MiniCambio.Orders.Api.Application.Payments;

namespace MiniCambio.Orders.Api.Infrastructure.Messaging;

/// <summary>
/// Consome o tópico de resultados de pagamento e entrega cada mensagem ao <see cref="PaymentResultHandler"/>.
/// <list type="bullet">
///   <item>Todas as réplicas usam o mesmo grupo de consumo: o Kafka divide as partições entre elas (com 3 partições
///   e 3 Pods, cada Pod fica com uma). Uma réplica a mais que o número de partições fica ociosa.</item>
///   <item>Entrega "pelo menos uma vez": o offset só é marcado depois que o handler gravou a ordem. Se o Pod morrer
///   no meio, a mensagem é entregue de novo (a outro Pod) e o handler, idempotente, ignora a repetição.</item>
///   <item>Erro transitório (banco fora): volta para a mesma mensagem e tenta de novo depois de alguns segundos.
///   Mensagem ilegível: vai para o log e é pulada, para não travar a partição (não há fila de "mensagens mortas").</item>
/// </list>
/// </summary>
public sealed class PaymentResultsConsumer(
    IServiceScopeFactory scopeFactory,
    IOptions<KafkaOptions> options,
    ILogger<PaymentResultsConsumer> logger) : BackgroundService
{
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(5);

    // Consume() bloqueia a thread enquanto espera mensagens: roda numa thread do pool para não travar a subida da API.
    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        Task.Run(() => ConsumeLoopAsync(stoppingToken), stoppingToken);

    private async Task ConsumeLoopAsync(CancellationToken stoppingToken)
    {
        var config = new ConsumerConfig
        {
            BootstrapServers = options.Value.BootstrapServers,
            GroupId = options.Value.ConsumerGroupId,
            ClientId = Environment.MachineName,

            // Grupo novo (primeira subida): começa do início do tópico, para não perder o que já foi publicado.
            AutoOffsetReset = AutoOffsetReset.Earliest,

            // O commit automático só envia offsets marcados com StoreOffset, e só marcamos depois de processar.
            EnableAutoCommit = true,
            EnableAutoOffsetStore = false,
        };

        using var consumer = new ConsumerBuilder<string, string>(config)
            // Sem isto, o librdkafka escreve os avisos dele direto no console, fora do JSON dos logs.
            .SetLogHandler((_, message) => logger.Log(
                KafkaMessages.ToLogLevel(message.Level), "Kafka ({Facility}): {Message}", message.Facility, message.Message))
            .SetErrorHandler((_, error) => logger.LogWarning("Kafka (consumer): {Reason}", error.Reason))
            .SetPartitionsAssignedHandler((_, partitions) => logger.LogInformation(
                "Partições de {Topic} atribuídas a este Pod: {Partitions}",
                options.Value.PaymentResultsTopic,
                string.Join(", ", partitions.Select(x => x.Partition.Value))))
            .SetPartitionsRevokedHandler((_, partitions) => logger.LogInformation(
                "Partições de {Topic} retiradas deste Pod: {Partitions}",
                options.Value.PaymentResultsTopic,
                string.Join(", ", partitions.Select(x => x.Partition.Value))))
            .Build();

        consumer.Subscribe(options.Value.PaymentResultsTopic);
        logger.LogInformation(
            "Consumidor de {Topic} iniciado (grupo {GroupId})", options.Value.PaymentResultsTopic, options.Value.ConsumerGroupId);

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                ConsumeResult<string, string> result;
                try
                {
                    result = consumer.Consume(stoppingToken);
                }
                catch (ConsumeException ex)
                {
                    logger.LogWarning(ex, "Falha ao ler do Kafka; tentando de novo");
                    continue;
                }

                if (await TryHandleAsync(result, stoppingToken))
                {
                    consumer.StoreOffset(result);
                }
                else
                {
                    // Volta o "cursor" para a mesma mensagem: o próximo Consume() a entrega de novo.
                    consumer.Seek(result.TopicPartitionOffset);
                    await Task.Delay(RetryDelay, stoppingToken);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Encerramento normal da aplicação.
        }
        finally
        {
            // Envia os últimos offsets e avisa o grupo que este Pod saiu: as partições dele vão para as outras réplicas.
            consumer.Close();
        }
    }

    /// <summary><c>true</c> quando a mensagem pode ser dada como lida (processada ou descartada).</summary>
    private async Task<bool> TryHandleAsync(ConsumeResult<string, string> result, CancellationToken stoppingToken)
    {
        var scopeState = new Dictionary<string, object>
        {
            ["KafkaTopic"] = result.Topic,
            ["KafkaPartition"] = result.Partition.Value,
            ["KafkaOffset"] = result.Offset.Value,
        };
        if (result.Message.Headers is { } headers
            && headers.TryGetLastBytes(KafkaMessages.CorrelationIdHeader, out var bytes))
        {
            scopeState["CorrelationId"] = Encoding.UTF8.GetString(bytes);
        }

        using var logScope = logger.BeginScope(scopeState);

        PaymentResultMessage? message;
        try
        {
            message = JsonSerializer.Deserialize<PaymentResultMessage>(result.Message.Value, KafkaMessages.JsonOptions);
        }
        catch (JsonException ex)
        {
            logger.LogError(ex, "Mensagem ilegível em {Topic}; descartada", result.Topic);
            return true;
        }

        if (message is null)
        {
            logger.LogError("Mensagem vazia em {Topic}; descartada", result.Topic);
            return true;
        }

        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<PaymentResultHandler>().HandleAsync(message, stoppingToken);
            return true;
        }
        catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
        {
            logger.LogError(
                ex, "Falha ao aplicar o resultado da ordem {OrderId}; tentando de novo em {Delay}", message.OrderId, RetryDelay);
            return false;
        }
    }
}
