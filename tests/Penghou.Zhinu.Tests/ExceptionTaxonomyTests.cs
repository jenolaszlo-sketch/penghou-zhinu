using FluentAssertions;
#pragma warning disable xUnit1051

namespace Penghou.Zhinu.Tests;

public sealed class ExceptionTaxonomyTests
{
    [Fact]
    public void AllDomainExceptions_DeriveFromZhinuException()
    {
        var error = new WorkflowError
        {
            Type = typeof(Exception).FullName!,
            Message = "boom",
            Timestamp = DateTimeOffset.UtcNow
        };
        Exception[] exceptions =
        [
            new WorkflowStateException("x"),
            new WorkflowNotFoundException("x"),
            new WorkflowConcurrencyException("x"),
            new WorkflowLeaseException("x"),
            new LeaseLostException("x"),
            new WorkflowSerializationException("x"),
            new WorkflowDefinitionUnavailableException("n", "v"),
            new WorkflowEventPublisherException("x", new Exception()),
            new RollbackFailedException("x"),
            new WorkflowExecutionFailedException(Guid.NewGuid(), error),
            new WorkflowStepFailedException("s", error)
        ];
        exceptions.Should().OnlyContain(ex => ex is ZhinuException);
    }

    [Fact]
    public void LeaseLost_IsConcurrencyAndState()
    {
        var ex = new LeaseLostException("x");
        ex.Should().BeAssignableTo<WorkflowLeaseException>();
        ex.Should().BeAssignableTo<WorkflowConcurrencyException>();
        ex.Should().BeAssignableTo<WorkflowStateException>();
        ex.Should().BeAssignableTo<ZhinuException>();
    }

    [Fact]
    public void WorkflowNotFound_IsStateException()
    {
        var ex = new WorkflowNotFoundException("x");
        ex.Should().BeAssignableTo<WorkflowStateException>();
        ex.Should().BeAssignableTo<ZhinuException>();
    }

    [Fact]
    public void DefinitionUnavailable_IsDefinitionException()
    {
        var ex = new WorkflowDefinitionUnavailableException("n", "v");
        ex.Should().BeAssignableTo<WorkflowDefinitionException>();
        ex.Should().BeAssignableTo<ZhinuException>();
    }

    [Fact]
    public void CatchWorkflowState_CoversLeaseAndNotFound()
    {
        // Consumers can catch WorkflowStateException and still see fencing/not-found errors.
        try
        {
            throw new LeaseLostException("stale generation");
        }
        catch (WorkflowStateException stateException)
        {
            stateException.Should().BeOfType<LeaseLostException>();
        }
    }
}
