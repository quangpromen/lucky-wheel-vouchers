using System;
using System.Threading;
using System.Threading.Tasks;
using LuckyWheel.Application.Common.Authentication;
using LuckyWheel.Application.Common.Time;
using LuckyWheel.Domain.Entities;
using LuckyWheel.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace LuckyWheel.Infrastructure.Authentication;

public sealed class AdminAuthenticationService : IAdminAuthenticationService
{
    private readonly ApplicationDbContext _dbContext;
    private readonly IPasswordHasher<AdminUser> _passwordHasher;
    private readonly IJwtTokenGenerator _tokenGenerator;
    private readonly IClock _clock;

    public AdminAuthenticationService(ApplicationDbContext dbContext, IPasswordHasher<AdminUser> passwordHasher,
        IJwtTokenGenerator tokenGenerator, IClock clock)
    {
        _dbContext = dbContext;
        _passwordHasher = passwordHasher;
        _tokenGenerator = tokenGenerator;
        _clock = clock;
    }

    public async Task<AdminLoginResult?> LoginAsync(string username, string password, CancellationToken cancellationToken)
    {
        var normalized = username.Trim().ToLowerInvariant();
        var admin = await _dbContext.AdminUsers.SingleOrDefaultAsync(x => x.Email == normalized, cancellationToken);
        if (admin is null || !admin.IsActive || string.IsNullOrWhiteSpace(admin.PasswordHash)) return null;

        var verification = _passwordHasher.VerifyHashedPassword(admin, admin.PasswordHash, password);
        if (verification == PasswordVerificationResult.Failed) return null;

        var now = _clock.UtcNow;
        admin.RecordLogin(now.UtcDateTime);
        if (verification == PasswordVerificationResult.SuccessRehashNeeded)
            admin.SetPasswordHash(_passwordHasher.HashPassword(admin, password), now.UtcDateTime);
        await _dbContext.SaveChangesAsync(cancellationToken);
        var token = _tokenGenerator.Generate(admin);
        return new AdminLoginResult(token.AccessToken, token.ExpiresAtUtc,
            new AdminIdentity(admin.Id, admin.Email, admin.DisplayName));
    }

    public async Task<AdminIdentity?> GetActiveAdminAsync(Guid id, CancellationToken cancellationToken) =>
        await _dbContext.AdminUsers.AsNoTracking().Where(x => x.Id == id && x.IsActive)
            .Select(x => new AdminIdentity(x.Id, x.Email, x.DisplayName))
            .SingleOrDefaultAsync(cancellationToken);
}
