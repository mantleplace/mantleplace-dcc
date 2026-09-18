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
                SiteBoundaryIdentity.Stamp(GroundLayer.LandUse, Stem, "Zone A", 1),
                $"Mantle Place Site Boundary {Stem}/Zone A",
                "named stamp");
        });

        run.Case("a blank or null name falls back to the one-based index", () =>
        {
            run.Equal(
                SiteBoundaryIdentity.Stamp(GroundLayer.LandUse, Stem, null, 3),
                $"Mantle Place Site Boundary {Stem}/3",
                "null name");
            run.Equal(
                SiteBoundaryIdentity.Stamp(GroundLayer.LandUse, Stem, "   ", 2),
                $"Mantle Place Site Boundary {Stem}/2",
                "whitespace name");
        });

        run.Case("a first import creates everything", () =>
        {
            IReadOnlyList<NewSiteBoundary> created =
                SiteBoundaryIdentity.NewFeatures(GroundLayer.LandUse, [], ["Zone A", null, "Zone B"], Stem);
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
                SiteBoundaryIdentity.NewFeatures(GroundLayer.LandUse, existing, ["Zone A", "", "Zone B"], Stem);
            run.Equal(created.Count, 0, "nothing to create — every name already exists");
        });

        run.Case("a partial earlier import creates only what is missing", () =>
        {
            IReadOnlyList<NewSiteBoundary> created = SiteBoundaryIdentity.NewFeatures(
                GroundLayer.LandUse,
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
                SiteBoundaryIdentity.NewFeatures(GroundLayer.LandUse, [], ["Zone A", "Zone A"], Stem);
            run.Equal(created.Count, 2, "both created");
            run.Equal(created[0].Stamp, $"Mantle Place Site Boundary {Stem}/Zone A 1", "first duplicate");
            run.Equal(created[1].Stamp, $"Mantle Place Site Boundary {Stem}/Zone A 2", "second duplicate");
        });

        run.Case("a duplicated name never collides with a literal name that looks suffixed", () =>
        {
            // "Zone A" at position 2 would naively suffix to "Zone A 2", the third feature's literal
            // name — the disambiguation must resolve that too, deterministically.
            IReadOnlyList<NewSiteBoundary> created =
                SiteBoundaryIdentity.NewFeatures(GroundLayer.LandUse, [], ["Zone A", "Zone A", "Zone A 2"], Stem);
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
                SiteBoundaryIdentity.NewFeatures(GroundLayer.LandUse, [], ["Zone A", "Zone A"], Stem);
            IReadOnlyList<NewSiteBoundary> second = SiteBoundaryIdentity.NewFeatures(
                GroundLayer.LandUse,
                [first[0].Stamp, first[1].Stamp],
                ["Zone A", "Zone A"],
                Stem);
            run.Equal(second.Count, 0, "the same feature list re-derives the same stamps");
        });

        run.Case("stamps from a different bundle's stem do not match", () =>
        {
            IReadOnlyList<NewSiteBoundary> created = SiteBoundaryIdentity.NewFeatures(
                GroundLayer.LandUse,
                ["Mantle Place Site Boundary other-order/Zone A"],
                ["Zone A"],
                Stem);
            run.Equal(created.Count, 1, "another order's boundary never suppresses this one");
        });

        run.Case("a curator's own comment text is never mistaken for a stamp", () =>
        {
            IReadOnlyList<NewSiteBoundary> created = SiteBoundaryIdentity.NewFeatures(
                GroundLayer.LandUse,
                ["survey note: verify against title plan", "Zone A"],
                ["Zone A"],
                Stem);
            run.Equal(created.Count, 1, "only the full stamp counts as identity");
        });

        run.Case("names are trimmed before they become identity", () =>
        {
            IReadOnlyList<NewSiteBoundary> created = SiteBoundaryIdentity.NewFeatures(
                GroundLayer.LandUse,
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
            string stamp = SiteBoundaryIdentity.Stamp(GroundLayer.LandUse, "eb00f56f", "Zone A", 1);

            run.True(SiteBoundaryIdentity.Parse(stamp, "eb00f56f")?.Token == "Zone A", "the feature token");
            run.True(SiteBoundaryIdentity.Parse(stamp, "other")?.Token is null, "another order's stem owns nothing");
            run.True(SiteBoundaryIdentity.Parse("a curator's note", "eb00f56f")?.Token is null, "an unstamped element");
            run.True(SiteBoundaryIdentity.Parse(null, "eb00f56f")?.Token is null, "no comments at all");
        });

        run.Case("land use keeps the words it had before land cover, which are identity", () =>
        {
            GroundLayerWords use = GroundLayerWords.For(GroundLayer.LandUse);
            run.Equal(use.StampKind, "Site Boundary", "the stamp kind every earlier import wrote");
            run.Equal(use.MaterialKind, "boundary", "the material word every earlier import wrote");
        });

        run.Case("the two layers differ in every word", () =>
        {
            GroundLayerWords use = GroundLayerWords.For(GroundLayer.LandUse);
            GroundLayerWords cover = GroundLayerWords.For(GroundLayer.LandCover);
            run.False(use.StampKind == cover.StampKind, "stamp kind");
            run.False(use.MaterialKind == cover.MaterialKind, "material word");
            run.False(use.Label == cover.Label, "log label");
            run.False(use.Noun == cover.Noun, "noun");
        });

        run.Case("a land-cover feature stamps under its own kind", () =>
        {
            run.Equal(
                SiteBoundaryIdentity.Stamp(GroundLayer.LandCover, Stem, null, 4),
                $"Mantle Place Land Cover {Stem}/4",
                "land_cover publishes no names, so its stamps are positions");
        });

        run.Case("the two layers' positional stamps never suppress one another", () =>
        {
            // Both layers stamp an unnamed feature by position, so each has a "1". Without the kind
            // in the stamp, the land-use subdivision 1 would read as land-cover 1 already cut.
            IReadOnlyList<NewSiteBoundary> created = SiteBoundaryIdentity.NewFeatures(
                GroundLayer.LandCover,
                [$"Mantle Place Site Boundary {Stem}/1", $"Mantle Place Site Boundary {Stem}/2"],
                [null, null],
                Stem);
            run.Equal(created.Count, 2, "both land-cover features are still to cut");
            run.Equal(created[0].Stamp, $"Mantle Place Land Cover {Stem}/1", "under the land-cover kind");
        });

        run.Case("a second land-cover import creates nothing", () =>
        {
            IReadOnlyList<NewSiteBoundary> created = SiteBoundaryIdentity.NewFeatures(
                GroundLayer.LandCover,
                [$"Mantle Place Land Cover {Stem}/1", $"Mantle Place Land Cover {Stem}/2"],
                [null, null],
                Stem);
            run.Equal(created.Count, 0, "both recognised");
        });

        run.Case("Stamps names every feature, present or not, in layer order", () =>
        {
            // What the drape pairs with each feature's subtype, whether this run cut it or an earlier
            // import did.
            IReadOnlyList<string> stamps = SiteBoundaryIdentity.Stamps(GroundLayer.LandUse, ["Zone A", null, "Zone A"], Stem);
            run.Equal(stamps.Count, 3, "one per feature");
            run.Equal(stamps[0], $"Mantle Place Site Boundary {Stem}/Zone A 1", "the disambiguated first");
            run.Equal(stamps[1], $"Mantle Place Site Boundary {Stem}/2", "the positional second");
            run.Equal(stamps[2], $"Mantle Place Site Boundary {Stem}/Zone A 3", "the disambiguated third");
        });

        run.Case("IsStampFor and Parse recognise both layers, and say which", () =>
        {
            string cover = SiteBoundaryIdentity.Stamp(GroundLayer.LandCover, Stem, null, 2);
            run.True(SiteBoundaryIdentity.IsStampFor(cover, Stem), "a land-cover subdivision is this import's to drape");
            run.True(SiteBoundaryIdentity.Parse(cover, Stem)?.Layer == GroundLayer.LandCover, "land cover");
            run.Equal(SiteBoundaryIdentity.Parse(cover, Stem)?.Token, "2", "its token");
            run.True(
                SiteBoundaryIdentity.Parse(SiteBoundaryIdentity.Stamp(GroundLayer.LandUse, Stem, "Zone A", 1), Stem)?.Layer
                    == GroundLayer.LandUse,
                "land use");
            run.False(SiteBoundaryIdentity.IsStampFor($"Mantle Place Land Cover other-order/2", Stem), "another order's cover");
            run.False(SiteBoundaryIdentity.IsStampFor($"Mantle Place Land Cover {Stem}/", Stem), "an empty token");
            run.False(SiteBoundaryIdentity.IsStampFor(RoadIdentity.Stamp(Stem, 1), Stem), "a road is not a subdivision");
        });

        return run.Report("site boundary identity");
    }
}
