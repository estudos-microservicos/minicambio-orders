using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using MiniCambio.Orders.Api.Application.Orders;
using MiniCambio.Orders.Api.Domain;

namespace MiniCambio.Orders.Api.Infrastructure.Persistence.Configurations;

internal sealed class OrderConfiguration : IEntityTypeConfiguration<Order>
{
    /// <summary>Nome fixo para que o repositório reconheça a violação (23505) deste índice em específico.</summary>
    public const string IdempotencyKeyIndexName = "ix_orders_idempotency_key";

    public void Configure(EntityTypeBuilder<Order> builder)
    {
        builder.ToTable("orders");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedNever();

        builder.Property(x => x.IdempotencyKey).HasMaxLength(Order.MaxIdempotencyKeyLength);
        builder.Property(x => x.Currency).HasMaxLength(3);
        builder.Property(x => x.Side).HasConversion<string>().HasMaxLength(4);
        builder.Property(x => x.ForeignAmount).HasPrecision(18, 2);
        builder.Property(x => x.Rate).HasPrecision(18, 6);
        builder.Property(x => x.BrlAmount).HasPrecision(18, 2);
        builder.Property(x => x.BeneficiaryName).HasMaxLength(CreateOrderRequestValidator.MaxBeneficiaryNameLength);
        builder.Property(x => x.BeneficiaryCountry).HasMaxLength(2);
        builder.Property(x => x.BeneficiaryAccount).HasMaxLength(CreateOrderRequestValidator.MaxAccountNumberLength);
        builder.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
        builder.Property(x => x.FailureReason).HasMaxLength(100);

        // Garante uma única ordem por chave mesmo com duas requisições simultâneas.
        builder.HasIndex(x => x.IdempotencyKey).IsUnique().HasDatabaseName(IdempotencyKeyIndexName);

        // Listagem (filtro por status, ordenação por data) e consultas do worker de reconciliação.
        builder.HasIndex(x => x.Status);
        builder.HasIndex(x => x.CreatedAt);
    }
}
