using System.ComponentModel.DataAnnotations;

namespace MiniCambio.Orders.Api.Application;

/// <summary>Seção <c>Orders</c> da configuração (ex.: <c>Orders__ReconciliationIntervalSeconds</c>).</summary>
public sealed class OrdersOptions
{
    public const string SectionName = "Orders";

    /// <summary>Intervalo, em segundos, entre dois ciclos do worker de reconciliação.</summary>
    [Range(1, 3600)]
    public int ReconciliationIntervalSeconds { get; set; } = 5;
}
