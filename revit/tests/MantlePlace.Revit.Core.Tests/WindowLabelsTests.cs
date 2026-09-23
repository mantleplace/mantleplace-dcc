namespace MantlePlace.Revit.Core.Tests;

/// <summary>
/// What the Mantle Place windows say, and the one colour the brand is allowed to spend on
/// them, including the slot their header gives the mark. Which render fills a slot of that size is
/// <see cref="RibbonImageryTests"/>, whose rule the header shares with the ribbon.
/// </summary>
/// <remarks>
/// <para>
/// These are strings and numbers, so they live in the pure core and are asserted here rather than
/// read off a screenshot (<c>HPS-02</c>, <c>HPS-42</c>). The shim's remaining job is to copy them
/// onto a <c>Window</c>, a <c>Button</c> and an <c>Image</c>, which is the part no test can reach —
/// and the part CI never even builds.
/// </para>
/// <para>
/// The casing cases are not decoration. Revit's windows sat at <c>Prepare for Revit</c> beside
/// <c>Remove download</c>, one Title Case and one sentence case, in the same button row. The
/// shared-word cases are not decoration either: <c>Vault</c> and <c>Sign In</c> are fixed across
/// hosts by <c>HPS-51</c>, and this is the half of that rule a test can hold.
/// </para>
/// </remarks>
internal static class WindowLabelsTests
{
    /// <summary>Every button face the windows show, so a new one cannot skip the casing rule.</summary>
    private static readonly string[] EveryButtonFace =
    [
        WindowLabels.Refresh,
        WindowLabels.PrepareForRevit,
        WindowLabels.Import,
        WindowLabels.RemoveDownload,
        WindowLabels.Cancel,
        WindowLabels.Close,
    ];

    internal static int Run()
    {
        TestRun run = new();

        run.Case("the window titles name the product and the purpose, with no em dash", () =>
        {
            run.Equal(WindowLabels.VaultWindowTitle, "Mantle Place Vault", "the vault window's title bar");
            run.Equal(WindowLabels.SignInWindowTitle, "Mantle Place Sign In", "the sign-in window's title bar");
            run.Equal(WindowLabels.ImportWindowTitle, "Mantle Place Bundle Import", "the import window's title bar");

            foreach (string title in new[] { WindowLabels.VaultWindowTitle, WindowLabels.SignInWindowTitle, WindowLabels.ImportWindowTitle })
            {
                run.False(title.Contains('—', StringComparison.Ordinal), $"\"{title}\" carries no em dash");
                run.True(title.StartsWith("Mantle Place ", StringComparison.Ordinal), $"\"{title}\" leads with the product");
            }
        });

        run.Case("each header names the window's purpose, not the product again", () =>
        {
            // The mark is beside it and the title bar is above it, so repeating "Mantle Place" here
            // would be the third time in one window.
            run.Equal(WindowLabels.VaultHeading, "Vault", "the vault window's heading");
            run.Equal(WindowLabels.SignInHeading, "Sign In", "the sign-in window's heading");
            run.Equal(WindowLabels.ImportHeading, "Bundle Import", "the import window's heading");

            foreach (string heading in new[] { WindowLabels.VaultHeading, WindowLabels.SignInHeading, WindowLabels.ImportHeading })
            {
                run.False(
                    heading.Contains("Mantle Place", StringComparison.Ordinal),
                    $"\"{heading}\" does not repeat the product name");
            }
        });

        run.Case("the sign-in heading is the ribbon's word for the same action", () =>
        {
            // One vocabulary across the plugin: the ribbon face the curator clicked and the window
            // it opened may not disagree about what the action is called.
            run.Equal(WindowLabels.SignInHeading, AccountRibbon.SignInFace, "window heading matches the ribbon face");
        });

        run.Case("every button face is Title Case", () =>
        {
            foreach (string face in EveryButtonFace)
            {
                foreach (string word in face.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                {
                    // "for" is the one lower-case word Title Case keeps lower: a short preposition
                    // inside the phrase, never at its start.
                    bool minorWord = string.Equals(word, "for", StringComparison.Ordinal);
                    bool leads = face.StartsWith(word, StringComparison.Ordinal);

                    run.Equal(
                        char.IsUpper(word[0]),
                        !minorWord || leads,
                        $"\"{word}\" in \"{face}\" is cased for a Revit button face");
                }
            }
        });

        run.Case("the faces are the ones the issue named, spelled exactly", () =>
        {
            run.Equal(WindowLabels.Refresh, "Refresh", "the refresh face");
            run.Equal(WindowLabels.PrepareForRevit, "Prepare for Revit", "the prepare face");
            run.Equal(WindowLabels.Import, "Import", "the import face");
            run.Equal(WindowLabels.RemoveDownload, "Remove Download", "the remove face");
            run.Equal(WindowLabels.Cancel, "Cancel", "the cancel face");
            run.Equal(WindowLabels.Close, "Close", "the close face");
        });

        run.Case("no two buttons in one row say the same thing", () =>
        {
            run.Equal(
                new HashSet<string>(EveryButtonFace, StringComparer.OrdinalIgnoreCase).Count,
                EveryButtonFace.Length,
                "every face is distinct");
        });

        run.Case("the header's slot is a real slot, and takes a real render at every scale", () =>
        {
            run.Equal(MarkRenders.HeaderSlotPixels, 32, "the slot beside a window heading");

            // The window half of the rule RibbonImageryTests pins for the ribbon: a header on a 200%
            // laptop must reach the 64 px render, or the mark beside the heading is a blurred square.
            run.Equal(MarkRenders.FileNameFor(MarkRenders.HeaderSlotPixels, 1.0), "MantlePlaceMark_32.png", "100%");
            run.Equal(MarkRenders.FileNameFor(MarkRenders.HeaderSlotPixels, 1.5), "MantlePlaceMark_48.png", "150%");
            run.Equal(MarkRenders.FileNameFor(MarkRenders.HeaderSlotPixels, 2.0), "MantlePlaceMark_64.png", "200%");
        });

        run.Case("the brand orange is the mark's own orange", () =>
        {
            // The tile the renderer mattes the monogram out of is 0xFF7110, and the reference host
            // names the same value Mantle(). One colour, one spelling, both hosts.
            run.Equal(BrandPalette.Mantle.Hex, "#FF7110", "the primary action's fill");
            run.Equal(BrandPalette.OnMantle.Hex, "#FFFFFF", "what is legible on it, as the mark does it");
        });

        run.Case("the hover and press states brighten the fill without leaving the hue", () =>
        {
            BrandColour hover = BrandPalette.MixToWhite(BrandPalette.Mantle, 0.14);
            BrandColour pressed = BrandPalette.MixToWhite(BrandPalette.Mantle, 0.24);

            // Each channel moves toward white by the same fraction, so the three keep their order
            // and the button never jumps to a different brand colour under the cursor.
            run.Equal(hover.R, (byte)0xFF, "red is already at white and stays");
            run.Equal(hover.G, (byte)0x85, "green lifts by 14% of its distance to white");
            run.Equal(hover.B, (byte)0x31, "blue lifts by 14% of its distance to white");
            run.True(pressed.G > hover.G, "press goes further than hover");
            run.True(pressed.B > hover.B, "press goes further than hover on every channel");
        });

        run.Case("mixing all the way to white, or not at all, is exact", () =>
        {
            run.Equal(BrandPalette.MixToWhite(BrandPalette.Mantle, 0).Hex, "#FF7110", "no mix is the colour itself");
            run.Equal(BrandPalette.MixToWhite(BrandPalette.Mantle, 1).Hex, "#FFFFFF", "a full mix is white");

            // A caller handing this a fraction out of range gets the nearest end rather than a
            // wrapped byte: this runs on a dispatcher where a throw takes Revit down.
            run.Equal(BrandPalette.MixToWhite(BrandPalette.Mantle, -5).Hex, "#FF7110", "below zero clamps");
            run.Equal(BrandPalette.MixToWhite(BrandPalette.Mantle, 5).Hex, "#FFFFFF", "above one clamps");
            run.Equal(BrandPalette.MixToWhite(BrandPalette.Mantle, double.NaN).Hex, "#FF7110", "NaN is no mix");
        });

        run.Case("the import window says HPS-51's words for a step's state", () =>
        {
            // The standard's row for the import window fixes these, because the reference host will
            // show the same surface. Revit's half is only the casing.
            run.Equal(WindowLabels.StateWord(ImportStepState.Waiting), "Waiting", "not reached");
            run.Equal(WindowLabels.StateWord(ImportStepState.Importing), "Importing", "in flight");
            run.Equal(WindowLabels.StateWord(ImportStepState.Done), "Done", "finished");
            run.Equal(WindowLabels.StateWord(ImportStepState.Failed), "Failed", "failed");
            run.Equal(WindowLabels.StateWord(ImportStepState.Cancelled), "Cancelled", "stopped partway");
            run.Equal(WindowLabels.StateWord(ImportStepState.NotRun), "Not Run", "never started");
        });

        run.Case("every step kind and every state has its own word", () =>
        {
            foreach (ImportStepKind kind in Enum.GetValues<ImportStepKind>())
            {
                run.False(
                    string.Equals(WindowLabels.StepName(kind), kind.ToString(), StringComparison.Ordinal),
                    $"{kind} has a curator's name rather than the planner's");
            }

            ImportStepState[] states = Enum.GetValues<ImportStepState>();
            run.Equal(
                new HashSet<string>(states.Select(WindowLabels.StateWord), StringComparer.Ordinal).Count,
                states.Length,
                "no two states read the same");
        });

        run.Case("the step names are the glossary's", () =>
        {
            // Three planner kinds build the one terrain, so they are one word to the curator.
            run.Equal(WindowLabels.StepName(ImportStepKind.ToposurfaceFromSurfaceTin), "Terrain", "the TIN path");
            run.Equal(WindowLabels.StepName(ImportStepKind.ToposurfaceFromPointsFile), "Terrain", "the points path");
            run.Equal(WindowLabels.StepName(ImportStepKind.LinkSiteIfc), "Site Model", "the IFC");
            run.Equal(WindowLabels.StepName(ImportStepKind.ContextBuildings), "Context Buildings", "what the site model's buildings become");
            run.Equal(WindowLabels.StepName(ImportStepKind.SiteBoundaries), "Land Use Subdivisions", "the land-use polygons, by what they become");
            run.Equal(WindowLabels.StepName(ImportStepKind.LandCover), "Land Cover Subdivisions", "the land-cover polygons, told apart from the land use");
            run.Equal(WindowLabels.StepName(ImportStepKind.Vegetation), "Planting", "the tree points, of every foliage type");
            run.Equal(WindowLabels.StepName(ImportStepKind.SetSiteLocation), "Site Location", "Revit's own dialog's word");
            run.Equal(WindowLabels.StepName(ImportStepKind.SiteContextView), "Site Context View", "the view and its filter");
            run.Equal(WindowLabels.StepName(ImportStepKind.AttributionAndProvenance), "Attribution", "the drafting view and the record");
            run.Equal(WindowLabels.ProgressText(new StepProgress(1_250, 4_532)), "1,250 of 4,532", "chunk progress");
        });

        run.Case("the search label and the unknown words are Title Case", () =>
        {
            foreach (string label in new[]
            {
                WindowLabels.Search,
                WindowLabels.AreaUnknown,
                WindowLabels.SizeUnknown,
                WindowLabels.ManifestVersionUnknown,
            })
            {
                foreach (string word in label.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                {
                    run.True(char.IsUpper(word[0]), $"\"{word}\" in \"{label}\" is capitalised");
                }
            }

            run.Equal(WindowLabels.Search, "Search", "the search field's label");

            // A tooltip is a sentence, like every button's ToolTip, not a face, so it is not Title
            // Case. What it must do is say the label is what is searched, so an order id typed
            // into the field and matching nothing is not a surprise.
            run.Contains(WindowLabels.SearchToolTip, "area label", "the tooltip names what is matched");
            run.True(WindowLabels.SearchToolTip.EndsWith('.'), "and is one sentence");
            run.Equal(WindowLabels.AreaUnknown, "Area Unknown", "an area the listing did not give");
            run.Equal(WindowLabels.SizeUnknown, "Size Unknown", "a size the listing did not give");
            run.Equal(WindowLabels.ManifestVersionUnknown, "Manifest Version Unknown", "a version the listing did not give");
        });

        return run.Report("window labels");
    }
}
