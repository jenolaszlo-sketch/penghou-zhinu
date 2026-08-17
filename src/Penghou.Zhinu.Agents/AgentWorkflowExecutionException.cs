namespace Penghou.Zhinu.Agents;

/// <summary>
/// Thrown when a Microsoft Agent Framework workflow run inside a durable Zhinu
/// step terminates with an error.
/// </summary>
public sealed class AgentWorkflowExecutionException : Exception
{
    public AgentWorkflowExecutionException(
        string message,
        Exception? innerException = null)
        : base(message, innerException)
    {
    }
}
