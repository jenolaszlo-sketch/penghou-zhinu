namespace Penghou.Zhinu.Declarative;

/// <summary>Deterministic catalogue of activity descriptors and their implementations.</summary>
internal interface IActivityCatalogue
{
    void Register<TInput, TOutput>(ActivityReference reference, IActivity<TInput, TOutput> implementation);
    ActivityDescriptor GetDescriptor(ActivityReference reference);
    IActivityExecutor Resolve(ActivityReference reference);
    IReadOnlyList<ActivityDescriptor> ListDescriptors();
    bool TryGetDescriptor(ActivityReference reference, out ActivityDescriptor descriptor);
}
