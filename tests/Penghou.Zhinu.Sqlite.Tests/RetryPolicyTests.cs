using FluentAssertions;
using Penghou.Zhinu;
using Penghou.Zhinu.Sqlite;

namespace Penghou.Zhinu.Sqlite.Tests;

/// <summary>
/// Retry policy classification and bounds: permanent failures skip backoff,
/// jitter stays within its factor, invalid values are rejected, and the
/// eligible time survives a process restart.
/// </summary>
public sealed class RetryPolicyTests : WorkflowEngineTestBase
{
    [Fact]
    public void Classification_MatchesTypeAndBaseTypes()
    {
        var policy = new RetryPolicy
        {
            NonRetryableErrorTypes = [typeof(WorkflowStateException).FullName!]
        };

        policy.IsRetryable(new InvalidOperationException("transient")).Should().BeTrue();
        policy.IsRetryable(new WorkflowStateException("permanent")).Should().BeFalse();
        policy.IsRetryable(new WorkflowConcurrencyException("derived")).Should().BeFalse();
        new RetryPolicy().IsRetryable(new InvalidOperationException("x")).Should().BeTrue();
    }

    [Fact]
    public void Jitter_StaysWithinFactor()
    {
        var policy = new RetryPolicy
        {
            InitialDelay = TimeSpan.FromSeconds(10),
            JitterFactor = 0.5
        };

        for (var i = 0; i < 100; i++)
        {
            var delay = policy.DelayAfter(1);
            delay.Should().BeGreaterThanOrEqualTo(TimeSpan.FromSeconds(5));
            delay.Should().BeLessThanOrEqualTo(TimeSpan.FromSeconds(15));
        }
    }

    [Fact]
    public void Validation_RejectsInvalidValues()
    {
        var badJitter = new RetryPolicy { JitterFactor = 1.5 };
        var badAttempts = new RetryPolicy { MaxAttempts = 0 };

        Assert.Throws<ArgumentOutOfRangeException>(() => badJitter.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => badAttempts.Validate());
    }

    [Fact]
    public async Task PermanentFailure_SkipsBackoffAndFails()
    {
        var ct = TestContext.Current.CancellationToken;
        var workflow = new PermanentFailureWorkflow();
        var engine = CreateEngine(workflow, "retry-permanent");
        var runId = await engine.StartAsync("retry-permanent", "1", "x", cancellationToken: ct);

        var act = () => engine.RunAsync<string, string>(
            "retry-permanent", "1", "x", workflowRunId: runId, cancellationToken: ct);

        await act.Should().ThrowAsync<WorkflowExecutionFailedException>();
        workflow.Calls.Should().Be(1);
        var steps = await engine.GetStepsAsync(runId, ct);
        steps.Should().ContainSingle()
            .Which.AvailableAt.Should().BeNull();
    }

    [Fact]
    public async Task EligibleTime_SurvivesProcessRestart()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = CreateStore();
        var registry = new WorkflowRegistry().Register("retry-restart", "1", new FlakyOnceWorkflow());
        var options = new ZhinuOptions { PollInterval = TimeSpan.FromMilliseconds(10) };
        await using var first = new WorkflowEngine(store, registry, options);
        var runId = await first.StartAsync("retry-restart", "1", "x", cancellationToken: ct);
        await first.ExecuteAsync(runId, ct);
        var before = (await store.GetStepsAsync(runId, ct))
            .Single(s => s.StepKey == "flaky");
        before.Status.Should().Be(StepStatus.Waiting);
        var parked = (await store.GetWaitAsync(runId, "flaky", ct))!;
        parked.Status.Should().Be(WaitStatus.Parked);

        await first.DisposeAsync();
        await using var second = new WorkflowEngine(store, registry, options);
        await second.ExecuteAsync(runId, ct);
        var result = await second.WaitForCompletionAsync<string>(runId, cancellationToken: ct);

        result.Should().Be("recovered:x");
        (await store.GetWaitAsync(runId, "flaky", ct))!.AvailableAt
            .Should().Be(parked.AvailableAt);
    }

    private sealed class PermanentFailureWorkflow : IWorkflow<string, string>
    {
        public int Calls;

        public Task<string> RunAsync(
            WorkflowContext context, string input, CancellationToken cancellationToken) =>
            context.StepAsync(
                "fatal",
                input,
                (_, _) =>
                {
                    Interlocked.Increment(ref Calls);
                    return Task.FromException<string>(new WorkflowStateException("permanent"));
                },
                new StepOptions
                {
                    Retry = new RetryPolicy
                    {
                        MaxAttempts = 3,
                        InitialDelay = TimeSpan.FromMilliseconds(10),
                        NonRetryableErrorTypes = [typeof(WorkflowStateException).FullName!]
                    }
                },
                cancellationToken);
    }

    private sealed class FlakyOnceWorkflow : IWorkflow<string, string>
    {
        public int Calls;

        public Task<string> RunAsync(
            WorkflowContext context, string input, CancellationToken cancellationToken) =>
            context.StepAsync(
                "flaky",
                input,
                (_, _) =>
                {
                    if (Interlocked.Increment(ref Calls) == 1)
                        throw new InvalidOperationException("transient");
                    return Task.FromResult($"recovered:{input}");
                },
                new StepOptions
                {
                    Retry = new RetryPolicy { MaxAttempts = 2, InitialDelay = TimeSpan.FromMilliseconds(300) }
                },
                cancellationToken);
    }
}
