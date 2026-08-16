using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Penghou.Zhinu.Hosting;

/// <summary>Continuously recovers and executes locally available workflows.</summary>
public sealed class ZhinuHostedService(
    WorkflowEngine engine,
    ZhinuOptions options,
    TimeProvider timeProvider,
    ILogger<ZhinuHostedService> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Zhinu embedded workflow execution started.");
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var processed = await engine.RunAvailableAsync(stoppingToken)
                    .ConfigureAwait(false);
                if (processed == 0)
                {
                    await Task.Delay(
                        options.PollInterval,
                        timeProvider,
                        stoppingToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(
                    exception,
                    "Zhinu execution scan failed; the next scan will retry.");
                await Task.Delay(
                    options.PollInterval,
                    timeProvider,
                    stoppingToken).ConfigureAwait(false);
            }
        }
        logger.LogInformation("Zhinu embedded workflow execution stopped.");
    }
}
