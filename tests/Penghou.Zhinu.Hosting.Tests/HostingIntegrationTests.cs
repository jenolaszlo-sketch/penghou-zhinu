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
            TestContext.Current.CancellationToken);

        result.Should().Be("HELLO");
        await host.StopAsync(TestContext.Current.CancellationToken);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(root))
            Directory.Delete(root, recursive: true);
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
}
