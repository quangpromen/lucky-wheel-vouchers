using System;
using LuckyWheel.Application.Common.Time;
using LuckyWheel.Application.Common.Authentication;
using LuckyWheel.Domain.Entities;
using LuckyWheel.Infrastructure.Authentication;
using LuckyWheel.Infrastructure.Persistence;
using LuckyWheel.Infrastructure.Time;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Identity;

namespace LuckyWheel.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        // ── Clock ──────────────────────────────────────────────────────────
        services.AddSingleton<IClock, SystemClock>();
        services.AddScoped<IPasswordHasher<AdminUser>, PasswordHasher<AdminUser>>();
        services.AddScoped<IAdminAuthenticationService, AdminAuthenticationService>();
        services.AddSingleton<IJwtTokenGenerator, JwtTokenGenerator>();
        services.Configure<JwtOptions>(configuration.GetSection(JwtOptions.SectionName));
        services.Configure<BootstrapAdminOptions>(configuration.GetSection(BootstrapAdminOptions.SectionName));
        services.AddScoped<BootstrapAdminSeeder>();

        // ── Database ───────────────────────────────────────────────────────
        var connectionString = configuration.GetConnectionString("DefaultConnection");

        services.AddDbContext<ApplicationDbContext>(options =>
        {
            if (string.IsNullOrWhiteSpace(connectionString))
            {
                // Note: Delayed check or immediate warning, DbContext configuration will throw on usage if missing
                options.UseSqlServer(
                    "Server=invalid;Database=invalid;Trusted_Connection=False;",
                    sqlOptions => sqlOptions.MigrationsAssembly(typeof(ApplicationDbContext).Assembly.FullName));
                return;
            }

            options.UseSqlServer(
                connectionString,
                sqlOptions =>
                {
                    sqlOptions.MigrationsAssembly(typeof(ApplicationDbContext).Assembly.FullName);
                    sqlOptions.EnableRetryOnFailure(
                        maxRetryCount: 5,
                        maxRetryDelay: TimeSpan.FromSeconds(10),
                        errorNumbersToAdd: null);
                });
        });

        return services;
    }
}
