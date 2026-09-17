namespace MantlePlace.Revit.Core.Tests;

/// <summary>
/// Which committed render of the mark a slot of a given logical size should be handed.
/// </summary>
/// <remarks>
/// The rule is the table in <c>Resources/src/README.md</c>, and the reason it is a rule rather than
/// "just use the 32" is that WPF and the Revit ribbon both measure in logical pixels and scale
/// whatever they are handed. On a 200% laptop a 32 px render in a 32 px slot is upscaled twice over
/// and reads as a blurry square; the 64 px render in the same slot reads as the mark.
/// </remarks>
internal static class MarkRenderTests
{
    internal static int Run()
    {
        TestRun run = new();

        run.Case("at 100% a slot takes the render that matches it", () =>
        {
            run.Equal(MarkRenders.SizeFor(16, 1.0), 16, "the ribbon's small slot");
            run.Equal(MarkRenders.SizeFor(MarkRenders.HeaderSlotPixels, 1.0), 32, "the window header's slot");
        });

        run.Case("the documented display scales land on the documented renders", () =>
        {
            // Resources/src/README.md, the three rows that stop the 24, 48 and 64 px renders from
            // looking like dead weight.
            run.Equal(MarkRenders.SizeFor(16, 1.5), 24, "150%, small");
            run.Equal(MarkRenders.SizeFor(32, 1.5), 48, "150%, large");
            run.Equal(MarkRenders.SizeFor(16, 2.0), 32, "200%, small");
            run.Equal(MarkRenders.SizeFor(32, 2.0), 64, "200%, large");
        });

        run.Case("a scale between the steps takes the next render up", () =>
        {
            // 125% wants 40 px, which nothing is. Scaling 48 down costs less than scaling 32 up.
            run.Equal(MarkRenders.SizeFor(32, 1.25), 48, "125%, large");
            run.Equal(MarkRenders.SizeFor(16, 1.25), 24, "125%, small");
        });

        run.Case("past the largest render, the largest render is the answer", () =>
        {
            // 64 px is what was rendered. Asking for more cannot conjure one, and the alternative —
            // returning nothing — would leave the header blank on a 300% display.
            run.Equal(MarkRenders.SizeFor(32, 3.0), 64, "300%, large");
            run.Equal(MarkRenders.SizeFor(256, 1.0), 64, "a slot larger than anything rendered");
        });

        run.Case("a scale Windows cannot report is treated as 100%", () =>
        {
            // DpiScaleX comes off a visual that may not be in a tree yet. A zero or a NaN there must
            // not pick a render by accident, and must not throw on a WPF dispatcher.
            run.Equal(MarkRenders.SizeFor(32, 0), 32, "zero");
            run.Equal(MarkRenders.SizeFor(32, -2), 32, "negative");
            run.Equal(MarkRenders.SizeFor(32, double.NaN), 32, "NaN");
            run.Equal(MarkRenders.SizeFor(32, 0.75), 32, "below 100%, which Windows does not offer");
        });

        run.Case("the file name is the render, and every render named exists in the list", () =>
        {
            run.Equal(MarkRenders.FileNameFor(32, 2.0), "MantlePlaceMark_64.png", "the 200% header render");
            run.Equal(MarkRenders.FileNameFor(16, 1.0), "MantlePlaceMark_16.png", "the 100% ribbon render");

            foreach (int size in MarkRenders.Sizes)
            {
                run.Equal(MarkRenders.FileNameFor(size, 1.0), $"MantlePlaceMark_{size}.png", $"the {size} px render");
            }
        });

        run.Case("the sizes are the committed renders, smallest first", () =>
        {
            // A render added to Resources without a row here would never be chosen; one removed from
            // Resources and left here would be asked for by name and not be there.
            run.Equal(string.Join(",", MarkRenders.Sizes), "16,24,32,48,64", "the committed renders");
        });

        run.Case("a slot with no size is a programming error, not a guess", () =>
        {
            bool threw = false;
            try
            {
                MarkRenders.SizeFor(0, 1.0);
            }
            catch (ArgumentOutOfRangeException)
            {
                threw = true;
            }

            run.True(threw, "a zero-pixel slot is refused");
        });

        return run.Report("mark renders");
    }
}
