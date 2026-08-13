using LuckyWheel.Api.Errors;
using LuckyWheel.Api.Middleware;
using LuckyWheel.Application;
using LuckyWheel.Infrastructure;
using LuckyWheel.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc;
using Microsoft.OpenApi.Models;

var builder = WebApplication.CreateBuilder(args);

// ── MVC / JSON ────────────────────────────────────────────────────────────────
builder.Services.AddControllers()
    .AddJsonOptions(options =>
    {
        // Ensure camelCase serialization for all API responses
        options.JsonSerializerOptions.PropertyNamingPolicy =
            System.Text.Json.JsonNamingPolicy.CamelCase;
    });

// ── Problem Details (RFC 7807) ─────────────────────────────────────────────────
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<GlobalExceptionHandler>();

// ── Application & Infrastructure layers ─────────────────────────────────────
builder.Services.AddApplication();
builder.Services.AddInfrastructure(builder.Configuration);

// ── Swagger / OpenAPI ─────────────────────────────────────────────────────────
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "Lucky Wheel API",
        Version = "v1",
        Description = "Backend API for the Lucky Wheel reward system"
    });
});

// ── Health Checks ─────────────────────────────────────────────────────────────
builder.Services.AddHealthChecks()
    .AddDbContextCheck<ApplicationDbContext>();

var app = builder.Build();

// ── Middleware pipeline (order matters) ────────────────────────────────────────

// 1. Exception handler must be first so it catches errors from all subsequent middleware
app.UseExceptionHandler();

// 2. Correlation id — sets X-Correlation-ID on every request/response
app.UseMiddleware<CorrelationIdMiddleware>();

// 3. HTTPS redirect
app.UseHttpsRedirection();

// 4. Swagger (Development only)
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI(options =>
    {
        options.SwaggerEndpoint("/swagger/v1/swagger.json", "Lucky Wheel API v1");
        options.RoutePrefix = "swagger";
    });
}

// 5. Authorization
app.UseAuthorization();

// 6. Controllers
app.MapControllers();

// 7. Health check
app.MapHealthChecks("/health");

app.Run();

// Make Program class accessible to WebApplicationFactory in integration tests
public partial class Program { }
