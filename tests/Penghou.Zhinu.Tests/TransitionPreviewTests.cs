using FluentAssertions;
using Penghou.Zhinu;

namespace Penghou.Zhinu.Tests;

/// <summary>
/// Transition previews classify nodes, compute reuse for completed identical
/// steps, invalidate changed nodes transitively, and flag running work for
/// cancellation. Pure calculation, no store involved.
/// </summary>
public sealed class TransitionPreviewTests
{
    [Fact]
    public void IdenticalPlans_ReuseCompleted()
    {
        var current = new[]
        {
            Step("a", StepStatus.Completed, "h1", null),
            Step("b", StepStatus.Completed, "h2", null)
        };
        var candidate = new[]
        {
            new CandidateStep { StepKey = "a", InputHash = "h1", DependsOn = [] },
            new CandidateStep { StepKey = "b", InputHash = "h2", DependsOn = ["a"] }
        };

        var preview = TransitionPreview.Calculate(current, Edges(("b", "a")), candidate);

        preview.Nodes.Select(n => n.Transition).Should()
            .OnlyContain(t => t == PlanNodeTransition.Unchanged);
        preview.ReusableSteps.Should().Equal("a", "b");
        preview.InvalidatedSteps.Should().BeEmpty();
        preview.CancelledSteps.Should().BeEmpty();
        preview.AddedSteps.Should().BeEmpty();
    }

    [Fact]
    public void ChangedInputs_InvalidateTransitively()
    {
        var current = new[]
        {
            Step("a", StepStatus.Completed, "h1", null),
            Step("b", StepStatus.Completed, "h2", null),
            Step("c", StepStatus.Completed, "h3", null)
        };
        var candidate = new[]
        {
            new CandidateStep { StepKey = "a", InputHash = "h1x", DependsOn = [] },
            new CandidateStep { StepKey = "b", InputHash = "h2", DependsOn = ["a"] },
            new CandidateStep { StepKey = "c", InputHash = "h3", DependsOn = ["b"] }
        };

        var preview = TransitionPreview.Calculate(current, Edges(("b", "a"), ("c", "b")), candidate);

        preview.Nodes.Single(n => n.StepKey == "a").Transition
            .Should().Be(PlanNodeTransition.ChangedInputs);
        preview.Nodes.Single(n => n.StepKey == "b").Transition
            .Should().Be(PlanNodeTransition.Unchanged);
        preview.InvalidatedSteps.Should().Equal("a", "b", "c");
        preview.ReusableSteps.Should().BeEmpty();
    }

    [Fact]
    public void AddedRemoved_ReportedSeparately()
    {
        var current = new[]
        {
            Step("gone", StepStatus.Completed, "h0", null),
            Step("kept", StepStatus.Completed, "h1", null)
        };
        var candidate = new[]
        {
            new CandidateStep { StepKey = "kept", InputHash = "h1", DependsOn = [] },
            new CandidateStep { StepKey = "fresh", InputHash = "h9", DependsOn = [] }
        };

        var preview = TransitionPreview.Calculate(current, Edges(), candidate);

        preview.AddedSteps.Should().Equal("fresh");
        preview.InvalidatedSteps.Should().Equal("gone");
        preview.ReusableSteps.Should().Equal("kept");
    }

    [Fact]
    public void RunningInvalidated_FlaggedForCancellation()
    {
        var current = new[]
        {
            Step("a", StepStatus.Running, "h1", null),
            Step("b", StepStatus.Pending, "h2", null),
            Step("c", StepStatus.Completed, "h3", null)
        };
        var candidate = new[]
        {
            new CandidateStep { StepKey = "a", InputHash = "h1x", DependsOn = [] },
            new CandidateStep { StepKey = "b", InputHash = "h2", DependsOn = ["a"] },
            new CandidateStep { StepKey = "c", InputHash = "h3", DependsOn = [] }
        };

        var preview = TransitionPreview.Calculate(current, Edges(("b", "a")), candidate);

        preview.CancelledSteps.Should().Equal("a");
        preview.InvalidatedSteps.Should().Equal("a", "b");
        preview.ReusableSteps.Should().Equal("c");
    }

    [Fact]
    public void ImplementationAndDependencyChanges_Distinguished()
    {
        var current = new[]
        {
            Step("impl", StepStatus.Completed, "h1", "i1"),
            Step("deps", StepStatus.Completed, "h2", null)
        };
        var candidate = new[]
        {
            new CandidateStep { StepKey = "impl", InputHash = "h1", ImplementationKey = "i2", DependsOn = [] },
            new CandidateStep { StepKey = "deps", InputHash = "h2", DependsOn = [] }
        };

        var preview = TransitionPreview.Calculate(current, Edges(("deps", "impl")), candidate);

        preview.Nodes.Single(n => n.StepKey == "impl").Transition
            .Should().Be(PlanNodeTransition.ChangedImplementation);
        preview.Nodes.Single(n => n.StepKey == "deps").Transition
            .Should().Be(PlanNodeTransition.ChangedDependencies);
    }

    [Fact]
    public void DuplicateKeys_Rejected()
    {
        var current = new[] { Step("a", StepStatus.Completed, "h1", null) };
        var candidate = new[]
        {
            new CandidateStep { StepKey = "a", InputHash = "h1", DependsOn = [] },
            new CandidateStep { StepKey = "a", InputHash = "h2", DependsOn = [] }
        };

        var act = () => TransitionPreview.Calculate(current, Edges(), candidate);

        act.Should().Throw<ArgumentException>();
    }

    private static WorkflowStepRun Step(
        string key, StepStatus status, string? hash, string? implementation) =>
        new()
        {
            Id = Guid.NewGuid(),
            WorkflowRunId = Guid.NewGuid(),
            StepKey = key,
            Status = status,
            Attempt = 1,
            CreatedAt = DateTimeOffset.UtcNow,
            InputHash = hash,
            ImplementationKey = implementation
        };

    private static IReadOnlyList<StepDependency> Edges(params (string Step, string DependsOn)[] edges) =>
        edges.Select(edge => new StepDependency(edge.Step, edge.DependsOn)).ToList();
}
