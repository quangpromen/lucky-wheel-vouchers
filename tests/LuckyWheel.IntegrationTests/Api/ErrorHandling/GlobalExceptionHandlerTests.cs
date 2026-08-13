using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using LuckyWheel.Api.Middleware;
using Xunit;

namespace LuckyWheel.IntegrationTests.Api.ErrorHandling;

/// <summary>
/// Integration tests for <see cref="LuckyWheel.Api.Errors.GlobalExceptionHandler"/>.
/// Uses <see cref="ErrorHandlingTestFactory"/> to host the API in-memory with test-only trigger endpoints.
/// Does NOT require SQL Server.
/// </summary>
[Trait("Category", "ErrorHandling")]
public sealed class GlobalExceptionHandlerTests : IClassFixture<ErrorHandlingTestFactory>
{
    private readonly HttpClient _client;

    public GlobalExceptionHandlerTests(ErrorHandlingTestFactory factory)
    {
        _client = factory.CreateClient();
    }

    // ── 1. ValidationException → 400, VALIDATION_ERROR, errors, traceId ──────

    [Fact]
    public async Task ValidationException_Returns400_WithValidationErrorCode_AndErrors_AndTraceId()
    {
        var response = await _client.GetAsync("/test-errors/validation");
        var body = await ReadProblemAsync(response);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("VALIDATION_ERROR", GetExtension(body, "errorCode"));
        Assert.True(body.TryGetProperty("errors", out _), "Response should contain 'errors' extension");
        AssertHasTraceId(body);
    }

    [Fact]
    public async Task ValidationException_Errors_ContainFieldMessages()
    {
        var response = await _client.GetAsync("/test-errors/validation");
        var body = await ReadProblemAsync(response);

        Assert.True(body.TryGetProperty("errors", out var errorsEl));
        Assert.True(errorsEl.TryGetProperty("email", out var emailEl));
        Assert.Equal("Email is required.", emailEl[0].GetString());
    }

    // ── 2. NotFoundException → 404, NOT_FOUND ────────────────────────────────

    [Fact]
    public async Task NotFoundException_Returns404_WithNotFoundErrorCode()
    {
        var response = await _client.GetAsync("/test-errors/not-found");
        var body = await ReadProblemAsync(response);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("NOT_FOUND", GetExtension(body, "errorCode"));
        AssertHasTraceId(body);
    }

    // ── 3. ConflictException → 409, CONFLICT ─────────────────────────────────

    [Fact]
    public async Task ConflictException_Returns409_WithConflictErrorCode()
    {
        var response = await _client.GetAsync("/test-errors/conflict");
        var body = await ReadProblemAsync(response);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("CONFLICT", GetExtension(body, "errorCode"));
        AssertHasTraceId(body);
    }

    // ── 4. BusinessRuleViolationException → 400, BUSINESS_RULE_VIOLATION ─────

    [Fact]
    public async Task BusinessRuleViolationException_Returns400_WithBusinessRuleViolationCode()
    {
        var response = await _client.GetAsync("/test-errors/business-rule");
        var body = await ReadProblemAsync(response);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("BUSINESS_RULE_VIOLATION", GetExtension(body, "errorCode"));
        AssertHasTraceId(body);
    }

    // ── 4b. DomainException → 400, BUSINESS_RULE_VIOLATION ───────────────────

    [Fact]
    public async Task DomainException_Returns400_WithBusinessRuleViolationCode()
    {
        var response = await _client.GetAsync("/test-errors/domain-exception");
        var body = await ReadProblemAsync(response);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("BUSINESS_RULE_VIOLATION", GetExtension(body, "errorCode"));
        AssertHasTraceId(body);
    }

    // ── 5. Unhandled exception → 500, no internal details leaked ─────────────

    [Fact]
    public async Task UnhandledException_Returns500_WithoutLeakingInternalDetails()
    {
        var response = await _client.GetAsync("/test-errors/unhandled");
        var body = await ReadProblemAsync(response);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("INTERNAL_SERVER_ERROR", GetExtension(body, "errorCode"));
        AssertHasTraceId(body);

        // Ensure sensitive content from the exception message is not leaked
        var bodyText = body.GetRawText();
        Assert.DoesNotContain("password=secret123", bodyText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("at System.", bodyText, StringComparison.OrdinalIgnoreCase);
    }

    // ── 6. Response has X-Correlation-ID header ───────────────────────────────

    [Fact]
    public async Task ErrorResponse_HasXCorrelationIdHeader()
    {
        var response = await _client.GetAsync("/test-errors/not-found");

        Assert.True(
            response.Headers.Contains(CorrelationIdMiddleware.CorrelationIdHeader),
            $"Response must contain header {CorrelationIdMiddleware.CorrelationIdHeader}");
    }

    [Fact]
    public async Task SuccessResponse_HasXCorrelationIdHeader()
    {
        var response = await _client.GetAsync("/test-errors/ok");

        Assert.True(
            response.Headers.Contains(CorrelationIdMiddleware.CorrelationIdHeader),
            $"CorrelationId header must be present even on success responses");
    }

    [Fact]
    public async Task ClientSupplied_ValidCorrelationId_IsReflectedInResponseHeader()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/test-errors/not-found");
        request.Headers.Add(CorrelationIdMiddleware.CorrelationIdHeader, "my-custom-trace-001");

        var response = await _client.SendAsync(request);

        var returned = response.Headers.GetValues(CorrelationIdMiddleware.CorrelationIdHeader)
                                        .FirstOrDefault();
        Assert.Equal("my-custom-trace-001", returned);
    }

    [Fact]
    public async Task ClientSupplied_TooLongCorrelationId_IsIgnored_SystemIdUsed()
    {
        var tooLong = new string('a', 65); // max length is 64
        var request = new HttpRequestMessage(HttpMethod.Get, "/test-errors/ok");
        request.Headers.Add(CorrelationIdMiddleware.CorrelationIdHeader, tooLong);

        var response = await _client.SendAsync(request);

        var returned = response.Headers.GetValues(CorrelationIdMiddleware.CorrelationIdHeader)
                                        .FirstOrDefault();
        // Must NOT reflect the invalid value
        Assert.NotEqual(tooLong, returned);
        Assert.NotNull(returned);
    }

    // ── 7. /health still returns 200 ─────────────────────────────────────────

    [Fact]
    public async Task HealthEndpoint_Returns200()
    {
        var response = await _client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static async Task<JsonElement> ReadProblemAsync(HttpResponseMessage response)
    {
        var content = await response.Content.ReadAsStringAsync();
        return JsonDocument.Parse(content).RootElement;
    }

    private static string? GetExtension(JsonElement body, string key)
    {
        return body.TryGetProperty(key, out var prop) ? prop.GetString() : null;
    }

    private static void AssertHasTraceId(JsonElement body)
    {
        Assert.True(body.TryGetProperty("traceId", out var traceIdEl),
            "ProblemDetails must contain 'traceId' extension");
        Assert.False(string.IsNullOrWhiteSpace(traceIdEl.GetString()),
            "'traceId' must not be empty");
    }
}
