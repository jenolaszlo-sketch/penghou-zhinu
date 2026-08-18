namespace Penghou.Zhinu;

/// <summary>
/// A durable dependency edge between two steps of the same run: step
/// <see cref="StepKey"/> depends on <see cref="DependsOnStepKey"/>. Restarting
/// <see cref="DependsOnStepKey"/> invalidates <see cref="StepKey"/> transitively.
/// </summary>
public sealed record StepDependency(
    string StepKey,
    string DependsOnStepKey);
