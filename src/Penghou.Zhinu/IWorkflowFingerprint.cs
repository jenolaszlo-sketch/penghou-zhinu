namespace Penghou.Zhinu;

/// <summary>Implemented by a workflow that carries a deterministic definition fingerprint (for example a compiled declarative definition).</summary>
public interface IWorkflowFingerprint
{
    string Fingerprint { get; }
}
