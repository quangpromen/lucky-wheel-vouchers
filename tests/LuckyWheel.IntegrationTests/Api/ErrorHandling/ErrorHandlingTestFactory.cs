using System;
using LuckyWheel.Application.Common.Exceptions;
using LuckyWheel.Domain.Common;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace LuckyWheel.IntegrationTests.Api.ErrorHandling;

/// <summary>
/// Custom <see cref="WebApplicationFactory{TEntryPoint}"/> that:
/// <list type="bullet">
///   <item>Replaces the DB health check with a simple in-memory check so no SQL Server is needed.</item>
///   <item>Overrides the middleware pipeline, keeping ExceptionHandler + CorrelationIdMiddleware active.</item>
///   <item>Registers test-only trigger endpoints — only visible in the test host, never in Production.</item>
/// </list>
/// </summary>
public sealed class ErrorHandlingTestFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");

        builder.ConfigureServices(services =>
        {
            // Remove ALL existing health check registrations (including EF Core DB check)
            // so tests don't need SQL Server. The HealthCheckService itself stays registered.
            services.Configure<HealthCheckServiceOptions>(options =>
                options.Registrations.Clear());
        });

        builder.Configure(app =>
        {
            // ── Real middleware from the production pipeline ────────────────
            app.UseExceptionHandler();
            app.UseMiddleware<LuckyWheel.Api.Middleware.CorrelationIdMiddleware>();

            // ── Routing ────────────────────────────────────────────────────
            app.UseRouting();
            app.UseAuthorization();

            // ── Test-only trigger endpoints ────────────────────────────────
            // None of these routes are reachable in Production.
            app.UseEndpoints(endpoints =>
            {
                // Trigger ValidationException
                endpoints.MapGet("/test-errors/validation", _ =>
                    throw new ValidationException("email", "Email is required."));

                // Trigger NotFoundException
                endpoints.MapGet("/test-errors/not-found", _ =>
                    throw new NotFoundException("Wheel", Guid.NewGuid()));

                // Trigger ConflictException
                endpoints.MapGet("/test-errors/conflict", _ =>
                    throw new ConflictException("A wheel with this slug already exists."));

                // Trigger ForbiddenException
                endpoints.MapGet("/test-errors/forbidden", _ =>
                    throw new ForbiddenException());

                // Trigger BusinessRuleViolationException
                endpoints.MapGet("/test-errors/business-rule", _ =>
                    throw new BusinessRuleViolationException("WHEEL_NOT_ACTIVE", "The wheel is not active."));

                // Trigger DomainException (from Domain layer, Phase 2)
                endpoints.MapGet("/test-errors/domain-exception", _ =>
                    throw new DomainException("SPIN_LIMIT_EXCEEDED", "Spin limit exceeded for this user."));

                // Trigger generic unhandled exception (message contains "sensitive" data to verify it's not leaked)
                endpoints.MapGet("/test-errors/unhandled", _ =>
                    throw new InvalidOperationException("Unexpected internal error with sensitive data: password=secret123"));

                // Simple success endpoint (for correlation id checks)
                endpoints.MapGet("/test-errors/ok", async ctx =>
                {
                    ctx.Response.StatusCode = StatusCodes.Status200OK;
                    await ctx.Response.WriteAsync("ok");
                });

                // Health check — no DB dependency in test environment
                endpoints.MapHealthChecks("/health");
            });
        });
    }
}
