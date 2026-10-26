using Microsoft.EntityFrameworkCore;
using MiniCambio.Orders.Api.Application;
using MiniCambio.Orders.Api.Domain;
using MiniCambio.Orders.Api.Infrastructure.Persistence.Configurations;
using Npgsql;

namespace MiniCambio.Orders.Api.Infrastructure.Persistence;

internal sealed class OrderRepository(OrdersDbContext db) : IOrderRepository
{
    public Task<Order?> GetAsync(Guid id, CancellationToken cancellationToken) =>
        db.Orders.FirstOrDefaultAsync(x => x.Id == id, cancellationToken);

    public Task<Order?> GetByIdempotencyKeyAsync(string idempotencyKey, CancellationToken cancellationToken) =>
        db.Orders.AsNoTracking().FirstOrDefaultAsync(x => x.IdempotencyKey == idempotencyKey, cancellationToken);

    public async Task AddAsync(Order order, CancellationToken cancellationToken)
    {
        db.Orders.Add(order);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.UniqueViolation,
            ConstraintName: OrderConfiguration.IdempotencyKeyIndexName,
        })
        {
            // Tira a ordem do change tracker para que um SaveChanges posterior não tente inseri-la de novo.
            db.Entry(order).State = EntityState.Detached;
            throw new DuplicateIdempotencyKeyException(order.IdempotencyKey, ex);
        }
    }

    public Task SaveChangesAsync(CancellationToken cancellationToken) => db.SaveChangesAsync(cancellationToken);

    public async Task<PagedResult<Order>> ListAsync(
        OrderStatus? status,
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        var query = db.Orders.AsNoTracking();
        if (status is { } filter)
        {
            query = query.Where(x => x.Status == filter);
        }

        var totalCount = await query.CountAsync(cancellationToken);

        // Página além do fim: não há o que buscar (e o offset em long evita overflow com page muito grande).
        var offset = (long)(page - 1) * pageSize;
        if (offset >= totalCount)
        {
            return new PagedResult<Order>([], totalCount);
        }

        // O id (UUID v7, ordenado pelo tempo) desempata ordens criadas no mesmo instante.
        var items = await query
            .OrderByDescending(x => x.CreatedAt)
            .ThenByDescending(x => x.Id)
            .Skip((int)offset)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        return new PagedResult<Order>(items, totalCount);
    }
}
