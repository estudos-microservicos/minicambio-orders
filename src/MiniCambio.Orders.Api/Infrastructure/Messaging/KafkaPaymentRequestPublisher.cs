using System.Text;
using System.Text.Json;
using Confluent.Kafka;
using Microsoft.Extensions.Options;
using MiniCambio.Orders.Api.Application;
using MiniCambio.Orders.Api.Application.Payments;
using MiniCambio.Orders.Api.Infrastructure.Correlation;

namespace MiniCambio.Orders.Api.Infrastructure.Messaging;

/// <summary>
/// Publica os pedidos de pagamento no Kafka. É singleton: o producer do Kafka mantém conexões abertas com o broker e
/// foi feito para ser compartilhado pela aplicação inteira.
/// </summary>
public sealed class KafkaPaymentRequestPublisher : IPaymentRequestPublisher, IDisposable
{
    public const string UnavailableMessage = "O Kafka não confirmou a publicação do pedido de pagamento.";

    private readonly IProducer<string, string> producer;
    private readonly string topic;
    private readonly CorrelationIdAccessor correlationIdAccessor;
    private readonly ILogger<KafkaPaymentRequestPublisher> logger;

    public KafkaPaymentRequestPublisher(
        IOptions<KafkaOptions> options,
        CorrelationIdAccessor correlationIdAccessor,
        ILogger<KafkaPaymentRequestPublisher> logger)
    {
        this.correlationIdAccessor = correlationIdAccessor;
        this.logger = logger;
        topic = options.Value.PaymentRequestsTopic;

        var config = new ProducerConfig
        {
            BootstrapServers = options.Value.BootstrapServers,
            ClientId = Environment.MachineName, // aparece nos logs do broker: dá para saber qual Pod publicou

            // Só considera publicado quando o broker gravou a mensagem; o idempotence evita duplicatas nas novas
            // tentativas internas do próprio cliente.
            Acks = Acks.All,
            EnableIdempotence = true,

            // Com o Kafka fora, o padrão (5 min) deixaria a confirmação da ordem travada. Em 5 s a publicação desiste,
            // a ordem fica em Confirmed e o worker de reconciliação publica de novo depois.
            MessageTimeoutMs = 5000,
        };

        producer = new ProducerBuilder<string, string>(config)
            // Sem isto, o librdkafka escreve os avisos dele direto no console, fora do JSON dos logs.
            .SetLogHandler((_, message) => logger.Log(
                KafkaMessages.ToLogLevel(message.Level), "Kafka ({Facility}): {Message}", message.Facility, message.Message))
            .SetErrorHandler((_, error) => logger.LogWarning("Kafka (producer): {Reason}", error.Reason))
            .Build();
    }

    public async Task PublishAsync(PaymentRequestedMessage message, CancellationToken cancellationToken)
    {
        var kafkaMessage = new Message<string, string>
        {
            // Mesma chave → mesma partição: as mensagens de uma ordem ficam sempre na mesma fila, em ordem.
            Key = message.OrderId.ToString(),
            Value = JsonSerializer.Serialize(message, KafkaMessages.JsonOptions),
            Headers = [],
        };

        if (correlationIdAccessor.CorrelationId is { } correlationId)
        {
            kafkaMessage.Headers.Add(KafkaMessages.CorrelationIdHeader, Encoding.UTF8.GetBytes(correlationId));
        }

        try
        {
            var result = await producer.ProduceAsync(topic, kafkaMessage, cancellationToken);
            logger.LogInformation(
                "Pedido de pagamento da ordem {OrderId} gravado em {Topic} (partição {Partition}, offset {Offset})",
                message.OrderId,
                result.Topic,
                result.Partition.Value,
                result.Offset.Value);
        }
        catch (KafkaException ex)
        {
            throw new ServiceUnavailableException(UnavailableMessage, ex);
        }
    }

    public void Dispose()
    {
        // Entrega o que ainda estiver no buffer antes de encerrar (o Pod recebe SIGTERM e tem alguns segundos).
        producer.Flush(TimeSpan.FromSeconds(5));
        producer.Dispose();
    }
}
