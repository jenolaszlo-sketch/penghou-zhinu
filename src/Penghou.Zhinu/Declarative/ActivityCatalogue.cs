namespace Penghou.Zhinu.Declarative;

public sealed class ActivityCatalogue : IActivityCatalogue
{
    private readonly Dictionary<ActivityReference, (ActivityDescriptor Descriptor, IActivityExecutor Executor)> entries = new();

    public void Register<TInput, TOutput>(ActivityReference reference, IActivity<TInput, TOutput> implementation)
    {
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentNullException.ThrowIfNull(implementation);
        if (entries.ContainsKey(reference))
            throw new InvalidOperationException($"Activity '{reference}' is already registered.");

        var descriptor = new ActivityDescriptor
        {
            Reference = reference,
            Input = new ActivityContract { TypeId = ActivityContractIdentity.Create(typeof(TInput)) },
            Output = new ActivityContract { TypeId = ActivityContractIdentity.Create(typeof(TOutput)) }
        };
        entries[reference] = (descriptor, new ActivityExecutor<TInput, TOutput>(implementation));
    }

    public ActivityDescriptor GetDescriptor(ActivityReference reference)
    {
        if (TryGetDescriptor(reference, out var descriptor))
            return descriptor;
        throw new KeyNotFoundException($"Activity '{reference}' is not registered.");
    }

    public bool TryGetDescriptor(ActivityReference reference, out ActivityDescriptor descriptor)
    {
        if (entries.TryGetValue(reference, out var entry))
        {
            descriptor = entry.Descriptor;
            return true;
        }
        descriptor = null!;
        return false;
    }

    internal IActivityExecutor Resolve(ActivityReference reference)
    {
        if (entries.TryGetValue(reference, out var entry))
            return entry.Executor;
        throw new KeyNotFoundException($"Activity '{reference}' is not registered.");
    }

    IActivityExecutor IActivityCatalogue.Resolve(ActivityReference reference) =>
        Resolve(reference);

    public IReadOnlyList<ActivityDescriptor> ListDescriptors() => entries.Values.Select(e => e.Descriptor).ToList();
}
