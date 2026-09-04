using MantlePlace.Revit.Core;

namespace MantlePlace.Revit.Core.Tests;

/// <summary>
/// The re-import identity: which site boundaries a re-import creates and which it recognises, all
/// decided from the stamps the previous import wrote.
/// </summary>
internal static class SiteBoundaryIdentityTests
{
    private const string Stem = "3f285101-0310-425b-b06b-bdb73b025b6a";

    internal static int Run()
    {
        TestRun run = new();

        run.Case("a named feature stamps as stem/name", () =>
        {
            run.Equal(
                SiteBoundaryIdentity.Stamp(Stem, "Zone A", 1),
                $"Mantle Place Site Boundary {Stem}/Zone A",
                "named stamp");
        });

        run.Case("a blank or null name falls back to the one-based index", () =>
        {
            run.Equal(
                SiteBoundaryIdentity.Stamp(Stem, null, 3),
                $"Mantle Place Site Boundary {Stem}/3",
                "null name");
            run.Equal(
                SiteBoundaryIdentity.Stamp(Stem, "   ", 2),
                $"Mantle Place Site Boundary {Stem}/2",
                "whitespace name");
        });

        run.Case("a first import creates everything", () =>
        {
            IReadOnlyList<NewSiteBoundary> created =
                SiteBoundaryIdentity.NewFeatures([], ["Zone A", null, "Zone B"], Stem);
            run.Equal(created.Count, 3, "all three are new");
            run.Equal(created[0].Ordinal, 1, "first ordinal");
            run.Equal(created[1].Stamp, $"Mantle Place Site Boundary {Stem}/2", "blank name stamps by index");
            run.Equal(created[2].Stamp, $"Mantle Place Site Boundary {Stem}/Zone B", "named stamp");
        });

        run.Case("a re-import creates nothing when every stamp is present", () =>
        {
            string[] existing =
            [
                $"Mantle Place Site Boundary {Stem}/Zone A",
                $"Mantle Place Site Boundary {Stem}/2",
                $"Mantle Place Site Boundary {Stem}/Zone B",
            ];
            IReadOnlyList<NewSiteBoundary> created =
                SiteBoundaryIdentity.NewFeatures(existing, ["Zone A", "", "Zone B"], Stem);
            run.Equal(created.Count, 0, "nothing to create — every name already exists");
        });

        run.Case("a partial earlier import creates only what is missing", () =>
        {
            IReadOnlyList<NewSiteBoundary> created = SiteBoundaryIdentity.NewFeatures(
                [$"Mantle Place Site Boundary {Stem}/Zone A"],
                ["Zone A", "Zone B"],
                Stem);
            run.Equal(created.Count, 1, "only the missing one");
            run.Equal(created[0].Ordinal, 2, "and it is the second feature");
            run.Equal(created[0].Stamp, $"Mantle Place Site Boundary {Stem}/Zone B", "its stamp");
        });

        run.Case("two features named alike get distinct stamps", () =>
        {
            IReadOnlyList<NewSiteBoundary> created =
                SiteBoundaryIdentity.NewFeatures([], ["Zone A", "Zone A"], Stem);
            run.Equal(created.Count, 2, "both created");
            run.Equal(created[0].Stamp, $"Mantle Place Site Boundary {Stem}/Zone A 1", "first duplicate");
            run.Equal(created[1].Stamp, $"Mantle Place Site Boundary {Stem}/Zone A 2", "second duplicate");
        });

        run.Case("a duplicated name never collides with a literal name that looks suffixed", () =>
        {
            // "Zone A" at position 2 would naively suffix to "Zone A 2", the third feature's literal
            // name — the disambiguation must resolve that too, deterministically.
            IReadOnlyList<NewSiteBoundary> created =
                SiteBoundaryIdentity.NewFeatures([], ["Zone A", "Zone A", "Zone A 2"], Stem);
            HashSet<string> stamps = new(StringComparer.Ordinal);
            foreach (NewSiteBoundary boundary in created)
            {
                stamps.Add(boundary.Stamp);
            }

            run.Equal(stamps.Count, 3, "three features, three distinct stamps");
        });

        run.Case("the disambiguation is deterministic across imports", () =>
        {
            IReadOnlyList<NewSiteBoundary> first =
                SiteBoundaryIdentity.NewFeatures([], ["Zone A", "Zone A"], Stem);
            IReadOnlyList<NewSiteBoundary> second = SiteBoundaryIdentity.NewFeatures(
                [first[0].Stamp, first[1].Stamp],
                ["Zone A", "Zone A"],
                Stem);
            run.Equal(second.Count, 0, "the same feature list re-derives the same stamps");
        });

        run.Case("stamps from a different bundle's stem do not match", () =>
        {
            IReadOnlyList<NewSiteBoundary> created = SiteBoundaryIdentity.NewFeatures(
                ["Mantle Place Site Boundary other-order/Zone A"],
                ["Zone A"],
                Stem);
            run.Equal(created.Count, 1, "another order's boundary never suppresses this one");
        });

        run.Case("a curator's own comment text is never mistaken for a stamp", () =>
        {
            IReadOnlyList<NewSiteBoundary> created = SiteBoundaryIdentity.NewFeatures(
                ["survey note: verify against title plan", "Zone A"],
                ["Zone A"],
                Stem);
            run.Equal(created.Count, 1, "only the full stamp counts as identity");
        });

        run.Case("names are trimmed before they become identity", () =>
        {
            IReadOnlyList<NewSiteBoundary> created = SiteBoundaryIdentity.NewFeatures(
                [$"Mantle Place Site Boundary {Stem}/Zone A"],
                ["  Zone A  "],
                Stem);
            run.Equal(created.Count, 0, "padding does not manufacture a new boundary");
        });

        run.Case("IsStampFor recognises this bundle's own subdivisions", () =>
        {
            // The drape's re-find. A subdivision is typeless, so the material goes on the instance,
            // and the instance has to be located on a RE-import where nothing was created.
            run.True(SiteBoundaryIdentity.IsStampFor($"Mantle Place Site Boundary {Stem}/Zone A", Stem),
                "a stamp this plugin wrote for this bundle");
            run.True(SiteBoundaryIdentity.IsStampFor($"Mantle Place Site Boundary {Stem}/3", Stem),
                "a positional stamp counts the same");
        });

        run.Case("IsStampFor refuses anything this import does not own", () =>
        {
            // ⛔ The trespass rule, as assertions. Draping a curator's subdivision is the same
            // trespass this plugin refuses when it declines to edit the project's own toposolid type.
            run.False(SiteBoundaryIdentity.IsStampFor(null, Stem), "no comments at all");
            run.False(SiteBoundaryIdentity.IsStampFor(string.Empty, Stem), "empty comments");
            run.False(SiteBoundaryIdentity.IsStampFor("Ridge line, do not move", Stem),
                "a curator's own note");
            run.False(SiteBoundaryIdentity.IsStampFor("Mantle Place Site Boundary other-order/Zone A", Stem),
                "another order's subdivision, sitting on the same terrain");
        });

        run.Case("IsStampFor does not let one stem claim another's", () =>
        {
            // Cache-key stems are truncated hashes. One being a prefix of another is a collision
            // waiting, not a hypothetical, and without the separator "abc" would claim "abcdef".
            run.False(SiteBoundaryIdentity.IsStampFor("Mantle Place Site Boundary abcdef/Zone A", "abc"),
                "a longer stem is not this one");
            run.False(SiteBoundaryIdentity.IsStampFor("Mantle Place Site Boundary abc/", "abc"),
                "the stem with an empty feature token is not an identity");
        });

        run.Case("the token is the part of an owned stamp after the stem, and nothing else's", () =>
        {
            // What names a subdivision's own drape material, so a re-import finds it by name.
            string stamp = SiteBoundaryIdentity.Stamp("eb00f56f", "Zone A", 1);

            run.True(SiteBoundaryIdentity.Token(stamp, "eb00f56f") == "Zone A", "the feature token");
            run.True(SiteBoundaryIdentity.Token(stamp, "other") is null, "another order's stem owns nothing");
            run.True(SiteBoundaryIdentity.Token("a curator's note", "eb00f56f") is null, "an unstamped element");
            run.True(SiteBoundaryIdentity.Token(null, "eb00f56f") is null, "no comments at all");
        });

        return run.Report("site boundary identity");
    }
}
