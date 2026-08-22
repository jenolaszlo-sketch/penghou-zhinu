using System.Reflection;
using FluentAssertions;
#pragma warning disable xUnit1051

namespace Penghou.Zhinu.Tests;

public sealed class PublicSurfaceTests
{
    private static readonly Assembly CoreAssembly = typeof(WorkflowEngine).Assembly;

    private static bool IsPublic(Type type) =>
        type.IsPublic || type.IsNestedPublic;

    [Theory]
    [InlineData("WorkflowDependencyValidator")]
    [InlineData("RunStateMachine")]
    [InlineData("StepStateMachine")]
    [InlineData("WorkflowIrArtifact")]
    [InlineData("WorkflowIrState")]
    [InlineData("WorkflowIrTransition")]
    [InlineData("ActivityReference")]
    public void ImplementationOnlyTypes_AreNotPublic(string typeName)
    {
        var type = CoreAssembly.GetType($"Penghou.Zhinu.{typeName}")
            ?? CoreAssembly.GetType($"Penghou.Zhinu.Ir.{typeName}");
        type.Should().NotBeNull($"expected internal type {typeName}");
        IsPublic(type!).Should().BeFalse($"{typeName} should be internal, not public API");
    }

    [Theory]
    [InlineData("WorkflowEngine")]
    [InlineData("WorkflowContext")]
    [InlineData("WorkflowHandle`1")]
    [InlineData("IWorkflowStore")]
    [InlineData("WorkflowStatus")]
    [InlineData("ZhinuException")]
    [InlineData("WorkflowStateException")]
    [InlineData("WorkflowNotFoundException")]
    [InlineData("LeaseLostException")]
    public void ConsumerTypes_RemainPublic(string typeName)
    {
        var type = CoreAssembly.GetType($"Penghou.Zhinu.{typeName}");
        type.Should().NotBeNull($"expected public type {typeName}");
        IsPublic(type!).Should().BeTrue($"{typeName} is consumer API and must stay public");
    }
}
