using LuckyWheel.Application.Common.Time;
using LuckyWheel.Domain.Entities;
using LuckyWheel.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LuckyWheel.Infrastructure.Authentication;

public sealed class BootstrapAdminSeeder(ApplicationDbContext dbContext, IPasswordHasher<AdminUser> hasher,
    IOptions<BootstrapAdminOptions> options, IClock clock, ILogger<BootstrapAdminSeeder> logger)
{
    public async Task SeedAsync(CancellationToken cancellationToken = default)
    {
        var settings = options.Value;
        if (string.IsNullOrWhiteSpace(settings.Username) || string.IsNullOrWhiteSpace(settings.Password))
        {
            logger.LogInformation("Bootstrap admin configuration is absent; seeding skipped.");
            return;
        }
        var username = settings.Username.Trim().ToLowerInvariant();
        if (await dbContext.AdminUsers.AnyAsync(x => x.Email == username, cancellationToken))
        {
            logger.LogInformation("Bootstrap admin already exists; password was not changed.");
            return;
        }
        var now = clock.UtcNow.UtcDateTime;
        var admin = new AdminUser(username, string.IsNullOrWhiteSpace(settings.DisplayName) ? "Administrator" : settings.DisplayName.Trim(), now);
        admin.SetPasswordHash(hasher.HashPassword(admin, settings.Password), now);
        dbContext.AdminUsers.Add(admin);
        await dbContext.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Bootstrap admin created successfully.");
    }
}
