using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Penghou.Zhinu.Hosting;

/// <summary>
/// Continuously admits locally available workflows into owned execution.
/// Admission fills free hosted capacity without waiting for the slowest
/// execution; completions free their slot for the next scan. Shutdown stops
/// admission and waits for owned executions within the configured bound.
/// </summary>
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
                var admitted = await engine.AdmitAvailableAsync(stoppingToken)
                    .ConfigureAwait(false);
                if (admitted == 0)
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
                    "Zhinu execution admission failed; the next scan will retry.");
                await Task.Delay(
                    options.PollInterval,
                    timeProvider,
                    stoppingToken).ConfigureAwait(false);
            }
        }
        logger.LogInformation("Zhinu embedded workflow execution stopped.");
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
        using var bound = new CancellationTokenSource(options.ShutdownTimeout);
        try
        {
            await engine.DrainAdmittedAsync(bound.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            logger.LogWarning(
                "Zhinu shutdown timed out waiting for owned executions; " +
                "their failures remain observable through run state and logging.");
        }
        catch (Exception exception)
        {
            logger.LogError(
                exception,
                "Zhinu shutdown observed an owned execution failure.");
        }
    }
}
