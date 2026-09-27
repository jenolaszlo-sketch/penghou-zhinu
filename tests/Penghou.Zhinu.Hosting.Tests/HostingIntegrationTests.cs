using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Penghou.Zhinu.Sqlite;

namespace Penghou.Zhinu.Hosting.Tests;

public sealed class HostingIntegrationTests : IDisposable
{
    private readonly string root = Path.Combine(
        Path.GetTempPath(),
        "penghou-zhinu-hosting-tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task HostedService_ExecutesPendingWorkflow()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddZhinuSqlite(options =>
            options.DatabasePath = Path.Combine(root, "zhinu.db"));
        builder.Services.AddZhinu(options =>
            options.PollInterval = TimeSpan.FromMilliseconds(10));
        builder.Services.AddZhinuWorkflow<EchoWorkflow, string, string>(
            "echo",
            "1");
        using var host = builder.Build();
        await host.StartAsync(TestContext.Current.CancellationToken);
        var engine = host.Services.GetRequiredService<WorkflowEngine>();

        var runId = await engine.StartAsync(
            "echo",
            "1",
            "hello",
            cancellationToken: TestContext.Current.CancellationToken);
        var result = await engine.WaitForCompletionAsync<string>(
            runId,
            cancellationToken: TestContext.Current.CancellationToken);

        result.Should().Be("HELLO");
        await host.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public void AddZhinu_WithoutStore_ThrowsAtCompositionTime()
    {
        var services = new ServiceCollection();

        var action = () => services.AddZhinu();

        action.Should().Throw<InvalidOperationException>()
            .WithMessage("*IWorkflowStore*");
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        for (var attempt = 1; Directory.Exists(root); attempt++)
        {
            try
            {
                Directory.Delete(root, recursive: true);
                return;
            }
            catch (IOException) when (attempt < 10)
            {
                SqliteConnection.ClearAllPools();
                GC.Collect();
                GC.WaitForPendingFinalizers();
                Thread.Sleep(50 * attempt);
            }
        }
    }

    private sealed class EchoWorkflow : IWorkflow<string, string>
    {
        public Task<string> RunAsync(
            WorkflowContext context,
            string input,
            CancellationToken cancellationToken) =>
            context.StepAsync(
                "uppercase",
                _ => Task.FromResult(input.ToUpperInvariant()),
                cancellationToken: cancellationToken);
    }

    [Fact]
    public async Task HostAdmitsNewWorkWhileEarlierRunWaits()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddZhinuSqlite(options =>
        {
            options.DatabasePath = Path.Combine(root, "zhinu-scheduler.db");
            options.Pooling = false;
        });
        builder.Services.AddZhinu(options =>
        {
            options.MaxConcurrentWorkflows = 4;
            options.PollInterval = TimeSpan.FromMilliseconds(10);
        });
        builder.Services.AddZhinuWorkflow<SchedulerReviewWorkflow, string, string>("work", "1");
        using var host = builder.Build();
        var engine = host.Services.GetRequiredService<WorkflowEngine>();
        var waiting = await engine.StartAsync(
            "work", "1", "wait", cancellationToken: TestContext.Current.CancellationToken);
        await host.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (!(await engine.GetStepsAsync(waiting, timeout.Token))
                .Any(s => s.Status == StepStatus.Waiting))
                await Task.Delay(10, timeout.Token);
            var ready = await engine.StartAsync("work", "1", "ready", cancellationToken: timeout.Token);
            while ((await engine.GetRunAsync(ready, timeout.Token))!.Status != WorkflowStatus.Completed)
                await Task.Delay(10, timeout.Token);
            (await engine.GetRunAsync(ready, timeout.Token))!.Status
                .Should().Be(WorkflowStatus.Completed);
        }
        finally
        {
            await host.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    private sealed class SchedulerReviewWorkflow : IWorkflow<string, string>
    {
        public Task<string> RunAsync(
            WorkflowContext context, string input, CancellationToken cancellationToken) =>
            input == "wait"
                ? context.WaitForSignalAsync<string>(
                    "wait", "release", cancellationToken: cancellationToken)
                : context.StepAsync(
                    "quick", _ => Task.FromResult(input), cancellationToken: cancellationToken);
    }

    [Fact]
    public async Task HostRespectsSharedConcurrencyBound()
    {
        ConcurrencyProbeWorkflow.Current = 0;
        ConcurrencyProbeWorkflow.MaxObserved = 0;
        ConcurrencyProbeWorkflow.Completed = 0;
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddZhinuSqlite(options =>
        {
            options.DatabasePath = Path.Combine(root, "zhinu-bound.db");
            options.Pooling = false;
        });
        builder.Services.AddZhinu(options =>
        {
            options.MaxConcurrentWorkflows = 2;
            options.PollInterval = TimeSpan.FromMilliseconds(10);
        });
        builder.Services.AddZhinuWorkflow<ConcurrencyProbeWorkflow, string, string>("probe", "1");
        using var host = builder.Build();
        var engine = host.Services.GetRequiredService<WorkflowEngine>();
        var ids = new List<Guid>();
        for (var i = 0; i < 4; i++)
            ids.Add(await engine.StartAsync(
                "probe", "1", $"w{i}", cancellationToken: TestContext.Current.CancellationToken));
        await host.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            foreach (var id in ids)
            {
                while ((await engine.GetRunAsync(id, timeout.Token))!.Status != WorkflowStatus.Completed)
                    await Task.Delay(10, timeout.Token);
            }
        }
        finally
        {
            await host.StopAsync(TestContext.Current.CancellationToken);
        }
        ConcurrencyProbeWorkflow.MaxObserved.Should().BeLessThanOrEqualTo(2);
        ConcurrencyProbeWorkflow.Completed.Should().Be(4);
    }

    [Fact]
    public async Task HostShutdown_DrainsOwnedExecutionsWithinBound()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddZhinuSqlite(options =>
        {
            options.DatabasePath = Path.Combine(root, "zhinu-shutdown.db");
            options.Pooling = false;
        });
        builder.Services.AddZhinu(options =>
        {
            options.PollInterval = TimeSpan.FromMilliseconds(10);
            options.ShutdownTimeout = TimeSpan.FromSeconds(10);
        });
        builder.Services.AddZhinuWorkflow<SchedulerReviewWorkflow, string, string>("work", "1");
        using var host = builder.Build();
        var engine = host.Services.GetRequiredService<WorkflowEngine>();
        var waiting = await engine.StartAsync(
            "work", "1", "wait", cancellationToken: TestContext.Current.CancellationToken);
        await host.StartAsync(TestContext.Current.CancellationToken);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!(await engine.GetStepsAsync(waiting, timeout.Token))
            .Any(s => s.Status == StepStatus.Waiting))
            await Task.Delay(10, timeout.Token);

        await host.StopAsync(TestContext.Current.CancellationToken);

        (await engine.GetRunAsync(waiting, TestContext.Current.CancellationToken))!.Status
            .Should().Be(WorkflowStatus.Running);
    }

    private sealed class ConcurrencyProbeWorkflow : IWorkflow<string, string>
    {
        public static int Current;
        public static int MaxObserved;
        public static int Completed;

        public async Task<string> RunAsync(
            WorkflowContext context, string input, CancellationToken cancellationToken)
        {
            var running = Interlocked.Increment(ref Current);
            try
            {
                int observed;
                do
                {
                    observed = MaxObserved;
                }
                while (running > observed &&
                    Interlocked.CompareExchange(ref MaxObserved, running, observed) != observed);
                await context.StepAsync(
                    "probe",
                    async (_, ct) =>
                    {
                        await Task.Delay(TimeSpan.FromMilliseconds(300), ct);
                        return input;
                    },
                    cancellationToken: cancellationToken);
                Interlocked.Increment(ref Completed);
                return input;
            }
            finally
            {
                Interlocked.Decrement(ref Current);
            }
        }
    }

    [Fact]
    public async Task TwoHosts_DoNotDuplicateExecution()
    {
        TwoHostCounter.Count = 0;
        var databasePath = Path.Combine(root, "zhinu-twohost.db");
        using var hostA = BuildHost(databasePath);
        using var hostB = BuildHost(databasePath);
        var engine = hostA.Services.GetRequiredService<WorkflowEngine>();
        var runId = await engine.StartAsync(
            "counted", "1", "x", cancellationToken: TestContext.Current.CancellationToken);
        await hostA.StartAsync(TestContext.Current.CancellationToken);
        await hostB.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            while ((await engine.GetRunAsync(runId, timeout.Token))!.Status != WorkflowStatus.Completed)
                await Task.Delay(10, timeout.Token);
        }
        finally
        {
            await hostA.StopAsync(TestContext.Current.CancellationToken);
            await hostB.StopAsync(TestContext.Current.CancellationToken);
        }
        TwoHostCounter.Count.Should().Be(1);
    }

    private static IHost BuildHost(string databasePath)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddZhinuSqlite(options =>
        {
            options.DatabasePath = databasePath;
            options.Pooling = false;
            options.BusyTimeout = TimeSpan.FromSeconds(30);
        });
        builder.Services.AddZhinu(options =>
            options.PollInterval = TimeSpan.FromMilliseconds(10));
        builder.Services.AddZhinuWorkflow<TwoHostWorkflow, string, string>("counted", "1");
        return builder.Build();
    }

    private sealed class TwoHostWorkflow : IWorkflow<string, string>
    {
        public Task<string> RunAsync(
            WorkflowContext context, string input, CancellationToken cancellationToken) =>
            context.StepAsync(
                "count",
                _ =>
                {
                    Interlocked.Increment(ref TwoHostCounter.Count);
                    return Task.FromResult(input);
                },
                cancellationToken: cancellationToken);
    }

    private static class TwoHostCounter
    {
        public static int Count;
    }
}
