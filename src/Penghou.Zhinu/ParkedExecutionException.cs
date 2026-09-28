namespace Penghou.Zhinu;

/// <summary>
/// Thrown when a wait primitive parks: the worker releases capacity and the
/// run stays pending. Caught only by the execution pipeline, which releases
/// the run lease without failing. A run that completes with unconsumed parked
/// waits fails loudly instead, so user code cannot turn a swallowed park
/// into durable success.
/// </summary>
internal sealed class ParkedExecutionException : Exception
{
    public ParkedExecutionException(Guid waitId)
        : base($"Execution parked on wait '{waitId:D}'.")
    {
        WaitId = waitId;
    }

    public Guid WaitId { get; }
}
