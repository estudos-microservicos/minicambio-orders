# minicambio-orders

Serviço **Orders** do MiniCâmbio, um sistema de câmbio simplificado usado como projeto de estudo.
Cria ordens a partir de cotações do **Rates**, permite confirmá-las ou cancelá-las e acompanha o pagamento no
**Payments**. É o único serviço chamado pelo frontend (`minicambio-web`, via `/api` no nginx).

## Fluxo

1. `POST /api/v1/orders` pede uma cotação ao Rates (`POST /api/v1/quotes`) e grava a ordem em `Quoted`, com a
   taxa, o valor em BRL e o prazo da cotação (60s). **Não** solicita pagamento.
2. `POST /api/v1/orders/{id}/confirm` confirma a ordem e solicita o pagamento ao Payments
   (`POST /api/v1/payments`). Se o Payments estiver fora, a ordem fica em `Confirmed` e o worker tenta de novo.
3. `DELETE /api/v1/orders/{id}` cancela (logicamente) uma ordem ainda em `Quoted`.
4. O worker de reconciliação acompanha o pagamento (`GET /api/v1/payments/{id}`) até `Completed` ou `Failed`.

## Máquina de estados

```
            ┌──► Cancelled            (DELETE, só a partir de Quoted)
            │
Quoted ─────┼──► Expired              (prazo da cotação venceu sem confirmação)
            │
            └──► Confirmed ──► PaymentRequested ──► Completed
                                               └──► Failed
```

- Estados finais: `Cancelled`, `Expired`, `Completed`, `Failed`. Nada é apagado fisicamente.
- As transições são métodos da entidade `Order` (`Confirm`, `Cancel`, `MarkExpired`, `MarkPaymentRequested`,
  `MarkCompleted`, `MarkFailed`); qualquer transição fora do diagrama vira **422** com uma mensagem em português.
- A resposta traz `canConfirm` (status `Quoted` e cotação ainda válida) e `canCancel` (status `Quoted`), para o
  frontend não duplicar a regra.

## Endpoints

| Método | Rota | Resposta |
|---|---|---|
| POST | `/api/v1/orders` | header `Idempotency-Key` obrigatório (1 a 100 caracteres); body `{ currency, side, foreignAmount, beneficiary: { name, country, accountNumber } }` → **201** + `Location` com a ordem em `Quoted`; **200** com a ordem existente se a chave já foi usada; 400 validação (inclusive header ausente, em `errors["Idempotency-Key"]`); 422 se o Rates recusar (repassa o `detail` do Rates, ex.: moeda não suportada); 503 se o Rates estiver fora (nada é gravado) |
| POST | `/api/v1/orders/{id}/confirm` | **200** com a ordem (`PaymentRequested`, ou `Confirmed` se o Payments estiver fora); repetir devolve 200 sem mudar nada; 404; 422 se a cotação expirou (a ordem passa a `Expired`), se foi cancelada etc. |
| DELETE | `/api/v1/orders/{id}` | **204** (também se já estava cancelada); 404; 422 se não estiver em `Quoted` |
| GET | `/api/v1/orders/{id}` | 200 ou 404 |
| GET | `/api/v1/orders?status=&page=&pageSize=` | 200 `{ items, page, pageSize, totalCount, servedBy }`, ordenado por `createdAt` desc |
| GET | `/health/live` | 200 se o processo responde (sem dependências) |
| GET | `/health/ready` | 200 se o banco responde (Rates e Payments ficam de fora de propósito) |

Listagem: `status` é opcional e case-insensitive (vazio = todos); `page` padrão 1 (mínimo 1); `pageSize` padrão 20
(de 1 a 100). Fora disso → 400 com `errors` por campo; valor não numérico em `page`/`pageSize` → 400
"Requisição inválida". Página além do fim devolve `items` vazio com o `totalCount` real.

Ordem (`OrderResponse`): `id, currency, side, foreignAmount, quoteId, rate, brlAmount, quoteExpiresAt,
beneficiary { name, country, accountNumber }, status, paymentId, failureReason, confirmedAt, cancelledAt, createdAt,
updatedAt, canConfirm, canCancel, servedBy`. `failureReason` vem do Payments (`BENEFICIARY_REJECTED`,
`COMPLIANCE_BLOCKED`, `LIMIT_EXCEEDED`).

Erros seguem ProblemDetails (`title` e `detail` em português, `correlationId`; 400 traz `errors` por campo em
camelCase, como `beneficiary.name`). Todas as respostas devolvem o header `X-Correlation-Id` (o recebido ou um novo),
que também é repassado nas chamadas ao Rates e ao Payments. Em `Development`, a documentação fica em `/scalar`
(OpenAPI em `/openapi/v1.json`).

### Mensagens de 422

| Situação | `detail` |
|---|---|
| Confirmar com a cotação vencida (ou ordem já `Expired`) | A cotação expirou. Crie uma nova ordem. |
| Confirmar ordem cancelada | Esta ordem foi cancelada e não pode mais ser confirmada. |
| Confirmar ordem com pagamento recusado | O pagamento desta ordem falhou. Crie uma nova ordem. |
| Cancelar ordem fora de `Quoted` | Só é possível cancelar ordens que ainda não foram confirmadas. |
| Rates recusou a cotação | mensagem do próprio Rates (ex.: A moeda XYZ não é suportada.) |

## Idempotência e resiliência

- **Criação**: a mesma `Idempotency-Key` devolve a ordem já criada. Duas requisições simultâneas com a mesma chave
  são resolvidas pelo índice único em `idempotency_key` (a violação `23505` devolve a ordem que gravou primeiro).
- **Confirmação** e **cancelamento** repetidos devolvem sucesso sem mudar nada.
- **Clientes HTTP** (`RatesClient`, `PaymentsClient`): `AddStandardResilienceHandler()` com retry exponencial
  (3 novas tentativas, espera inicial de 500ms), timeout de 5s por tentativa e 15s no total, e circuit breaker.
  Falha de rede, timeout, circuito aberto ou 5xx depois das tentativas = serviço indisponível.

## Worker de reconciliação

`OrderReconciliationWorker` roda a cada `Orders__ReconciliationIntervalSeconds` (padrão 5s). Cada ciclo
(`OrderReconciliationCycle`, com um correlation id próprio) trava lotes de até 20 ordens com
`SELECT ... FOR UPDATE SKIP LOCKED` dentro de uma transação:

1. `Quoted` com a cotação vencida → `Expired`;
2. `Confirmed` → solicita o pagamento (seguro porque o Payments é idempotente por `orderId`) → `PaymentRequested`;
3. `PaymentRequested` → consulta o pagamento: `Completed` → `Completed`; `Failed` → `Failed` com o `failureReason`
   do Payments; `Pending`/`Processing` → nada.

O `SKIP LOCKED` permite várias réplicas no Kubernetes sem processar a mesma ordem duas vezes. Erro em uma ordem é
logado e não derruba o worker. Esse polling será substituído por eventos (mensageria) numa etapa futura.

## Variáveis de ambiente

| Variável | Padrão | Descrição |
|---|---|---|
| `ConnectionStrings__Default` | — (obrigatória) | Connection string do Postgres |
| `Database__ApplyMigrationsOnStartup` | `false` | Aplica as migrations ao subir (`true` no compose; no Kubernetes vira Job/init container) |
| `Services__RatesApi` | — (obrigatória) | URL base do Rates (ex.: `http://rates-api:8080`) |
| `Services__PaymentsApi` | — (obrigatória) | URL base do Payments (ex.: `http://payments-api:8080`) |
| `Orders__ReconciliationIntervalSeconds` | `5` | Intervalo do worker de reconciliação, em segundos |

As URLs e o intervalo são validados na inicialização: a API não sobe com configuração inválida. Em `Development`,
o `appsettings.Development.json` já aponta para o Postgres do `compose.dev.yml`, liga as migrations e usa
`http://localhost:5001` (Rates) e `http://localhost:5002` (Payments).

## Requisitos

- .NET SDK 10
- Docker Desktop (Postgres local e testes de integração com Testcontainers)

## Rodar localmente

```powershell
docker compose -f compose.dev.yml up -d           # só o Postgres, na porta 5435
dotnet run --project src/MiniCambio.Orders.Api    # http://localhost:5000 (Scalar em /scalar)
```

O Rates (porta 5001) e o Payments (porta 5002) precisam estar rodando para criar e confirmar ordens. Sem o Rates a
criação devolve 503; sem o Payments a confirmação deixa a ordem em `Confirmed` até ele voltar.

## Testes

```powershell
dotnet test MiniCambio.Orders.slnx
```

- `tests/MiniCambio.Orders.UnitTests`: toda a máquina de estados (transições válidas e inválidas, `CanBeConfirmed`
  e `CanBeCancelled`), os handlers com fakes (Rates fora/recusando, Payments fora, idempotência de criação,
  confirmação e cancelamento), os validadores, os clientes HTTP (mapeamento de status e falhas) e o handler de
  correlation id.
- `tests/MiniCambio.Orders.IntegrationTests`: endpoints contra um Postgres real (Testcontainers), com Rates e
  Payments falsos e sem o worker hospedado; o ciclo de reconciliação é chamado diretamente, com `FakeTimeProvider`
  e um banco próprio por teste.

## Migrations

```powershell
dotnet tool exec dotnet-ef@10.0.14 --yes -- migrations add <Nome> --project src/MiniCambio.Orders.Api --output-dir Infrastructure/Persistence/Migrations
```

## Docker

```powershell
docker build -t minicambio-orders-api .
```

A imagem roda como usuário não-root e escuta na porta `8080`. Para subir o sistema inteiro (bancos, APIs e
frontend), use o `docker-compose.yml` do repositório `minicambio-infra`.
