namespace MantlePlace.Core;

public static class AddinFaults
{
    private const string OwnNamespacePrefix = "MantlePlace.";

    public static bool IsOurs(IReadOnlyList<string?> declaringTypeNames)
    {
        ArgumentNullException.ThrowIfNull(declaringTypeNames);

        for (int i = 0; i < declaringTypeNames.Count; i++)
        {
            string? name = declaringTypeNames[i];

            if (name is not null && name.StartsWith(OwnNamespacePrefix, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
