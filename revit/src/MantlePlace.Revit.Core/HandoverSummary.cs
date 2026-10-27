namespace MantlePlace.Revit.Core;

/// <summary>What Prepare for handover did, or is about to do, in numbers.</summary>
/// <param name="Containers">How many ODbL containers the project holds.</param>
/// <param name="Swapped">How many of them are swapped for a Produced Work terrain already in the project.</param>
/// <param name="Elements">How many elements the containers hold.</param>
public readonly record struct HandoverCounts(int Containers, int Swapped, int Elements);

/// <summary>The words Prepare for handover says. Pure, so the wording is asserted headlessly.</summary>
public static class HandoverSummary
{
    /// <summary>What the credit step says when the project has no provenance record to credit from.</summary>
    public const string NoRecord =
        "This project has no Mantle Place provenance record, so there is no credit to add. A project "
        + "imported by this plugin carries one.";

    /// <summary>The confirmation's question.</summary>
    public static string Ask(HandoverCounts counts)
    {
        if (counts.Containers == 0)
        {
            return "This project holds no ODbL content from Mantle Place, so nothing will be removed. "
                + "The credit will still be checked.";
        }

        return $"Remove the {Plural(counts.Elements, "element")} of ODbL content in {Plural(counts.Containers, "container")}, "
            + "and make sure the data credit is in the project?"
            + (counts.Swapped > 0
                ? $" {Plural(counts.Swapped, "container")} will be swapped: the Produced Work terrain of the same order stays."
                : string.Empty)
            + " One undo step reverses it, and nothing is saved.";
    }

    /// <summary>The result, once it is done. <paramref name="creditLine"/> is what the credit step said.</summary>
    public static string Done(HandoverCounts counts, int deleted, string creditLine)
    {
        ArgumentNullException.ThrowIfNull(creditLine);

        string removed = counts.Containers == 0
            ? "This project holds no ODbL content from Mantle Place, so nothing was removed."
            : $"Removed {Plural(counts.Elements, "element")} of ODbL content from {Plural(counts.Containers, "container")}"
                + (deleted > counts.Elements
                    ? $" ({Plural(deleted, "element")} in all, with what Revit deletes along with them)."
                    : ".")
                + (counts.Swapped > 0
                    ? $" {Plural(counts.Swapped, "container")} swapped: the Produced Work terrain of the same order stays."
                    : string.Empty);

        return removed + " " + creditLine
            + " Views, materials and plans the import made are left as they are. Nothing was saved: undo reverses all of it.";
    }

    private static string Plural(int count, string noun) => count == 1 ? $"1 {noun}" : $"{count} {noun}s";
}
