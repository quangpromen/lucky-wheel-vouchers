using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using LuckyWheel.Api.Middleware;
using LuckyWheel.Application.Common.Exceptions;
using LuckyWheel.Domain.Common;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LuckyWheel.Api.Errors;

/// <summary>
/// Global exception handler registered with <c>AddExceptionHandler&lt;T&gt;()</c>.
/// Maps known application exceptions to RFC 7807 ProblemDetails responses.
///
/// Error code mapping:
/// <list type="table">
///   <listheader><term>Exception Type</term><term>HTTP</term><term>errorCode</term></listheader>
///   <item><term>ValidationException</term><term>400</term><term>VALIDATION_ERROR</term></item>
///   <item><term>BusinessRuleViolationException</term><term>400</term><term>BUSINESS_RULE_VIOLATION</term></item>
///   <item><term>DomainException</term><term>400</term><term>BUSINESS_RULE_VIOLATION</term></item>
///   <item><term>NotFoundException</term><term>404</term><term>NOT_FOUND</term></item>
///   <item><term>ConflictException</term><term>409</term><term>CONFLICT</term></item>
///   <item><term>ForbiddenException</term><term>403</term><term>FORBIDDEN</term></item>
///   <item><term>DbUpdateConcurrencyException</term><term>409</term><term>CONFLICT</term></item>
///   <item><term>DbUpdateException</term><term>500</term><term>INTERNAL_SERVER_ERROR</term></item>
///   <item><term>Any other</term><term>500</term><term>INTERNAL_SERVER_ERROR</term></item>
/// </list>
///
/// Production: never exposes stack trace, SQL error message, inner exception, or connection strings.
/// Development: logs full exception details; stack trace is omitted from HTTP response body.
/// </summary>
public sealed class GlobalExceptionHandler : IExceptionHandler
{
    private readonly ILogger<GlobalExceptionHandler> _logger;
    private readonly IHostEnvironment _environment;

    public GlobalExceptionHandler(
        ILogger<GlobalExceptionHandler> logger,
        IHostEnvironment environment)
    {
        _logger = logger;
        _environment = environment;
    }

    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        var traceId = ResolveTraceId(httpContext);

        (int status, string errorCode, string title, string detail) = MapException(exception);

        // ── Logging ───────────────────────────────────────────────────────
        if (status >= 500)
        {
            // 5xx: log full exception with trace id — never log sensitive data
            _logger.LogError(exception,
                "Unhandled exception. TraceId={TraceId} Status={Status} ErrorCode={ErrorCode}",
                traceId, status, errorCode);
        }
        else
        {
            // 4xx: info-level, no stack trace needed
            _logger.LogInformation(exception,
                "Handled exception. TraceId={TraceId} Status={Status} ErrorCode={ErrorCode} Message={Message}",
                traceId, status, errorCode, exception.Message);
        }

        // ── Build ProblemDetails ──────────────────────────────────────────
        var problem = new ProblemDetails
        {
            Type = $"https://httpstatuses.com/{status}",
            Title = title,
            Status = status,
            Detail = detail,
            Instance = httpContext.Request.Path
        };

        problem.Extensions["traceId"] = traceId;
        problem.Extensions["errorCode"] = errorCode;

        // Validation errors: add field-level errors extension
        if (exception is ValidationException validationException)
        {
            problem.Extensions["errors"] = validationException.Errors;
        }

        // Business rule from Domain: add stable domain code
        if (exception is DomainException domainException)
        {
            problem.Extensions["ruleCode"] = domainException.Code;
        }

        // Business rule from Application
        if (exception is BusinessRuleViolationException bizEx)
        {
            problem.Extensions["ruleCode"] = bizEx.RuleCode;
        }

        httpContext.Response.StatusCode = status;

        // ExceptionHandlerMiddleware calls Response.Clear() before invoking IExceptionHandler,
        // which wipes all headers previously set (including X-Correlation-ID from CorrelationIdMiddleware).
        // We must re-set it here to ensure every error response carries the correlation id header.
        httpContext.Response.Headers[CorrelationIdMiddleware.CorrelationIdHeader] = traceId;

        await httpContext.Response.WriteAsJsonAsync(problem, cancellationToken: cancellationToken);

        return true;
    }

    // ── Private helpers ───────────────────────────────────────────────────

    private static string ResolveTraceId(HttpContext context)
    {
        // Prefer already-resolved correlation id set by CorrelationIdMiddleware
        if (context.Items.TryGetValue(CorrelationIdMiddleware.CorrelationIdItemKey, out var item)
            && item is string correlationId
            && !string.IsNullOrWhiteSpace(correlationId))
        {
            return correlationId;
        }

        return Activity.Current?.Id ?? context.TraceIdentifier;
    }

    private (int Status, string ErrorCode, string Title, string Detail) MapException(Exception exception)
    {
        return exception switch
        {
            ValidationException =>
                (StatusCodes.Status400BadRequest,
                 Application.Common.Exceptions.ValidationException.ErrorCode,
                 "Validation failed",
                 "One or more validation errors occurred."),

            BusinessRuleViolationException =>
                (StatusCodes.Status400BadRequest,
                 Application.Common.Exceptions.BusinessRuleViolationException.ErrorCode,
                 "Business rule violation",
                 exception.Message),

            DomainException =>
                (StatusCodes.Status400BadRequest,
                 "BUSINESS_RULE_VIOLATION",
                 "Business rule violation",
                 exception.Message),

            NotFoundException =>
                (StatusCodes.Status404NotFound,
                 Application.Common.Exceptions.NotFoundException.ErrorCode,
                 "Resource not found",
                 exception.Message),

            ConflictException =>
                (StatusCodes.Status409Conflict,
                 Application.Common.Exceptions.ConflictException.ErrorCode,
                 "Conflict",
                 exception.Message),

            ForbiddenException =>
                (StatusCodes.Status403Forbidden,
                 Application.Common.Exceptions.ForbiddenException.ErrorCode,
                 "Forbidden",
                 exception.Message),

            DbUpdateConcurrencyException =>
                (StatusCodes.Status409Conflict,
                 "CONFLICT",
                 "Concurrency conflict",
                 "The resource was modified by another request. Please retry."),

            DbUpdateException =>
                // Never expose SQL error message or inner exception details to client
                (StatusCodes.Status500InternalServerError,
                 "INTERNAL_SERVER_ERROR",
                 "An error occurred while saving data",
                 SafeServerErrorDetail()),

            _ =>
                (StatusCodes.Status500InternalServerError,
                 "INTERNAL_SERVER_ERROR",
                 "An unexpected error occurred",
                 SafeServerErrorDetail())
        };
    }

    /// <summary>
    /// Returns a safe, generic detail message for 500 errors.
    /// Never exposes exception message, stack trace, or connection information.
    /// </summary>
    private string SafeServerErrorDetail()
    {
        // Development: a slightly more helpful hint is acceptable, but still no secret data.
        if (_environment.IsDevelopment())
            return "An unexpected server error occurred. Check application logs for details.";

        return "An unexpected server error occurred. Please try again later.";
    }
}
