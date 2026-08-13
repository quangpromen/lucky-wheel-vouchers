using Microsoft.Extensions.DependencyInjection;

namespace LuckyWheel.Application;

public static class DependencyInjection
{
    /// <summary>
    /// Registers Application layer services.
    /// Note: <see cref="Common.Time.IClock"/> is registered by the Infrastructure layer
    /// because the implementation (SystemClock) lives there.
    /// </summary>
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        // Future phases: register command handlers, query handlers, etc.
        return services;
    }
}
