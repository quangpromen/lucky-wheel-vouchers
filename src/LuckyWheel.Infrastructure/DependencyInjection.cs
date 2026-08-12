using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace LuckyWheel.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        // Register Infrastructure layer services here in future phases
        // (e.g., DbContext, Repositories, External Services)
        return services;
    }
}
