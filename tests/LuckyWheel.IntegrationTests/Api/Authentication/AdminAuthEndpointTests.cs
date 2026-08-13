using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace LuckyWheel.IntegrationTests.Api.Authentication;

public sealed class AdminAuthEndpointTests : IClassFixture<AdminAuthTestFactory>
{
    private readonly AdminAuthTestFactory _factory;
    private readonly HttpClient _client;

    public AdminAuthEndpointTests(AdminAuthTestFactory factory)
    {
        _factory = factory;
        _factory.State.IsActive = true;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task Login_WithInvalidPayload_ReturnsValidationProblem()
    {
        var response = await _client.PostAsJsonAsync("/api/admin/auth/login", new { username = "", password = "" });
        var body = await ReadAsync(response);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("VALIDATION_ERROR", body.GetProperty("errorCode").GetString());
        Assert.True(body.TryGetProperty("traceId", out _));
    }

    [Fact]
    public async Task Login_WithInvalidCredentials_ReturnsSameSafeResponse()
    {
        var unknown = await _client.PostAsJsonAsync("/api/admin/auth/login", Credentials("unknown@example.test", _factory.State.ValidPassword));
        var wrongPassword = await _client.PostAsJsonAsync("/api/admin/auth/login", Credentials("admin@example.test", _factory.State.InvalidPassword));
        var unknownBody = await ReadAsync(unknown);
        var wrongBody = await ReadAsync(wrongPassword);

        Assert.Equal(HttpStatusCode.Unauthorized, unknown.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, wrongPassword.StatusCode);
        Assert.Equal("INVALID_CREDENTIALS", unknownBody.GetProperty("errorCode").GetString());
        Assert.Equal(unknownBody.GetProperty("errorCode").GetString(), wrongBody.GetProperty("errorCode").GetString());
        Assert.Equal(unknownBody.GetProperty("detail").GetString(), wrongBody.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task Me_RequiresActiveAdminToken_AndPublicEndpointsRemainPublic()
    {
        var noToken = await _client.GetAsync("/api/admin/auth/me");
        var invalid = await SendMeAsync("not-a-token");
        var expired = await SendMeAsync(_factory.CreateExpiredToken());
        var login = await _client.PostAsJsonAsync("/api/admin/auth/login", Credentials("admin@example.test", _factory.State.ValidPassword));
        var loginBody = await ReadAsync(login);
        var token = loginBody.GetProperty("accessToken").GetString()!;
        var valid = await SendMeAsync(token);

        _factory.State.IsActive = false;
        var inactive = await SendMeAsync(token);
        _factory.State.IsActive = true;

        Assert.Equal(HttpStatusCode.Unauthorized, noToken.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, invalid.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, expired.StatusCode);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        Assert.Equal("Bearer", loginBody.GetProperty("tokenType").GetString());
        Assert.True(loginBody.TryGetProperty("expiresAtUtc", out _));
        Assert.False(loginBody.GetRawText().Contains("passwordHash", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(HttpStatusCode.OK, valid.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, inactive.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _client.GetAsync("/health")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _client.GetAsync("/api/system/info")).StatusCode);
    }

    private async Task<HttpResponseMessage> SendMeAsync(string token)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/admin/auth/me");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await _client.SendAsync(request);
    }

    private static async Task<JsonElement> ReadAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

    private static Dictionary<string, string> Credentials(string username, string password) =>
        new() { ["username"] = username, ["password"] = password };
}
