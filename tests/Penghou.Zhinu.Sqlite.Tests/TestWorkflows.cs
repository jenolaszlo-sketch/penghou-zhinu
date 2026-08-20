namespace Penghou.Zhinu.Sqlite.Tests;

internal sealed class TwoStepWorkflow : IWorkflow<string, string>
{
    public int FirstCalls { get; private set; }
    public int SecondCalls { get; private set; }
    public Guid RunId { get; private set; }

    public async Task<string> RunAsync(
        WorkflowContext context,
        string input,
        CancellationToken cancellationToken)
    {
        RunId = context.WorkflowRunId;
        var first = await context.StepAsync(
            "first",
            input,
            (value, _) =>
            {
                FirstCalls++;
                return Task.FromResult($"Hello, {value}");
            },
            cancellationToken: cancellationToken);
        return await context.StepAsync(
            "second",
            first,
            (value, _) =>
            {
                SecondCalls++;
                return Task.FromResult($"{value}!");
            },
            cancellationToken: cancellationToken);
    }
}

internal sealed class RestartableWorkflow : IWorkflow<string, string>
{
    public string SecondSuffix = "a";
    public int FirstCalls;
    public int SecondCalls;
    public Guid RunId;

    public async Task<string> RunAsync(
        WorkflowContext context,
        string input,
        CancellationToken cancellationToken)
    {
        RunId = context.WorkflowRunId;
        var first = await context.StepAsync(
            "first",
            input,
            (value, _) =>
            {
                FirstCalls++;
                return Task.FromResult($"hello-{value}");
            },
            cancellationToken: cancellationToken);
        return await context.StepAsync(
            "second",
            first,
            (value, _) =>
            {
                SecondCalls++;
                return Task.FromResult($"{value}-{SecondSuffix}");
            },
            cancellationToken: cancellationToken);
    }
}

internal sealed class DependentStepsWorkflow : IWorkflow<string, string>
{
    public int ACalls;
    public int BCalls;
    public int CCalls;
    public int DCalls;
    public int ECalls;
    public Guid RunId { get; private set; }

    public async Task<string> RunAsync(
        WorkflowContext context,
        string input,
        CancellationToken cancellationToken)
    {
        RunId = context.WorkflowRunId;
        var a = await context.StepAsync(
            "a",
            input,
            (value, _) =>
            {
                ACalls++;
                return Task.FromResult($"A({value})");
            },
            cancellationToken: cancellationToken);
        var c = await context.StepAsync(
            "c",
            input,
            (value, _) =>
            {
                CCalls++;
                return Task.FromResult($"C({value})");
            },
            cancellationToken: cancellationToken);
        string b;
        using (context.DependsOn("a"))
        {
            b = await context.StepAsync(
                "b",
                a,
                (value, _) =>
                {
                    BCalls++;
                    return Task.FromResult($"B[{value}]");
                },
                cancellationToken: cancellationToken);
        }
        string d;
        using (context.DependsOn("b"))
        {
            d = await context.StepAsync(
                "d",
                b,
                (value, _) =>
                {
                    DCalls++;
                    return Task.FromResult($"D[{value}]");
                },
                cancellationToken: cancellationToken);
        }
        string e;
        using (context.DependsOn("c"))
        {
            e = await context.StepAsync(
                "e",
                c,
                (value, _) =>
                {
                    ECalls++;
                    return Task.FromResult($"E[{value}]");
                },
                cancellationToken: cancellationToken);
        }
        return $"{d}|{e}";
    }
}

internal sealed class CompensationWorkflow : IWorkflow<string, string>
{
    public int ReserveCalls;
    public int CompensationCalls;
    public Guid RunId { get; private set; }

    public async Task<string> RunAsync(
        WorkflowContext context,
        string input,
        CancellationToken cancellationToken)
    {
        RunId = context.WorkflowRunId;
        var reservation = await context.StepAsync(
            "reserve",
            input,
            (value, _) =>
            {
                ReserveCalls++;
                return Task.FromResult($"vm-{value}");
            },
            compensation: async (result, step, ct) =>
            {
                CompensationCalls++;
                await Task.Delay(1, ct);
            },
            cancellationToken: cancellationToken);
        return await context.StepAsync(
            "confirm",
            reservation,
            (value, _) => Task.FromResult($"confirmed:{value}"),
            cancellationToken: cancellationToken);
    }
}

internal sealed class FailingCompensatedWorkflow : IWorkflow<string, string>
{
    public Task<string> RunAsync(
        WorkflowContext context,
        string input,
        CancellationToken cancellationToken) =>
        context.StepAsync<string, string>(
            "reserve",
            input,
            (value, ct) => Task.FromException<string>(
                new InvalidOperationException("quota exceeded")),
            compensation: (result, step, ct) => Task.CompletedTask,
            cancellationToken: cancellationToken);
}

internal sealed class RollbackWorkflow : IWorkflow<string, string>
{
    private readonly IReadOnlySet<string> failCompensations;

    public RollbackWorkflow(params string[] failCompensations)
    {
        this.failCompensations = new HashSet<string>(failCompensations);
    }

    public List<string> CompensatedOrder { get; } = [];

    public Guid RunId { get; private set; }

    public async Task<string> RunAsync(
        WorkflowContext context,
        string input,
        CancellationToken cancellationToken)
    {
        RunId = context.WorkflowRunId;
        var plan = await context.StepAsync(
            "plan",
            input,
            (value, _) => Task.FromResult($"plan-{value}"),
            cancellationToken: cancellationToken);
        string payment;
        using (context.DependsOn("plan"))
        {
            payment = await context.StepAsync(
                "payment",
                plan,
                (value, _) => Task.FromResult($"paid:{value}"),
                compensation: (result, step, ct) =>
                    Compensate("payment", cancellationToken),
                cancellationToken: cancellationToken);
            await context.StepAsync(
                "frontend",
                plan,
                (value, _) => Task.FromResult($"ui:{value}"),
                compensation: (result, step, ct) =>
                    Compensate("frontend", cancellationToken),
                cancellationToken: cancellationToken);
        }
        string deploy;
        using (context.DependsOn("payment"))
        {
            deploy = await context.StepAsync(
                "deploy",
                payment,
                (value, _) => Task.FromResult($"deployed:{value}"),
                compensation: (result, step, ct) =>
                    Compensate("deploy", cancellationToken),
                cancellationToken: cancellationToken);
        }
        using (context.DependsOn("deploy"))
        {
            await context.StepAsync(
                "tests",
                deploy,
                (value, _) => Task.FromResult($"tested:{value}"),
                compensation: (result, step, ct) =>
                    Compensate("tests", cancellationToken),
                cancellationToken: cancellationToken);
        }
        return "done";
    }

    private Task Compensate(
        string stepKey,
        CancellationToken cancellationToken)
    {
        CompensatedOrder.Add(stepKey);
        if (failCompensations.Contains(stepKey))
        {
            throw new InvalidOperationException(
                $"Cannot undo '{stepKey}'.");
        }
        return Task.CompletedTask;
    }
}

internal sealed class CapturingCompensationWorkflow : IWorkflow<string, string>
{
    public List<string> CompensatedResults { get; } = [];

    public List<string> CompensationKeys { get; } = [];

    public Guid RunId { get; private set; }

    public async Task<string> RunAsync(
        WorkflowContext context,
        string input,
        CancellationToken cancellationToken)
    {
        RunId = context.WorkflowRunId;
        var reservation = await context.StepAsync(
            "reserve",
            input,
            (value, _) => Task.FromResult($"vm-{value}"),
            compensation: (result, step, ct) =>
            {
                CompensatedResults.Add(result);
                CompensationKeys.Add(step.IdempotencyKey);
                return Task.CompletedTask;
            },
            cancellationToken: cancellationToken);
        return await context.StepAsync(
            "confirm",
            reservation,
            (value, _) => Task.FromResult($"confirmed:{value}"),
            cancellationToken: cancellationToken);
    }
}

internal sealed class SignalWorkflow : IWorkflow<string, string>
{
    public TimeSpan? WaitTimeout { get; set; }
    public Guid RunId { get; private set; }

    public async Task<string> RunAsync(
        WorkflowContext context,
        string input,
        CancellationToken cancellationToken)
    {
        RunId = context.WorkflowRunId;
        await context.StepAsync(
            "greet",
            input,
            (value, _) => Task.FromResult($"hello-{value}"),
            cancellationToken: cancellationToken);
        return await context.WaitForSignalAsync<string>(
            "approval",
            "approve",
            WaitTimeout,
            cancellationToken);
    }
}

internal sealed class ParentWorkflow : IWorkflow<string, string>
{
    public async Task<string> RunAsync(
        WorkflowContext context,
        string input,
        CancellationToken cancellationToken)
    {
        var first = await context.StepAsync(
            "parent-step",
            input,
            (value, _) => Task.FromResult($"parent:{value}"),
            cancellationToken: cancellationToken);
        return await context.StartChildAsync<string, string>(
            "child",
            "child",
            "1",
            first,
            cancellationToken);
    }
}

internal sealed class ChildWorkflow : IWorkflow<string, string>
{
    public int Calls { get; private set; }

    public async Task<string> RunAsync(
        WorkflowContext context,
        string input,
        CancellationToken cancellationToken)
    {
        Calls++;
        return await context.StepAsync(
            "child-step",
            input,
            (value, _) => Task.FromResult($"child:{value}"),
            cancellationToken: cancellationToken);
    }
}

internal sealed class FailingChildWorkflow : IWorkflow<string, string>
{
    public Task<string> RunAsync(
        WorkflowContext context,
        string input,
        CancellationToken cancellationToken) =>
        Task.FromException<string>(
            new InvalidOperationException("child failed"));
}

internal sealed class FailingParentWorkflow : IWorkflow<string, string>
{
    public Task<string> RunAsync(
        WorkflowContext context,
        string input,
        CancellationToken cancellationToken) =>
        context.StartChildAsync<string, string>(
            "child",
            "bad-child",
            "1",
            input,
            cancellationToken);
}

internal sealed class RetryWorkflow : IWorkflow<string, string>
{
    public int Calls { get; private set; }
    public Guid RunId { get; private set; }

    public async Task<string> RunAsync(
        WorkflowContext context,
        string input,
        CancellationToken cancellationToken)
    {
        RunId = context.WorkflowRunId;
        return await context.StepAsync(
            "unstable",
            input,
            (value, _) =>
            {
                Calls++;
                if (Calls == 1)
                    throw new InvalidOperationException("transient");
                return Task.FromResult($"{value}-ok");
            },
            new StepOptions
            {
                Retry = new RetryPolicy
                {
                    MaxAttempts = 2,
                    InitialDelay = TimeSpan.Zero
                }
            },
            cancellationToken);
    }
}

internal sealed class FailingWorkflow : IWorkflow<string, string>
{
    public Task<string> RunAsync(
        WorkflowContext context,
        string input,
        CancellationToken cancellationToken) =>
        context.StepAsync<string>(
            "fail",
            _ => throw new InvalidOperationException("planned failure"),
            cancellationToken: cancellationToken);
}

internal sealed class DelayWorkflow : IWorkflow<string, string>
{
    public Guid RunId { get; private set; }

    public async Task<string> RunAsync(
        WorkflowContext context,
        string input,
        CancellationToken cancellationToken)
    {
        RunId = context.WorkflowRunId;
        await context.DelayAsync(
            "short-delay",
            TimeSpan.FromMilliseconds(25),
            cancellationToken);
        return input;
    }
}

internal sealed class ParallelSameKeyWorkflow : IWorkflow<string, string>
{
    public int Calls;

    public async Task<string> RunAsync(
        WorkflowContext context,
        string input,
        CancellationToken cancellationToken)
    {
        Task<string> Call() => context.StepAsync(
            "shared",
            input,
            async (value, ct) =>
            {
                Interlocked.Increment(ref Calls);
                await Task.Delay(20, ct);
                return value;
            },
            cancellationToken: cancellationToken);
        var results = await Task.WhenAll(Call(), Call());
        return string.Join(':', results);
    }
}

internal sealed class RecoveringWorkflow(bool blockSecond)
    : IWorkflow<string, string>
{
    public int FirstCalls { get; private set; }
    public int SecondCalls { get; private set; }
    public TaskCompletionSource SecondStarted { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public async Task<string> RunAsync(
        WorkflowContext context,
        string input,
        CancellationToken cancellationToken)
    {
        var first = await context.StepAsync(
            "first",
            _ =>
            {
                FirstCalls++;
                return Task.FromResult("first");
            },
            cancellationToken: cancellationToken);
        return await context.StepAsync(
            "second",
            async (_, ct) =>
            {
                SecondCalls++;
                SecondStarted.TrySetResult();
                if (blockSecond)
                    await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                return $"{first}-second";
            },
            cancellationToken: cancellationToken);
    }
}

internal sealed class SlowWorkflow : IWorkflow<string, string>
{
    public int Calls;

    public async Task<string> RunAsync(
        WorkflowContext context,
        string input,
        CancellationToken cancellationToken) =>
        await context.StepAsync(
            "slow",
            async ct =>
            {
                Interlocked.Increment(ref Calls);
                await Task.Delay(50, ct);
                return input;
            },
            cancellationToken: cancellationToken);
}

internal sealed class TimeoutWorkflow : IWorkflow<string, string>
{
    public Task<string> RunAsync(
        WorkflowContext context,
        string input,
        CancellationToken cancellationToken) =>
        context.StepAsync(
            "timeout",
            async (_, ct) =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                return input;
            },
            new StepOptions
            {
                ExecutionTimeout = TimeSpan.FromMilliseconds(25)
            },
            cancellationToken);
}

internal sealed class ProgressWorkflow : IWorkflow<string, string>
{
    public Guid RunId { get; private set; }

    public async Task<string> RunAsync(
        WorkflowContext context,
        string input,
        CancellationToken cancellationToken)
    {
        RunId = context.WorkflowRunId;
        await context.EmitAsync(
            WorkflowEventTypes.Progress,
            25,
            cancellationToken);
        await context.EmitAsync(
            WorkflowEventTypes.Progress,
            50,
            cancellationToken);
        await context.EmitAsync(
            WorkflowEventTypes.Progress,
            75,
            cancellationToken);
        return $"{input}-done";
    }
}

internal sealed class FanOutWorkflow : IWorkflow<string, string>
{
    public int Calls;
    public Guid RunId { get; private set; }

    public async Task<string> RunAsync(
        WorkflowContext context,
        string input,
        CancellationToken cancellationToken)
    {
        RunId = context.WorkflowRunId;
        var values = input.Split(',');
        var results = await context.FanOutAsync(
            "process",
            values,
            async (value, _, ct) =>
            {
                Interlocked.Increment(ref Calls);
                await Task.Delay(5, ct);
                return value.ToUpperInvariant();
            },
            cancellationToken: cancellationToken);
        return string.Join(':', results);
    }
}
