using LuckyWheel.Api.Controllers;
using Microsoft.AspNetCore.Authorization;
using LuckyWheel.IntegrationTests.Api.Authentication;
using System.Net;

namespace LuckyWheel.IntegrationTests.Api;

public sealed class AdminManagementEndpointMetadataTests
{
    [Theory]
    [InlineData(typeof(AdminWheelsController))]
    [InlineData(typeof(AdminPrizesController))]
    [InlineData(typeof(AdminWheelVersionsController))]
    public void Stage6Controllers_RequireAdminOnlyPolicy(Type controller)
    {
        var authorize = Assert.Single(controller.GetCustomAttributes(typeof(AuthorizeAttribute), true).Cast<AuthorizeAttribute>());
        Assert.Equal("AdminOnly", authorize.Policy);
    }

    [Theory]
    [InlineData(typeof(AdminWheelsController))]
    [InlineData(typeof(AdminPrizesController))]
    [InlineData(typeof(AdminWheelVersionsController))]
    public void Stage6Controllers_DoNotExposeAnonymousActions(Type controller)
    {
        Assert.Empty(controller.GetMethods().SelectMany(x => x.GetCustomAttributes(typeof(AllowAnonymousAttribute), true)));
    }
}

public sealed class AdminManagementAuthorizationTests : IClassFixture<AdminAuthTestFactory>
{
    private readonly HttpClient _client;

    public AdminManagementAuthorizationTests(AdminAuthTestFactory factory) => _client = factory.CreateClient();

    [Theory]
    [InlineData("/api/admin/wheels")]
    [InlineData("/api/admin/prizes")]
    [InlineData("/api/admin/wheel-versions/00000000-0000-0000-0000-000000000001")]
    public async Task Stage6Endpoints_WithoutToken_ReturnUnauthorized(string path)
    {
        var response = await _client.GetAsync(path);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
