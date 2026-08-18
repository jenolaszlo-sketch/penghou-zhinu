namespace Penghou.Zhinu;

/// <summary>
/// Thrown when a worker attempts a state transition using a lease generation
/// that no longer matches the run's current generation, typically because the
/// run was restarted or re-claimed after the worker acquired its lease. The
/// worker is fenced out and must not commit.
/// </summary>
public sealed class LeaseLostException : WorkflowStateException
{
    public LeaseLostException(string message) : base(message)
    {
    }
}
