using MantlePlace.Revit.Core.Tests;

namespace MantlePlace.AutoCad.Tests;

/// <summary>
/// Which surface in the block's LandXML Civil 3D is asked to build: the file's one surface, named as the
/// file names it, and a refusal that says why when there is not exactly one.
/// </summary>
internal static class LandXmlSurfaceTests
{
    private const string Head = "<?xml version=\"1.0\" encoding=\"UTF-8\"?><LandXML xmlns=\"http://www.landxml.org/schema/LandXML-1.2\" version=\"1.2\">"
        + "<Units><Imperial linearUnit=\"USSurveyFoot\"/></Units><CoordinateSystem epsgCode=\"2231\"/>";

    internal static int Run()
    {
        TestRun run = new();

        static string Read(string xml, out string? name)
        {
            using StringReader reader = new(xml);
            return LandXmlSurface.TryReadName(reader, out name) ?? string.Empty;
        }

        run.Case("the file's one surface is named as the file names it", () =>
        {
            string problem = Read(Head + "<Surfaces><Surface name=\"Mantle Place terrain\"><Definition surfType=\"TIN\"/></Surface></Surfaces></LandXML>", out string? name);

            run.Equal(problem, string.Empty, "no problem");
            run.Equal(name, "Mantle Place terrain", "the name, verbatim");
        });

        run.Case("a file with no surface is refused, saying so", () =>
        {
            string problem = Read(Head + "<Surfaces/></LandXML>", out string? name);

            run.Equal(name, null, "no name");
            run.Contains(problem, "no surface", "why");
        });

        run.Case("a file with two surfaces is refused rather than guessing which", () =>
        {
            string problem = Read(Head + "<Surfaces><Surface name=\"A\"/><Surface name=\"B\"/></Surfaces></LandXML>", out string? name);

            run.Equal(name, null, "no name");
            run.Contains(problem, "2 surfaces", "the count");
        });

        run.Case("a surface with no name is refused", () =>
        {
            string problem = Read(Head + "<Surfaces><Surface/></Surfaces></LandXML>", out string? name);

            run.Equal(name, null, "no name");
            run.Contains(problem, "no name", "why");
        });

        run.Case("text that is not XML is refused, not thrown", () =>
        {
            string problem = Read("<LandXML><Surfaces><Surface name=", out string? name);

            run.Equal(name, null, "no name");
            run.Contains(problem, "not LandXML", "why");
        });

        return run.Report("LandXML surface");
    }
}
