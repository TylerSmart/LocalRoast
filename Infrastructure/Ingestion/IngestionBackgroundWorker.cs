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
            using var startupScope = scopeFactory.CreateScope();
            var processor = startupScope.ServiceProvider.GetRequiredService<IIngestionProcessor>();
            await processor.RecoverInterruptedJobsAsync(stoppingToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Error during IngestionBackgroundWorker startup recovery.");
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var pollInterval = TimeSpan.FromSeconds(
                    Math.Max(1, options.Value.WorkerPollIntervalSeconds)
                );

                EffectiveLlmConnectionConfig config;
                using (var checkScope = scopeFactory.CreateScope())
                {
                    var settingsService =
                        checkScope.ServiceProvider.GetRequiredService<ILlmSettingsService>();
                    config = await settingsService.GetEffectiveConfigAsync(stoppingToken);

                    // Auto-resume check if scheduled pause window has elapsed
                    if (
                        !config.EnableIngestion
                        && config.AutoResumeQueue
                        && config.PausedUntilUtc.HasValue
                        && DateTimeOffset.UtcNow >= config.PausedUntilUtc.Value
                    )
                    {
                        await settingsService.SetIngestionEnabledAsync(true, stoppingToken);
                        config = await settingsService.GetEffectiveConfigAsync(stoppingToken);
                        logger.LogInformation(
                            "Automatically resumed ingestion queue after scheduled pause window elapsed ({Reason}).",
                            config.PauseReason ?? "Scheduled auto-resume"
                        );
                    }
                }
                var ingestionEnabled = config.EnableIngestion;

                // 1. Recover any orphaned/interrupted jobs if no active execution is running
                if (queueSignal.ActiveJobId == null)
                {
                    using var recoverScope = scopeFactory.CreateScope();
                    var processor =
                        recoverScope.ServiceProvider.GetRequiredService<IIngestionProcessor>();
                    await processor.RecoverInterruptedJobsAsync(stoppingToken);
                }

                // 2. Drain all pending queued jobs (supporting configurable max concurrency)
                var maxConcurrency = Math.Clamp(config.MaxConcurrentRequests, 1, 5);
                if (maxConcurrency <= 1)
                {
                    bool processedAny;
                    do
                    {
                        using var scope = scopeFactory.CreateScope();
                        var processor =
                            scope.ServiceProvider.GetRequiredService<IIngestionProcessor>();
                        processedAny = await processor.ProcessNextQueuedJobAsync(stoppingToken);
                    } while (processedAny && !stoppingToken.IsCancellationRequested);
                }
                else
                {
                    var runningTasks = new List<Task<bool>>();
                    while (!stoppingToken.IsCancellationRequested)
                    {
                        runningTasks.RemoveAll(t => t.IsCompleted);

                        while (
                            runningTasks.Count < maxConcurrency
                            && !stoppingToken.IsCancellationRequested
                        )
                        {
                            var scope = scopeFactory.CreateScope();
                            var processor =
                                scope.ServiceProvider.GetRequiredService<IIngestionProcessor>();
                            var task = processor.ProcessNextQueuedJobAsync(stoppingToken);
                            var scopedTask = task.ContinueWith(
                                t =>
                                {
                                    scope.Dispose();
                                    return t.GetAwaiter().GetResult();
                                },
                                TaskScheduler.Default
                            );

                            if (scopedTask.IsCompleted && !scopedTask.Result)
                            {
                                break;
                            }

                            runningTasks.Add(scopedTask);
                        }

                        if (runningTasks.Count == 0)
                        {
                            break;
                        }

                        var finished = await Task.WhenAny(runningTasks);
                        runningTasks.Remove(finished);
                        if (!finished.Result && runningTasks.Count == 0)
                        {
                            break;
                        }
                    }
                }

                // 3. Probe offline waiting jobs (only when ingestion is enabled)
                if (ingestionEnabled)
                {
                    using var probeScope = scopeFactory.CreateScope();
                    var processor =
                        probeScope.ServiceProvider.GetRequiredService<IIngestionProcessor>();
                    await processor.ProbeAndPromoteWaitingJobsAsync(stoppingToken);
                }

                // 4. Refine one review draft with the larger model while nothing is queued
                bool refined;
                using (var refineScope = scopeFactory.CreateScope())
                {
                    var reviewService =
                        refineScope.ServiceProvider.GetRequiredService<IIngestionReviewService>();
                    refined = await reviewService.RefineNextPendingDraftAsync(stoppingToken);
                }
                if (refined)
                {
                    continue;
                }

                // 5. Await signal or poll interval
                pollInterval = TimeSpan.FromSeconds(
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
