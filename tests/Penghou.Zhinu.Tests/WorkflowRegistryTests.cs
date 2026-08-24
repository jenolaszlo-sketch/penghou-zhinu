using FluentAssertions;

namespace Penghou.Zhinu.Tests;

public sealed class WorkflowRegistryTests
{
    [Fact]
    public void Register_ResolvesWorkflowByNameAndVersion()
    {
        var registry = new WorkflowRegistry()
            .Register("sample", "1", new EchoWorkflow());

        var registration = registry.Get("sample", "1");

        registration.InputType.Should().Be(typeof(string));
        registration.OutputType.Should().Be(typeof(string));
    }

    [Fact]
    public void Register_RejectsDuplicateDefinition()
    {
        var registry = new WorkflowRegistry()
            .Register("sample", "1", new EchoWorkflow());

        var action = () => registry.Register(
            "sample",
            "1",
            new EchoWorkflow());

        action.Should().Throw<WorkflowRegistrationException>()
            .WithMessage("*already registered*");
    }

    [Fact]
    public void Get_RejectsUnavailableVersion()
    {
        var registry = new WorkflowRegistry();

        var action = () => registry.Get("sample", "2");

        action.Should().Throw<WorkflowDefinitionUnavailableException>();
    }

    private sealed class EchoWorkflow : IWorkflow<string, string>
    {
        public Task<string> RunAsync(
            WorkflowContext context,
            string input,
            CancellationToken cancellationToken) =>
            Task.FromResult(input);
    }
}
