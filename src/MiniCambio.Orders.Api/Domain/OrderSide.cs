namespace MiniCambio.Orders.Api.Domain;

/// <summary>Lado da operação, sempre do ponto de vista do cliente.</summary>
public enum OrderSide
{
    /// <summary>Cliente compra moeda estrangeira (paga em BRL).</summary>
    Buy,

    /// <summary>Cliente vende moeda estrangeira (recebe em BRL).</summary>
    Sell,
}
