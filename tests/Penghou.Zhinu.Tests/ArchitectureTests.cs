using System.Reflection;
using FluentAssertions;
#pragma warning disable xUnit1051

namespace Penghou.Zhinu.Tests;

public sealed class ArchitectureTests
{
    private static readonly Assembly Core = typeof(WorkflowEngine).Assembly;

    private static Assembly Load(string simpleName) =>
        Assembly.Load(new AssemblyName(simpleName));

    private static bool References(Assembly assembly, string simpleName) =>
        assembly.GetReferencedAssemblies().Any(r => r.Name == simpleName);

    [Fact]
    public void Core_DoesNotReferenceSqliteOrItsProvider()
    {
        References(Core, "Penghou.Zhinu.Sqlite").Should().BeFalse();
        References(Core, "Microsoft.Data.Sqlite").Should().BeFalse();
    }

    [Fact]
    public void Core_DoesNotReferenceHostingOrAgents()
    {
        References(Core, "Penghou.Zhinu.Hosting").Should().BeFalse();
        References(Core, "Penghou.Zhinu.Agents").Should().BeFalse();
    }

    [Fact]
    public void CoreAndHosting_DoNotReferenceAspNetCore()
    {
        References(Core, "Microsoft.AspNetCore.App").Should().BeFalse();
        References(Load("Penghou.Zhinu.Hosting"), "Microsoft.AspNetCore.App").Should().BeFalse();
    }

    [Fact]
    public void AspNetCoreEndpoints_DependOnlyOnCore()
    {
        var endpoints = Load("Penghou.Zhinu.Hosting.AspNetCore");
        References(endpoints, "Penghou.Zhinu").Should().BeTrue();
        // The endpoints package must not know about the SQLite provider or Hosting.
        References(endpoints, "Penghou.Zhinu.Sqlite").Should().BeFalse();
        References(endpoints, "Microsoft.Data.Sqlite").Should().BeFalse();
        References(endpoints, "Penghou.Zhinu.Hosting").Should().BeFalse();
    }

    [Fact]
    public void Sqlite_ReferencesCoreOnlyForStoreContract()
    {
        var sqlite = Load("Penghou.Zhinu.Sqlite");
        References(sqlite, "Penghou.Zhinu").Should().BeTrue();
        References(sqlite, "Penghou.Zhinu.Hosting").Should().BeFalse();
        References(sqlite, "Penghou.Zhinu.Agents").Should().BeFalse();
    }

    [Fact]
    public void Testing_DoesNotReferenceHostingOrAgents()
    {
        var testing = Load("Penghou.Zhinu.Testing");
        References(testing, "Penghou.Zhinu.Hosting").Should().BeFalse();
        References(testing, "Penghou.Zhinu.Agents").Should().BeFalse();
    }

    [Fact]
    public void Hosting_DoesNotReferenceSqliteProviderDirectly()
    {
        var hosting = Load("Penghou.Zhinu.Hosting");
        References(hosting, "Microsoft.Data.Sqlite").Should().BeFalse();
    }
}
