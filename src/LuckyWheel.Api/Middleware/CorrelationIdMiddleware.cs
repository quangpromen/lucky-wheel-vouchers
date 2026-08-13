using System;
using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;

namespace LuckyWheel.Api.Middleware;

/// <summary>
/// Middleware that ensures every request and response carries a stable correlation / trace id.
///
/// Resolution order:
///   1. Incoming header <c>X-Correlation-ID</c> (if present and passes safety check).
///   2. <c>Activity.Current?.Id</c> (W3C TraceParent / OpenTelemetry).
///   3. <c>HttpContext.TraceIdentifier</c> (ASP.NET Core fallback).
///
/// The resolved id is:
///   • Stored in <c>HttpContext.Items["CorrelationId"]</c> for downstream use.
///   • Returned in the response header <c>X-Correlation-ID</c>.
///
/// Security: client-supplied values are validated (length ≤ 64, safe chars only).
/// The correlation id is never used for authorization or business identity.
/// </summary>
public sealed class CorrelationIdMiddleware
{
    /// <summary>Key used to store the resolved correlation id in <see cref="HttpContext.Items"/>.</summary>
    public const string CorrelationIdItemKey = "CorrelationId";

    /// <summary>Header name accepted from clients and sent back in responses.</summary>
    public const string CorrelationIdHeader = "X-Correlation-ID";

    private const int MaxCorrelationIdLength = 64;

    // Allow alphanumeric, hyphens, underscores, and dots — common in trace/request ids.
    private static readonly Regex SafeCorrelationIdRegex =
        new(@"^[a-zA-Z0-9\-_\.]+$", RegexOptions.Compiled, TimeSpan.FromMilliseconds(100));

    private readonly RequestDelegate _next;

    public CorrelationIdMiddleware(RequestDelegate next)
    {
        _next = next ?? throw new ArgumentNullException(nameof(next));
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var correlationId = ResolveCorrelationId(context);

        context.Items[CorrelationIdItemKey] = correlationId;
        context.Response.Headers[CorrelationIdHeader] = correlationId;

        await _next(context);
    }

    private static string ResolveCorrelationId(HttpContext context)
    {
        // 1. Try client-supplied header — only accept if safe
        if (context.Request.Headers.TryGetValue(CorrelationIdHeader, out var headerValues))
        {
            var clientValue = headerValues.ToString();
            if (IsValidCorrelationId(clientValue))
                return clientValue;
        }

        // 2. W3C Activity / OpenTelemetry trace id
        var activityId = Activity.Current?.Id;
        if (!string.IsNullOrWhiteSpace(activityId))
            return activityId;

        // 3. ASP.NET Core fallback
        return context.TraceIdentifier;
    }

    internal static bool IsValidCorrelationId(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        if (value.Length > MaxCorrelationIdLength) return false;
        return SafeCorrelationIdRegex.IsMatch(value);
    }
}
