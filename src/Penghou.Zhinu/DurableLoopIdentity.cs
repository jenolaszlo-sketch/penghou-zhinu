namespace Penghou.Zhinu;

internal sealed record DurableLoopScope
{
    private const int MaximumNameLength = 128;
    private const int MaximumEncodedKeyLength = 4096;

    private DurableLoopScope(
        string name,
        DurableLoopIterationIdentity? parentIteration)
    {
        ValidateName(name, nameof(name));
        Name = name;
        ParentIteration = parentIteration;
        Depth = parentIteration is null
            ? 1
            : parentIteration.Value.Scope.Depth + 1;
        StepKeyPrefix = parentIteration is null
            ? $"$loop/{name}"
            : $"{parentIteration.Value.StepKeyPrefix}/loop/{name}";
        ValidateEncodedKey(StepKeyPrefix);
        DisplayPath = parentIteration is null
            ? name
            : $"{parentIteration.Value.DisplayPath}.{name}";
        FinalStepKey = parentIteration is null ? name : StepKeyPrefix;
    }

    public string Name { get; }

    public DurableLoopIterationIdentity? ParentIteration { get; }

    public int Depth { get; }

    public string StepKeyPrefix { get; }

    public string DisplayPath { get; }

    public string FinalStepKey { get; }

    public static DurableLoopScope Root(string name) => new(name, null);

    public DurableLoopScope Nest(
        DurableLoopIterationIdentity parentIteration,
        string name)
    {
        if (!ReferenceEquals(parentIteration.Scope, this))
        {
            throw new WorkflowStateException(
                "Nested loop parent iteration does not belong to its declaring loop scope.");
        }
        return new DurableLoopScope(name, parentIteration);
    }

    public DurableLoopIterationIdentity Iteration(int number)
    {
        if (number < 1)
            throw new ArgumentOutOfRangeException(nameof(number));
        return new DurableLoopIterationIdentity(this, number);
    }

    public static void ValidateName(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (value.Length > MaximumNameLength)
        {
            throw new ArgumentException(
                $"Durable loop names cannot exceed {MaximumNameLength} characters.",
                parameterName);
        }
        foreach (var character in value)
        {
            if (char.IsAsciiLetterOrDigit(character) ||
                character is '_' or '-' or '.')
            {
                continue;
            }
            throw new ArgumentException(
                "Durable loop names may contain only ASCII letters, digits, '_', '-', and '.'.",
                parameterName);
        }
    }

    public static string ValidateEncodedKey(string value)
    {
        if (value.Length > MaximumEncodedKeyLength)
        {
            throw new WorkflowConfigurationException(
                $"Encoded durable loop key exceeds {MaximumEncodedKeyLength} characters.");
        }
        return value;
    }
}

internal readonly record struct DurableLoopIterationIdentity(
    DurableLoopScope Scope,
    int Number)
{
    public string StepKeyPrefix => $"{Scope.StepKeyPrefix}/{Number}";

    public string DisplayPath => $"{Scope.DisplayPath}[{Number}]";
}

internal static class DurableLoopStepKeys
{
    public static string Limits(DurableLoopScope scope) =>
        DurableLoopScope.ValidateEncodedKey($"{scope.StepKeyPrefix}/limits");

    public static string Condition(DurableLoopIterationIdentity iteration) =>
        DurableLoopScope.ValidateEncodedKey(
            $"{iteration.StepKeyPrefix}/condition");

    public static string Body(
        DurableLoopIterationIdentity iteration,
        string stepName) =>
        DurableLoopScope.ValidateEncodedKey(
            $"{iteration.StepKeyPrefix}/body/{stepName}");

    public static string Commit(DurableLoopIterationIdentity iteration) =>
        DurableLoopScope.ValidateEncodedKey(
            $"{iteration.StepKeyPrefix}/commit");

    public static string Limit(DurableLoopScope scope) =>
        DurableLoopScope.ValidateEncodedKey($"{scope.StepKeyPrefix}/limit");
}
