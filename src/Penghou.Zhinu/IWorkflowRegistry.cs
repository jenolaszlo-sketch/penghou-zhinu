namespace Penghou.Zhinu;

/// <summary>Resolves available workflow code by its durable name and version.</summary>
public interface IWorkflowRegistry
{
    IWorkflowRegistration Get(string name, string version);

    bool TryGet(
        string name,
        string version,
        out IWorkflowRegistration? registration);
}
