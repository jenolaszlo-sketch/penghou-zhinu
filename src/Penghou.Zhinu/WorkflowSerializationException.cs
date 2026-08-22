namespace Penghou.Zhinu;

public sealed class WorkflowSerializationException(
    string message,
    Exception? innerException = null)
    : ZhinuException(message, innerException ?? new Exception());
