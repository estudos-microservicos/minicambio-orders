using MiniCambio.Orders.Api.Domain;

namespace MiniCambio.Orders.Api.Application;

public interface IOrderRepository
{
    /// <summary>Carrega a ordem para alteração (as mudanças são gravadas em <see cref="SaveChangesAsync"/>).</summary>
    Task<Order?> GetAsync(Guid id, CancellationToken cancellationToken);

    Task<Order?> GetByIdempotencyKeyAsync(string idempotencyKey, CancellationToken cancellationToken);

    /// <summary>
    /// Insere a ordem. Lança <see cref="DuplicateIdempotencyKeyException"/> quando outra requisição já gravou uma
    /// ordem com o mesmo <c>Idempotency-Key</c> (violação do índice único).
    /// </summary>
    Task AddAsync(Order order, CancellationToken cancellationToken);

    Task SaveChangesAsync(CancellationToken cancellationToken);

    /// <summary>Lista paginada, da mais recente para a mais antiga (<c>created_at</c> desc).</summary>
    Task<PagedResult<Order>> ListAsync(OrderStatus? status, int page, int pageSize, CancellationToken cancellationToken);
}

public sealed record PagedResult<T>(IReadOnlyList<T> Items, int TotalCount);

/// <summary>Corrida entre duas criações com o mesmo <c>Idempotency-Key</c>: a outra gravou primeiro.</summary>
public sealed class DuplicateIdempotencyKeyException(string idempotencyKey, Exception? innerException = null)
    : Exception($"Já existe uma ordem com o Idempotency-Key '{idempotencyKey}'.", innerException)
{
    public string IdempotencyKey { get; } = idempotencyKey;
}
