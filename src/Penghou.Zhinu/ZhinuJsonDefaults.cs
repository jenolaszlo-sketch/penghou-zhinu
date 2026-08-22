using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Penghou.Zhinu;

/// <summary>Centralized JSON defaults for workflow serialization.</summary>
public static class ZhinuJsonDefaults
{
    public static JsonSerializerOptions CreateDefault()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter());
        options.TypeInfoResolver ??= new DefaultJsonTypeInfoResolver();
        options.MakeReadOnly();
        return options;
    }

    public static JsonSerializerOptions CloneAndFreeze(JsonSerializerOptions source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var clone = new JsonSerializerOptions(source);
        clone.TypeInfoResolver ??= new DefaultJsonTypeInfoResolver();
        clone.MakeReadOnly();
        return clone;
    }
}
