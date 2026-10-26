using App.Core.Ingestion;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace App.Infrastructure.Ingestion;

public sealed class IngestionBackgroundWorker(
    IServiceScopeFactory scopeFactory,
    IIngestionQueueSignal queueSignal,
    IOptions<LlmDefaultsOptions> options,
    ILogger<IngestionBackgroundWorker> logger
) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.EnableBackgroundWorker)
        {
            logger.LogInformation("IngestionBackgroundWorker is disabled via configuration.");
            return;
        }

        logger.LogInformation("IngestionBackgroundWorker started.");

        try
        {
            using (var startupScope = scopeFactory.CreateScope())
            {
                var processor =
                    startupScope.ServiceProvider.GetRequiredService<IIngestionProcessor>();
                await processor.RecoverInterruptedJobsAsync(stoppingToken);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Error during IngestionBackgroundWorker startup recovery.");
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // 1. Drain all pending queued jobs
                bool processedAny;
                do
                {
                    using var scope = scopeFactory.CreateScope();
                    var processor = scope.ServiceProvider.GetRequiredService<IIngestionProcessor>();
                    processedAny = await processor.ProcessNextQueuedJobAsync(stoppingToken);
                } while (processedAny && !stoppingToken.IsCancellationRequested);

                // 2. Probe offline waiting jobs
                using (var probeScope = scopeFactory.CreateScope())
                {
                    var processor =
                        probeScope.ServiceProvider.GetRequiredService<IIngestionProcessor>();
                    await processor.ProbeAndPromoteWaitingJobsAsync(stoppingToken);
                }

                // 3. Await signal or poll interval
                var pollInterval = TimeSpan.FromSeconds(
                    Math.Max(1, options.Value.WorkerPollIntervalSeconds)
                );
                await queueSignal.WaitForWorkAsync(pollInterval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Unhandled error in IngestionBackgroundWorker loop.");
                await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken);
            }
        }

        logger.LogInformation("IngestionBackgroundWorker stopped.");
    }
}
