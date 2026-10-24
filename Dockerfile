# ---------- build ----------
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore numa camada própria: só é refeito quando props, nuget.config ou o .csproj mudam.
COPY nuget.config Directory.Build.props Directory.Packages.props ./
COPY src/MiniCambio.Orders.Api/MiniCambio.Orders.Api.csproj src/MiniCambio.Orders.Api/
RUN dotnet restore src/MiniCambio.Orders.Api/MiniCambio.Orders.Api.csproj

# Código-fonte e publish apenas da API (os testes não rodam dentro da imagem).
COPY src/ src/
RUN dotnet publish src/MiniCambio.Orders.Api/MiniCambio.Orders.Api.csproj -c Release --no-restore -o /app/publish -p:UseAppHost=false

# ---------- runtime ----------
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .

# Usuário não-root já existente na imagem base (APP_UID=1654). A imagem escuta em 8080 por padrão.
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "MiniCambio.Orders.Api.dll"]
