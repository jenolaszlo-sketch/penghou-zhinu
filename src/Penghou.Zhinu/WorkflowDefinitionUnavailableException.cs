namespace Penghou.Zhinu;

public sealed class WorkflowDefinitionUnavailableException(
    string name,
    string version)
    : Exception($"Workflow '{name}' version '{version}' is not registered.");
