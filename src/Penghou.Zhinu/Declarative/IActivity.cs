namespace Penghou.Zhinu.Declarative;

/// <summary>Executable implementation of an activity. Separated from its descriptor.</summary>
public interface IActivity<TInput, TOutput>
{
    Task<TOutput> ExecuteAsync(TInput input, CancellationToken cancellationToken);
}

/// <summary>Untyped execution entry used by the catalogue to invoke activities without knowing TInput/TOutput at compile time.</summary>
internal interface IActivityExecutor
{
    Task<object?> ExecuteAsync(object? input, CancellationToken cancellationToken);
    Type InputType { get; }
    Type OutputType { get; }
}

internal sealed class ActivityExecutor<TInput, TOutput> : IActivityExecutor
{
    private readonly IActivity<TInput, TOutput> inner;

    public ActivityExecutor(IActivity<TInput, TOutput> inner) => this.inner = inner;

    public Type InputType => typeof(TInput);
    public Type OutputType => typeof(TOutput);

    public async Task<object?> ExecuteAsync(object? input, CancellationToken cancellationToken)
    {
        var typedInput = (TInput)input!;
        var result = await inner.ExecuteAsync(typedInput, cancellationToken);
        return result;
    }
}
