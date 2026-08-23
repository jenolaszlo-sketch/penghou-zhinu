namespace Penghou.Zhinu.Declarative;

internal static class ActivityContractIdentity
{
    public static string Create(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        if (type.ContainsGenericParameters)
        {
            throw new ArgumentException(
                "Activity contracts must use closed CLR types.",
                nameof(type));
        }

        if (type.IsArray)
        {
            var rank = type.GetArrayRank();
            return $"{Create(type.GetElementType()!)}[{new string(',', rank - 1)}]";
        }

        if (type.IsGenericType)
        {
            var definition = type.GetGenericTypeDefinition();
            var arguments = string.Join(
                ",",
                type.GetGenericArguments().Select(Create));
            return $"{NamedTypeId(definition)}<{arguments}>";
        }

        return NamedTypeId(type);
    }

    private static string NamedTypeId(Type type) =>
        $"{type.FullName}, {type.Assembly.GetName().Name}";
}
