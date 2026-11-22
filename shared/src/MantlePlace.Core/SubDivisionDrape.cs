namespace MantlePlace.Core;

public static class SubDivisionDrape
{
    public static string? Clause(int draped, int refused, IReadOnlyCollection<string> reasons)
    {
        ArgumentNullException.ThrowIfNull(reasons);
        ArgumentOutOfRangeException.ThrowIfNegative(draped);
        ArgumentOutOfRangeException.ThrowIfNegative(refused);

        if (draped == 0 && refused == 0)
        {
            return null;
        }

        List<string> parts = [];

        if (draped > 0)
        {
            parts.Add($"the photograph also covers {draped:N0} subdivision(s)");
        }

        if (refused > 0)
        {
            string clause = $"{refused:N0} subdivision(s) would not take it and show through the "
                + "photograph as untextured patches";

            clause += reasons.Count == 0
                ? " — Revit gave no reason, which is itself a defect worth reporting with this log"
                : $" — Revit said: {string.Join(" / ", reasons)}";

            parts.Add(clause);
        }

        return "; " + string.Join("; ", parts);
    }
}
