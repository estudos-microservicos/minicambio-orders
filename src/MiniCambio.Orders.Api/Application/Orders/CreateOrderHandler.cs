using MiniCambio.Orders.Api.Application.Rates;
using MiniCambio.Orders.Api.Domain;

namespace MiniCambio.Orders.Api.Application.Orders;

/// <param name="Order">A ordem criada ou a já existente para a mesma chave.</param>
/// <param name="Created"><c>true</c> se a ordem foi criada agora (201); <c>false</c> se é um replay (200).</param>
public sealed record CreateOrderResult(OrderResponse Order, bool Created);

public sealed class CreateOrderHandler(
    IOrderRepository orders,
    IRatesClient ratesClient,
    TimeProvider timeProvider,
    ILogger<CreateOrderHandler> logger)
{
    /// <summary>
    /// Cota no Rates e cria a ordem em <see cref="OrderStatus.Quoted"/>. Não solicita pagamento.
    /// Espera um request já validado e um <paramref name="idempotencyKey"/> não vazio.
    /// </summary>
    public async Task<CreateOrderResult> HandleAsync(
        string idempotencyKey,
        CreateOrderRequest request,
        CancellationToken cancellationToken)
    {
        // 1. Mesma chave já usada: devolve a ordem existente (o cliente repetiu o envio).
        var existing = await orders.GetByIdempotencyKeyAsync(idempotencyKey, cancellationToken);
        if (existing is not null)
        {
            logger.LogInformation("Idempotency-Key repetido; devolvendo a ordem {OrderId}", existing.Id);
            return new CreateOrderResult(OrderResponse.From(existing, timeProvider.GetUtcNow()), Created: false);
        }

        var currency = request.Currency!;
        var side = Enum.Parse<OrderSide>(request.Side!, ignoreCase: true);
        var foreignAmount = request.ForeignAmount!.Value;
        var beneficiary = request.Beneficiary!;

        // 2. Cotação no Rates. Indisponível -> ServiceUnavailableException (503); recusada -> QuoteRejectedException
        //    (422). Em ambos os casos nada é gravado.
        var quote = await ratesClient.CreateQuoteAsync(new CreateQuoteRequest(currency, side, foreignAmount), cancellationToken);

        // 3. Grava a ordem em Quoted com os dados da cotação.
        var now = timeProvider.GetUtcNow();
        var order = Order.CreateQuoted(
            idempotencyKey,
            currency,
            side,
            foreignAmount,
            new OrderQuote(quote.Id, quote.Rate, quote.BrlAmount, quote.ExpiresAt),
            new Beneficiary(beneficiary.Name!, beneficiary.Country!, beneficiary.AccountNumber!),
            now);

        try
        {
            await orders.AddAsync(order, cancellationToken);
        }
        catch (DuplicateIdempotencyKeyException)
        {
            // Corrida: outra requisição com a mesma chave gravou entre a consulta do passo 1 e o insert.
            // O índice único garante uma única ordem; devolvemos a que venceu.
            var winner = await orders.GetByIdempotencyKeyAsync(idempotencyKey, cancellationToken)
                ?? throw new InvalidOperationException("Violação do Idempotency-Key sem ordem correspondente.");
            logger.LogInformation("Corrida de Idempotency-Key resolvida; devolvendo a ordem {OrderId}", winner.Id);
            return new CreateOrderResult(OrderResponse.From(winner, timeProvider.GetUtcNow()), Created: false);
        }

        logger.LogInformation(
            "Ordem {OrderId} criada em {Status} com a cotação {QuoteId}", order.Id, order.Status, order.QuoteId);

        // 4. 201, sem solicitar pagamento (isso só acontece na confirmação).
        return new CreateOrderResult(OrderResponse.From(order, now), Created: true);
    }
}
