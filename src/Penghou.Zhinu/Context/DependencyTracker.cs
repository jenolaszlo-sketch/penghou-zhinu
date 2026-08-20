namespace Penghou.Zhinu.Context;

/// <summary>
/// Tracks durable dependencies declared via dependency scopes and resolves the
/// effective dependency set for each step claim.
/// </summary>
internal sealed class DependencyTracker
{
    private readonly List<string> currentDependencies = [];

    public IDisposable Declare(IReadOnlyList<string> stepKeys)
    {
        var added = stepKeys
            .Where(stepKey => !currentDependencies.Contains(stepKey))
            .ToList();
        currentDependencies.AddRange(added);
        return new DependencyScope(this, added);
    }

    public IReadOnlyCollection<string>? Resolve(
        IReadOnlyCollection<string>? explicitKeys)
    {
        if (currentDependencies.Count == 0)
            return explicitKeys is { Count: > 0 } ? explicitKeys : null;
        if (explicitKeys is null or { Count: 0 })
            return currentDependencies.ToArray();
        return explicitKeys
            .Concat(currentDependencies)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    private sealed class DependencyScope : IDisposable
    {
        private readonly DependencyTracker owner;
        private readonly IReadOnlyList<string> added;
        private bool disposed;

        public DependencyScope(
            DependencyTracker owner,
            IReadOnlyList<string> added)
        {
            this.owner = owner;
            this.added = added;
        }

        public void Dispose()
        {
            if (disposed)
                return;
            disposed = true;
            foreach (var stepKey in added)
                owner.currentDependencies.Remove(stepKey);
        }
    }
}
