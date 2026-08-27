namespace Penghou.Zhinu;

/// <summary>
/// Describes a keyed class-based step contract. Share one reference between
/// workflow invocation and dependency-injection registration so the
/// implementation key and input/output types cannot drift independently.
/// </summary>
public abstract record WorkflowStepReference
{
    internal WorkflowStepReference(
        StepImplementationKey implementationKey,
        Type inputType,
        Type outputType)
    {
        implementationKey.Validate(nameof(implementationKey));
        ImplementationKey = implementationKey;
        InputType = inputType ?? throw new ArgumentNullException(nameof(inputType));
        OutputType = outputType ?? throw new ArgumentNullException(nameof(outputType));
    }

    public StepImplementationKey ImplementationKey { get; }
    public Type InputType { get; }
    public Type OutputType { get; }
}

/// <summary>
/// A typed reference to a keyed class-based workflow step.
/// </summary>
public sealed record WorkflowStepReference<TInput, TOutput> :
    WorkflowStepReference
{
    public WorkflowStepReference(StepImplementationKey implementationKey)
        : base(implementationKey, typeof(TInput), typeof(TOutput)) { }
}
