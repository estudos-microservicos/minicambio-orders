using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using MiniCambio.Orders.Api.Application.Orders;

namespace MiniCambio.Orders.Api.Endpoints;

public static class OrdersEndpoints
{
    public static IEndpointRouteBuilder MapOrdersEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/orders").WithTags("Orders");

        group.MapPost("/", async Task<Results<Created<OrderResponse>, Ok<OrderResponse>>> (
                [FromHeader(Name = IdempotencyKeyFilter.HeaderName)] string? idempotencyKey,
                CreateOrderRequest request,
                CreateOrderHandler handler,
                CancellationToken cancellationToken) =>
            {
                // O IdempotencyKeyFilter já garantiu que o header veio preenchido.
                var result = await handler.HandleAsync(idempotencyKey!, request, cancellationToken);
                return result.Created
                    ? TypedResults.Created($"/api/v1/orders/{result.Order.Id}", result.Order)
                    : TypedResults.Ok(result.Order);
            })
            .AddEndpointFilter<IdempotencyKeyFilter>()
            .AddEndpointFilter<ValidationFilter<CreateOrderRequest>>()
            .WithName("CreateOrder")
            .WithSummary("Cota no Rates e cria a ordem em Quoted (201); repetir o Idempotency-Key devolve a mesma ordem (200)")
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        group.MapPost("/{id:guid}/confirm", async (Guid id, ConfirmOrderHandler handler, CancellationToken cancellationToken) =>
                TypedResults.Ok(await handler.HandleAsync(id, cancellationToken)))
            .WithName("ConfirmOrder")
            .WithSummary("Confirma a ordem e solicita o pagamento (idempotente)")
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        group.MapDelete("/{id:guid}", async (Guid id, CancelOrderHandler handler, CancellationToken cancellationToken) =>
            {
                await handler.HandleAsync(id, cancellationToken);
                return TypedResults.NoContent();
            })
            .WithName("CancelOrder")
            .WithSummary("Cancela (logicamente) uma ordem ainda não confirmada (idempotente)")
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        group.MapGet("/{id:guid}", async (Guid id, GetOrderHandler handler, CancellationToken cancellationToken) =>
                TypedResults.Ok(await handler.HandleAsync(id, cancellationToken)))
            .WithName("GetOrder")
            .WithSummary("Consulta uma ordem")
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapGet("/", async ([AsParameters] ListOrdersQuery query, ListOrdersHandler handler, CancellationToken cancellationToken) =>
                TypedResults.Ok(await handler.HandleAsync(query, cancellationToken)))
            .AddEndpointFilter<ValidationFilter<ListOrdersQuery>>()
            .WithName("ListOrders")
            .WithSummary("Lista as ordens (mais recentes primeiro), com filtro opcional por status e paginação")
            .ProducesValidationProblem();

        return app;
    }
}
