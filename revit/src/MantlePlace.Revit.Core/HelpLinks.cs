namespace MantlePlace.Revit.Core;

/// <summary>
/// Where F1 over a Mantle Place ribbon button sends a curator.
/// </summary>
/// <remarks>
/// One constant, held in the pure core so a test can pin it; the shim hands it to every push button
/// it builds. Without it Revit answers F1 over our buttons with its own help, which knows nothing of
/// them. The page is the Revit plugin's own README in the public repository: it is the one document
/// that describes every button, and it is versioned with the code the button belongs to.
/// </remarks>
public static class HelpLinks
{
    /// <summary>The Revit plugin's public help page.</summary>
    public const string RibbonHelpUrl = "https://github.com/mantleplace/mantleplace-dcc/blob/main/revit/README.md";
}
