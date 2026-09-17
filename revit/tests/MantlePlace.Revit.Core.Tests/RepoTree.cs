namespace MantlePlace.Revit.Core.Tests;

/// <summary>
/// Finds a checked-in directory by climbing out of the test assembly's output folder.
/// </summary>
/// <remarks>
/// The suite reads real files in two places — the shared conformance corpus and the add-in's
/// committed renders — and neither is copied to <c>bin/</c>. Where <c>bin/</c> sits relative to the
/// repository root depends on the target framework and the configuration, and this project builds
/// two frameworks, so the number of levels to climb is not a constant anybody should be writing
/// down twice.
/// </remarks>
internal static class RepoTree
{
    /// <summary>Directories to climb before giving up.</summary>
    private const int MaxWalkUp = 10;

    /// <summary>
    /// The first existing directory at <paramref name="relativePath"/> under an ancestor of the test
    /// assembly, or <c>null</c>.
    /// </summary>
    /// <param name="relativePath">A repository-root-relative path, spelled with forward slashes.</param>
    /// <param name="accept">
    /// An extra test the candidate must pass — the seam for "the directory exists AND has the file
    /// that proves it is the right one". Omitted, existing is enough.
    /// </param>
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
