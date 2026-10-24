# minicambio-orders

Serviço **Orders** do MiniCâmbio, um sistema de câmbio simplificado usado como projeto de estudo.
Cria ordens a partir de cotações, permite confirmá-las ou cancelá-las e acompanha o pagamento.

> Repositório em construção (Fase 0: estrutura). Endpoints, variáveis de ambiente e instruções completas
> entram nas próximas fases.

## Requisitos

- .NET SDK 10
- Docker Desktop (testes de integração usam Testcontainers)

## Build e testes

```powershell
dotnet build MiniCambio.Orders.slnx
dotnet test MiniCambio.Orders.slnx
```

## Rodar localmente

```powershell
dotnet run --project src/MiniCambio.Orders.Api
```

A API sobe em `http://localhost:5000`.

## Docker

```powershell
docker build -t minicambio-orders-api .
```

A imagem roda como usuário não-root e escuta na porta `8080`. Para subir o sistema inteiro (bancos, APIs e
frontend), use o `docker-compose.yml` do repositório `minicambio-infra`.
