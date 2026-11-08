namespace MiniCambio.Orders.Api.Domain;

/// <summary>
/// Status da ordem. Transições permitidas:
/// <code>
///             ┌──► Cancelled            (DELETE, só a partir de Quoted)
///             │
/// Quoted ─────┼──► Expired              (prazo da cotação venceu sem confirmação)
///             │
///             └──► Confirmed ──► PaymentRequested ──► Completed
///                                                └──► Failed
/// </code>
/// Estados finais: <see cref="Cancelled"/>, <see cref="Expired"/>, <see cref="Completed"/> e <see cref="Failed"/>.
/// </summary>
public enum OrderStatus
{
    /// <summary>Cotada, aguardando confirmação ou cancelamento dentro do prazo da cotação.</summary>
    Quoted,

    /// <summary>Confirmada pelo cliente; o pedido de pagamento ainda não foi publicado no Kafka.</summary>
    Confirmed,

    /// <summary>Pedido de pagamento publicado no Kafka, aguardando o resultado do Payments.</summary>
    PaymentRequested,

    /// <summary>Pagamento concluído (final).</summary>
    Completed,

    /// <summary>Pagamento recusado (final).</summary>
    Failed,

    /// <summary>Cancelada pelo cliente antes da confirmação (final).</summary>
    Cancelled,

    /// <summary>A cotação venceu sem confirmação (final).</summary>
    Expired,
}
