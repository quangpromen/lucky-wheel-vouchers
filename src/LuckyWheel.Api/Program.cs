using LuckyWheel.Api.Errors;
using LuckyWheel.Api.Middleware;
using LuckyWheel.Application;
using LuckyWheel.Infrastructure;
using LuckyWheel.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc;
using Microsoft.OpenApi.Models;
using LuckyWheel.Infrastructure.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using System.Text;
using System.Security.Cryptography;

var builder = WebApplication.CreateBuilder(args);

// ── MVC / JSON ────────────────────────────────────────────────────────────────
builder.Services.AddControllers()
    .AddJsonOptions(options =>
    {
        // Ensure camelCase serialization for all API responses
        options.JsonSerializerOptions.PropertyNamingPolicy =
            System.Text.Json.JsonNamingPolicy.CamelCase;
    });
builder.Services.Configure<ApiBehaviorOptions>(options =>
{
    options.InvalidModelStateResponseFactory = context =>
    {
        var problem = new ValidationProblemDetails(context.ModelState)
        {
            Status = StatusCodes.Status400BadRequest,
            Title = "Validation failed",
            Detail = "One or more validation errors occurred.",
            Instance = context.HttpContext.Request.Path
        };
        problem.Extensions["errorCode"] = "VALIDATION_ERROR";
        problem.Extensions["traceId"] = context.HttpContext.TraceIdentifier;
        return new BadRequestObjectResult(problem);
    };
});

// ── Problem Details (RFC 7807) ─────────────────────────────────────────────────
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<GlobalExceptionHandler>();

// ── Application & Infrastructure layers ─────────────────────────────────────
builder.Services.AddApplication();
builder.Services.AddInfrastructure(builder.Configuration);

var jwt = builder.Configuration.GetSection(JwtOptions.SectionName).Get<JwtOptions>() ?? new JwtOptions();
if (!builder.Environment.IsEnvironment("Testing") && (string.IsNullOrWhiteSpace(jwt.Issuer) || string.IsNullOrWhiteSpace(jwt.Audience)
    || jwt.AccessTokenLifetimeMinutes <= 0 || Encoding.UTF8.GetByteCount(jwt.SigningKey) < 32))
    throw new InvalidOperationException("Jwt configuration is invalid. Issuer, Audience, positive lifetime, and a signing key of at least 32 bytes are required.");

if (builder.Environment.IsEnvironment("Testing") && Encoding.UTF8.GetByteCount(jwt.SigningKey) < 32)
    jwt = new JwtOptions
    {
        Issuer = "LuckyWheel.Tests",
        Audience = "LuckyWheel.Tests",
        AccessTokenLifetimeMinutes = 5,
        SigningKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(48))
    };

var prizeKeyOpt = builder.Configuration.GetSection(LuckyWheel.Infrastructure.PrizeKeys.PrizeKeyProtectionOptions.SectionName).Get<LuckyWheel.Infrastructure.PrizeKeys.PrizeKeyProtectionOptions>() ?? new LuckyWheel.Infrastructure.PrizeKeys.PrizeKeyProtectionOptions();
if (builder.Environment.IsEnvironment("Testing") && string.IsNullOrWhiteSpace(prizeKeyOpt.EncryptionKey))
{
    builder.Services.Configure<LuckyWheel.Infrastructure.PrizeKeys.PrizeKeyProtectionOptions>(options =>
    {
        options.EncryptionKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
    });
}
else if (!builder.Environment.IsEnvironment("Testing"))
{
    prizeKeyOpt.GetKeyBytesOrThrow();
}

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true, ValidIssuer = jwt.Issuer,
            ValidateAudience = true, ValidAudience = jwt.Audience,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.SigningKey)),
            ValidateLifetime = true, ClockSkew = TimeSpan.Zero
        };
        options.Events = new JwtBearerEvents
        {
            OnTokenValidated = async context =>
            {
                var idValue = context.Principal?.FindFirst(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub)?.Value
                    ?? context.Principal?.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
                if (!Guid.TryParse(idValue, out var id)) { context.Fail("Invalid admin identity."); return; }
                var service = context.HttpContext.RequestServices.GetRequiredService<LuckyWheel.Application.Common.Authentication.IAdminAuthenticationService>();
                if (await service.GetActiveAdminAsync(id, context.HttpContext.RequestAborted) is null)
                    context.Fail("Admin account is unavailable.");
            },
            OnChallenge = async context =>
            {
                context.HandleResponse();
                context.Response.StatusCode = 401;
                context.Response.ContentType = "application/problem+json";
                await context.Response.WriteAsJsonAsync(new
                {
                    type = "https://httpstatuses.com/401", title = "Unauthorized", status = 401,
                    detail = "Authentication is required.", instance = context.Request.Path.Value,
                    traceId = context.HttpContext.TraceIdentifier, errorCode = "UNAUTHORIZED"
                });
            }
        };
    });
builder.Services.AddAuthorization(options => options.AddPolicy("AdminOnly", policy =>
    policy.RequireAuthenticatedUser().RequireRole("Admin")));

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
    options.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Name = "Authorization", In = ParameterLocation.Header, Type = SecuritySchemeType.Http,
        Scheme = "bearer", BearerFormat = "JWT", Description = "Enter a JWT access token."
    });
    options.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        [new OpenApiSecurityScheme { Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "Bearer" } }] = Array.Empty<string>()
    });
    options.IncludeXmlComments(Path.Combine(AppContext.BaseDirectory, "LuckyWheel.Api.xml"));
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
app.UseAuthentication();
app.UseAuthorization();

// 6. Controllers
app.MapControllers();

// 7. Health check
app.MapHealthChecks("/health");

if (app.Environment.IsDevelopment())
{
    using var scope = app.Services.CreateScope();
    await scope.ServiceProvider.GetRequiredService<BootstrapAdminSeeder>().SeedAsync();
}

app.Run();

// Make Program class accessible to WebApplicationFactory in integration tests
public partial class Program { }
