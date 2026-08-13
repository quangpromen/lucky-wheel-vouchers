using System;
using System.Threading;
using System.Threading.Tasks;
using LuckyWheel.Domain.Entities;

namespace LuckyWheel.Application.Common.Authentication;

public interface IAdminAuthenticationService
{
    Task<AdminLoginResult?> LoginAsync(string username, string password, CancellationToken cancellationToken);
    Task<AdminIdentity?> GetActiveAdminAsync(Guid id, CancellationToken cancellationToken);
}

public interface IJwtTokenGenerator
{
    GeneratedAccessToken Generate(AdminUser adminUser);
}
