using System.Security.Cryptography;
using System.Text;

namespace Penghou.Zhinu.Declarative;

internal static class WorkflowFingerprint
{
    public static string Compute(string canonicalJson)
    {
        var bytes = Encoding.UTF8.GetBytes(canonicalJson);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public static string Compute(CompiledWorkflowDefinition compiled)
    {
        var canonical = WorkflowCanonicalizer.Canonicalize(compiled);
        return Compute(canonical);
    }
}
