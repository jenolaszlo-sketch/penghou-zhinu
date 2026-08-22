using System.Text.Json;

namespace Penghou.Zhinu.Context;

/// <summary>
/// Serializes and deserializes durable step results with stable error
/// translation, shared by the step context and child-run coordinator.
/// </summary>
internal static class StepResultSerializer
{
    public static T Deserialize<T>(
        string? json,
        string expectedType,
        JsonSerializerOptions serializerOptions)
    {
        if (json is null)
        {
            if (default(T) is not null)
                throw new WorkflowSerializationException(
                    $"Stored step result '{expectedType}' was null.");
            return default!;
        }
        try
        {
            var value = JsonSerializer.Deserialize<T>(json, serializerOptions);
            if (value is null && default(T) is not null)
            {
                throw new WorkflowSerializationException(
                    $"Stored step result '{expectedType}' was null.");
            }
            return value!;
        }
        catch (JsonException exception)
        {
            throw new WorkflowSerializationException(
                $"Stored step result could not be deserialized as '{expectedType}'.",
                exception);
        }
    }
}
