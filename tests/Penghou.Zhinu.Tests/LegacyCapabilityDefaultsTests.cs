using FluentAssertions;

namespace Penghou.Zhinu.Tests;

public sealed class LegacyCapabilityDefaultsTests
{
    [Fact]
    public async Task LegacyRuntime_ReceivesUnboundStartsThroughDefaultOverload()
    {
        var legacy = new LegacyRuntime();
        IWorkflowRuntime runtime = legacy;

        var result = await runtime.StartAsync("work", "1", "input",
            cancellationToken: TestContext.Current.CancellationToken);

        result.Should().Be(legacy.RunId);
        legacy.Calls.Should().Be(1);
    }

    [Fact]
    public async Task LegacyRuntime_RejectsBindingsRatherThanDiscardingThem()
    {
        var legacy = new LegacyRuntime();
        IWorkflowRuntime runtime = legacy;

        var action = () => runtime.StartAsync("work", "1", "input",
            authoritySubjectBinding: "subject", cancellationToken: TestContext.Current.CancellationToken);

        await action.Should().ThrowAsync<NotSupportedException>();
        legacy.Calls.Should().Be(0);
    }

    [Fact]
    public async Task LegacyStarter_DefaultOverloadsDelegateOnlyUnboundRequests()
    {
        var legacy = new LegacyStarter();
        IWorkflowStarter starter = legacy;
        var token = TestContext.Current.CancellationToken;

        (await starter.StartAsync("work", "1", "input", cancellationToken: token)).Should().Be(legacy.RunId);
        (await starter.RunAsync<string, string>("work", "1", "input", cancellationToken: token)).Should().Be("legacy");
        var handle = () => starter.StartHandleAsync<string, string>("work", "1", "input", cancellationToken: token);
        await handle.Should().ThrowAsync<NotImplementedException>().WithMessage("legacy-handle");
        legacy.Calls.Should().Be(3);

        var boundStart = () => starter.StartAsync("work", "1", "input", authoritySubjectBinding: "subject", cancellationToken: token);
        var boundRun = () => starter.RunAsync<string, string>("work", "1", "input", authoritySubjectBinding: "subject", cancellationToken: token);
        var boundHandle = () => starter.StartHandleAsync<string, string>("work", "1", "input", authoritySubjectBinding: "subject", cancellationToken: token);
        await boundStart.Should().ThrowAsync<NotSupportedException>();
        await boundRun.Should().ThrowAsync<NotSupportedException>();
        await boundHandle.Should().ThrowAsync<NotSupportedException>();
        legacy.Calls.Should().Be(3);
    }

    [Theory]
    [InlineData("StartAsync")]
    [InlineData("RunAsync")]
    [InlineData("StartHandleAsync")]
    public void Engine_RetainsOriginalBinarySignatures(string methodName)
    {
        var overload = typeof(WorkflowEngine).GetMethods().Single(method =>
            method.Name == methodName && method.GetParameters().Length == 7);

        overload.GetParameters()[6].ParameterType.Should().Be(typeof(CancellationToken));
        overload.GetParameters().Should().OnlyContain(parameter => !parameter.IsOptional);
    }

    // These implementations intentionally provide only the original seven-argument
    // members, proving new authority-aware calls do not require an added member.
    private sealed class LegacyRuntime : IWorkflowRuntime
    {
        public Guid RunId { get; } = Guid.NewGuid();
        public int Calls { get; private set; }

        public Task<Guid> StartAsync<TInput>(string workflowName, string workflowVersion,
            TInput input, Guid? workflowRunId, DateTimeOffset? deadline, object? metadata,
            CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(RunId);
        }

        public Task ExecuteAsync(Guid workflowRunId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<int> RunAvailableAsync(CancellationToken cancellationToken = default) => Task.FromResult(0);
    }

    private sealed class LegacyStarter : IWorkflowStarter
    {
        public Guid RunId { get; } = Guid.NewGuid();
        public int Calls { get; private set; }

        public Task<Guid> StartAsync<TInput>(string workflowName, string workflowVersion,
            TInput input, Guid? workflowRunId, DateTimeOffset? deadline, object? metadata,
            CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(RunId);
        }

        public Task<WorkflowHandle<TOutput>> StartHandleAsync<TInput, TOutput>(string workflowName,
            string workflowVersion, TInput input, Guid? workflowRunId, DateTimeOffset? deadline,
            object? metadata, CancellationToken cancellationToken)
        {
            Calls++;
            throw new NotImplementedException("legacy-handle");
        }

        public Task<TOutput> RunAsync<TInput, TOutput>(string workflowName, string workflowVersion,
            TInput input, Guid? workflowRunId, DateTimeOffset? deadline, object? metadata,
            CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult((TOutput)(object)"legacy");
        }
    }
}
