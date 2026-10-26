using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Http.Json;
using MiniCambio.Orders.Api.Application;
using MiniCambio.Orders.Api.Endpoints;
using MiniCambio.Orders.Api.Infrastructure;
using MiniCambio.Orders.Api.Infrastructure.Persistence;
using MiniCambio.Orders.Api.Infrastructure.Web;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

// Logs estruturados em JSON no console (sem Serilog).
builder.Logging.ClearProviders();
builder.Logging.AddJsonConsole(options =>
{
    options.IncludeScopes = true;
    options.UseUtcTimestamp = true;
    options.TimestampFormat = "yyyy-MM-ddTHH:mm:ss.fffZ";
});

builder.Services.Configure<JsonOptions>(options =>
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

// JSON malformado ou query string inválida viram BadHttpRequestException, tratada pelo GlobalExceptionHandler
// (400 com ProblemDetails).
builder.Services.Configure<RouteHandlerOptions>(options => options.ThrowOnBadRequest = true);

builder.Services.AddProblemDetails(options => options.CustomizeProblemDetails = context =>
{
    var correlationId = CorrelationIdMiddleware.GetCorrelationId(context.HttpContext);
    if (correlationId is not null)
    {
        context.ProblemDetails.Extensions["correlationId"] = correlationId;
    }
});
builder.Services.AddExceptionHandler<GlobalExceptionHandler>();
builder.Services.AddOpenApi();

builder.Services.AddApplication();
builder.Services.AddInfrastructure();

var app = builder.Build();

await app.ApplyMigrationsIfConfiguredAsync();

app.UseMiddleware<CorrelationIdMiddleware>();
app.UseExceptionHandler();
app.UseStatusCodePages();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.MapScalarApiReference();
}

app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
app.MapHealthChecks("/health/ready", new HealthCheckOptions
{
    Predicate = check => check.Tags.Contains(InfrastructureServiceCollectionExtensions.ReadyTag),
});

app.MapOrdersEndpoints();

await app.RunAsync();
