namespace Penghou.Zhinu.Context;

/// <summary>
/// Tracks durable dependencies declared via dependency scopes and resolves the
/// effective dependency set for each step claim. Scopes are immutable
/// snapshots isolated by async execution flow: sibling branches each observe
/// their own declarations plus inherited parents, and disposal restores
/// exactly the prior lexical state.
/// </summary>
internal sealed class DependencyTracker
{
    private readonly AsyncLocal<string[]?> current = new();

    public IDisposable Declare(IReadOnlyList<string> stepKeys)
    {
        var previous = current.Value;
        current.Value = previous is null or { Length: 0 }
            ? Deduplicate(stepKeys)
            : previous.Concat(stepKeys).Distinct(StringComparer.Ordinal).ToArray();
        return new DependencyScope(this, previous);
    }

    public IReadOnlyCollection<string>? Resolve(
        IReadOnlyCollection<string>? explicitKeys)
    {
        var ambient = current.Value;
        if (ambient is null or { Length: 0 })
            return explicitKeys is { Count: > 0 } ? explicitKeys : null;
        if (explicitKeys is null or { Count: 0 })
            return ambient.ToArray();
        return explicitKeys
            .Concat(ambient)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    private static string[] Deduplicate(IReadOnlyList<string> stepKeys) =>
        stepKeys.Distinct(StringComparer.Ordinal).ToArray();

    private sealed class DependencyScope : IDisposable
    {
        private readonly DependencyTracker owner;
        private readonly string[]? previous;
        private bool disposed;

        public DependencyScope(DependencyTracker owner, string[]? previous)
        {
            this.owner = owner;
            this.previous = previous;
        }

        public void Dispose()
        {
            if (disposed)
                return;
            disposed = true;
            owner.current.Value = previous;
        }
    }
}
