namespace Penghou.Zhinu;

public sealed class WorkflowDefinitionUnavailableException(
    string name,
    string version)
    : WorkflowDefinitionException($"Workflow '{name}' version '{version}' is not registered.");
