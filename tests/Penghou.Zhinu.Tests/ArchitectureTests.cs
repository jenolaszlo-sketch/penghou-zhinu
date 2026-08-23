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

    [Fact]
    public void DeclarativeAdapter_DependsOnlyOnPublicRuntimeContracts()
    {
        // The P3 execution adapter must not reach into WorkflowContext internals,
        // the execution pipeline, or any persistence implementation.
        var adapter = Core.GetType("Penghou.Zhinu.Declarative.DeclarativeWorkflow", throwOnError: false);
        adapter.Should().NotBeNull("DeclarativeWorkflow must exist");

        var forbiddenNamespaces = new[]
        {
            "Penghou.Zhinu.Context",
            "Penghou.Zhinu.Execution",
            "Penghou.Zhinu.Sqlite",
            "Microsoft.Data.Sqlite"
        };
        var dependencies = new HashSet<string>(StringComparer.Ordinal);
        foreach (var field in adapter!.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            Collect(field.FieldType, dependencies);
        foreach (var ctor in adapter.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            foreach (var p in ctor.GetParameters())
                Collect(p.ParameterType, dependencies);
        foreach (var method in adapter.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly))
        {
            Collect(method.ReturnType, dependencies);
            foreach (var p in method.GetParameters())
                Collect(p.ParameterType, dependencies);
        }
        foreach (var iface in adapter.GetInterfaces())
            Collect(iface, dependencies);

        var leaks = dependencies
            .Where(ns => forbiddenNamespaces.Any(f => ns.StartsWith(f, StringComparison.Ordinal)))
            .ToArray();
        leaks.Should().BeEmpty("DeclarativeWorkflow must not depend on runtime internals or the store");
    }

    private static void Collect(Type type, ISet<string> into)
    {
        var ns = type.Namespace ?? string.Empty;
        into.Add(ns);
        if (type.IsGenericType)
            foreach (var arg in type.GetGenericArguments())
                Collect(arg, into);
        if (type.BaseType is { } baseType)
            into.Add(baseType.Namespace ?? string.Empty);
    }
}
