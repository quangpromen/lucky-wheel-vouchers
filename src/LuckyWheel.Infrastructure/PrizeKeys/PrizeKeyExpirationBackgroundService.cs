using System;
using System.Threading;
using System.Threading.Tasks;
using LuckyWheel.Application.Features.Admin.PrizeKeys;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LuckyWheel.Infrastructure.PrizeKeys;

public sealed class PrizeKeyExpirationBackgroundService(
    IServiceScopeFactory scopeFactory,
    IOptions<PrizeKeyExpirationOptions> options,
    ILogger<PrizeKeyExpirationBackgroundService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("PrizeKey expiration background worker started. Interval: {IntervalSeconds}s, BatchSize: {BatchSize}.",
            options.Value.IntervalSeconds, options.Value.BatchSize);

        while (!stoppingToken.IsCancellationRequested)
        {
            var config = options.Value;
            if (config.Enabled)
            {
                try
                {
                    using var scope = scopeFactory.CreateScope();
                    var processor = scope.ServiceProvider.GetRequiredService<IPrizeKeyExpirationProcessor>();
                    var result = await processor.ProcessExpiredKeysBatchAsync(config.BatchSize, stoppingToken);

                    if (result.ProcessedCount > 0)
                    {
                        logger.LogInformation(
                            "PrizeKey expiration worker finished batch. Processed: {ProcessedCount}, Succeeded: {SuccessCount}, Failed: {FailedCount}.",
                            result.ProcessedCount, result.SuccessCount, result.FailedCount);
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Unexpected error occurred during PrizeKey expiration background loop.");
                }
            }

            try
            {
                var delaySeconds = Math.Max(1, config.IntervalSeconds);
                await Task.Delay(TimeSpan.FromSeconds(delaySeconds), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }

        logger.LogInformation("PrizeKey expiration background worker has stopped.");
    }
}
