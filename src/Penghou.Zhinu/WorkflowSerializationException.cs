namespace Penghou.Zhinu;

public sealed class WorkflowSerializationException(
    string message,
    Exception? innerException = null)
    : Exception(message, innerException);
