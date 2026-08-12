using Microsoft.Extensions.DependencyInjection;

namespace LuckyWheel.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        // Register Application layer services here in future phases
        return services;
    }
}
