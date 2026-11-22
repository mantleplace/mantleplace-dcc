namespace MantlePlace.Core.Tests;

internal static class RepoTree
{
    private const int MaxWalkUp = 10;

    internal static string? Find(string relativePath, Func<string, bool>? accept = null)
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        for (int i = 0; i < MaxWalkUp && dir is not null; i++)
        {
            string candidate = Path.Combine(
                dir.FullName,
                relativePath.Replace('/', Path.DirectorySeparatorChar));

            if (Directory.Exists(candidate) && (accept is null || accept(candidate)))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        return null;
    }
}
