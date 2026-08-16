using System.Security.Cryptography;
using System.Text;

namespace Penghou.Zhinu;

internal static class SerializationIdentity
{
    public static string TypeId(Type type) =>
        $"{type.FullName}, {type.Assembly.GetName().Name}";

    public static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))
            .ToLowerInvariant();
}
