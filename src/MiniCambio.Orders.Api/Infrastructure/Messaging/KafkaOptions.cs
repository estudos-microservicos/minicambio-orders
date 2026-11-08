using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using System.Text.Json.Serialization;
using Confluent.Kafka;

namespace MiniCambio.Orders.Api.Infrastructure.Messaging;

/// <summary>Seção <c>Kafka</c>: endereço do broker, tópicos e grupo de consumo do Orders.</summary>
public sealed class KafkaOptions
{
    public const string SectionName = "Kafka";

    /// <summary>Endereço do broker (<c>Kafka__BootstrapServers</c>, ex.: <c>kafka:9092</c>).</summary>
    [Required(ErrorMessage = "Configure o endereço do Kafka em Kafka__BootstrapServers (ex.: kafka:9092).")]
    public string BootstrapServers { get; set; } = string.Empty;

    /// <summary>Tópico em que o Orders publica os pedidos de pagamento.</summary>
    [Required]
    public string PaymentRequestsTopic { get; set; } = "minicambio.payment-requests";

    /// <summary>Tópico em que o Payments publica os resultados, consumido pelo Orders.</summary>
    [Required]
    public string PaymentResultsTopic { get; set; } = "minicambio.payment-results";

    /// <summary>
    /// Grupo de consumo. Todas as réplicas do Orders usam o mesmo grupo, e o Kafka divide as partições do tópico
    /// entre elas: cada mensagem é tratada por uma réplica só.
    /// </summary>
    [Required]
    public string ConsumerGroupId { get; set; } = "orders-api";
}

/// <summary>Formato das mensagens: JSON camelCase com enums como texto, igual ao das APIs.</summary>
internal static class KafkaMessages
{
    /// <summary>Header com o correlation id, o equivalente ao <c>X-Correlation-Id</c> do HTTP.</summary>
    public const string CorrelationIdHeader = "X-Correlation-Id";

    /// <summary>Nível do log interno do librdkafka (syslog) no nível equivalente do <see cref="ILogger"/>.</summary>
    public static LogLevel ToLogLevel(SyslogLevel level) => level switch
    {
        SyslogLevel.Emergency or SyslogLevel.Alert or SyslogLevel.Critical or SyslogLevel.Error => LogLevel.Error,
        SyslogLevel.Warning => LogLevel.Warning,
        SyslogLevel.Notice or SyslogLevel.Info => LogLevel.Information,
        _ => LogLevel.Debug,
    };

    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };
}
