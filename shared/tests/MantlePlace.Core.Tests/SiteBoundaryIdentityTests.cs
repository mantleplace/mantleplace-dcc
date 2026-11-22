using MantlePlace.Core;

namespace MantlePlace.Core.Tests;

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
            run.True(SiteBoundaryIdentity.IsStampFor($"Mantle Place Site Boundary {Stem}/Zone A", Stem),
                "a stamp this plugin wrote for this bundle");
            run.True(SiteBoundaryIdentity.IsStampFor($"Mantle Place Site Boundary {Stem}/3", Stem),
                "a positional stamp counts the same");
        });

        run.Case("IsStampFor refuses anything this import does not own", () =>
        {
            run.False(SiteBoundaryIdentity.IsStampFor(null, Stem), "no comments at all");
            run.False(SiteBoundaryIdentity.IsStampFor(string.Empty, Stem), "empty comments");
            run.False(SiteBoundaryIdentity.IsStampFor("Ridge line, do not move", Stem),
                "a curator's own note");
            run.False(SiteBoundaryIdentity.IsStampFor("Mantle Place Site Boundary other-order/Zone A", Stem),
                "another order's subdivision, sitting on the same terrain");
        });

        run.Case("IsStampFor does not let one stem claim another's", () =>
        {
            run.False(SiteBoundaryIdentity.IsStampFor("Mantle Place Site Boundary abcdef/Zone A", "abc"),
                "a longer stem is not this one");
            run.False(SiteBoundaryIdentity.IsStampFor("Mantle Place Site Boundary abc/", "abc"),
                "the stem with an empty feature token is not an identity");
        });

        run.Case("the token is the part of an owned stamp after the stem, and nothing else's", () =>
        {
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

        run.Case("no two layers share a word", () =>
        {
            GroundLayerWords[] words = [.. Enum.GetValues<GroundLayer>().Select(GroundLayerWords.For)];

            run.Equal(words.Select(w => w.StampKind).Distinct(StringComparer.Ordinal).Count(), words.Length, "stamp kind");
            run.Equal(words.Select(w => w.MaterialKind).Distinct(StringComparer.Ordinal).Count(), words.Length, "material word");
            run.Equal(words.Select(w => w.Label).Distinct(StringComparer.Ordinal).Count(), words.Length, "log label");
            run.Equal(words.Select(w => w.Noun).Distinct(StringComparer.Ordinal).Count(), words.Length, "noun");
        });

        run.Case("a layer nobody worded throws rather than borrowing land use's stamp", () =>
        {
            bool threw = false;
            try
            {
                GroundLayerWords.For((GroundLayer)99);
            }
            catch (ArgumentOutOfRangeException)
            {
                threw = true;
            }

            run.True(threw, "an unworded layer has no identity to lend or borrow");
        });

        run.Case("the water and road-surface layers stamp under kinds of their own", () =>
        {
            run.Equal(
                SiteBoundaryIdentity.Stamp(GroundLayer.Water, Stem, "Lake Wendouree", 1),
                $"Mantle Place Water {Stem}/Lake Wendouree",
                "a named water body");
            run.Equal(
                SiteBoundaryIdentity.Stamp(GroundLayer.RoadSurface, Stem, null, 7),
                $"Mantle Place Road Surface {Stem}/7",
                "the road surfaces are merged per class and carry no name");
        });

        run.Case("a road surface's stamp is not a road centreline's", () =>
        {
            string surface = SiteBoundaryIdentity.Stamp(GroundLayer.RoadSurface, Stem, null, 1);
            run.Equal(
                RoadIdentity.Decide([surface], Stem, 1).RowsToCreate.Count,
                1,
                "a road surface on the terrain is no centreline the road step has already drawn");
            run.False(SiteBoundaryIdentity.IsStampFor(RoadIdentity.Stamp(Stem, 1), Stem), "nor the other way round");
            run.True(SiteBoundaryIdentity.Parse(surface, Stem)?.Layer == GroundLayer.RoadSurface, "and the surface is a surface");
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

        run.Case("a subdivision's drape names come from its stamp, else the stamp it was cut with, else its id", () =>
        {
            string cover = SiteBoundaryIdentity.Stamp(GroundLayer.LandCover, Stem, null, 7);
            string other = SiteBoundaryIdentity.Stamp(GroundLayer.LandUse, Stem, "Zone A", 1);

            run.True(
                SiteBoundaryIdentity.NamingStamp(cover, other, 4242, Stem) == new GroundStamp(GroundLayer.LandCover, "7"),
                "the stamp it carries wins, as it always has");

            run.True(
                SiteBoundaryIdentity.NamingStamp(null, cover, 4242, Stem) == new GroundStamp(GroundLayer.LandCover, "7"),
                "an unstamped cut takes the stamp it was cut with");
            run.True(
                SiteBoundaryIdentity.NamingStamp("a curator's note", cover, 4242, Stem) == new GroundStamp(GroundLayer.LandCover, "7"),
                "Comments that are not a stamp are not one here either");

            run.True(
                SiteBoundaryIdentity.NamingStamp(null, null, 4242, Stem) == new GroundStamp(GroundLayer.LandUse, "4242"),
                "with neither, the element id under the land-use spelling, as before");
        });

        run.Case("a subdivision cut from many roads carries every road's stamp, one to a line", () =>
        {
            string first = SiteBoundaryIdentity.Stamp(GroundLayer.RoadSurface, Stem, "primary 1", 1);
            string second = SiteBoundaryIdentity.Stamp(GroundLayer.RoadSurface, Stem, "primary 2", 2);
            string third = SiteBoundaryIdentity.Stamp(GroundLayer.RoadSurface, Stem, null, 3);
            string comments = SiteBoundaryIdentity.Batch([first, second, third]);

            run.Equal(comments, first + "\n" + second + "\n" + third, "one stamp to a line");
            run.Equal(SiteBoundaryIdentity.Members(comments).Count, 3, "every member reads back");
            run.Equal(SiteBoundaryIdentity.Members(comments)[2], third, "in the order they were cut");
            run.Equal(SiteBoundaryIdentity.Batch([first]), first, "a batch of one is the stamp it always was");
            run.Equal(SiteBoundaryIdentity.Members(first).Count, 1, "and reads back as itself");
            run.Equal(SiteBoundaryIdentity.Members(null).Count, 0, "no comment, no member");
        });

        run.Case("a re-import of a batched layer finds every road it cut", () =>
        {
            string comments = SiteBoundaryIdentity.Batch(
                SiteBoundaryIdentity.Stamps(GroundLayer.RoadSurface, ["primary", "primary", null], Stem));

            IReadOnlyList<NewSiteBoundary> created = SiteBoundaryIdentity.NewFeatures(
                GroundLayer.RoadSurface,
                SiteBoundaryIdentity.Members(comments),
                ["primary", "primary", null, "service"],
                Stem);

            run.Equal(created.Count, 1, "only the road the batch does not hold is cut again");
            run.Equal(created[0].Ordinal, 4, "and it is the new one");
        });

        run.Case("a batched subdivision is owned and named by its first road", () =>
        {
            string comments = SiteBoundaryIdentity.Batch(
                SiteBoundaryIdentity.Stamps(GroundLayer.RoadSurface, ["primary", "service"], Stem));

            run.True(SiteBoundaryIdentity.IsStampFor(comments, Stem), "this import owns it");
            run.True(
                SiteBoundaryIdentity.Parse(comments, Stem) == new GroundStamp(GroundLayer.RoadSurface, "primary"),
                "its drape material is named for its first road, not for every road it holds");
            run.False(
                SiteBoundaryIdentity.IsStampFor("a curator's note\n" + comments, Stem),
                "a curator's note on the first line is still not a stamp");
        });

        run.Case("a line break inside a name cannot split one road's stamp into two", () =>
        {
            run.Equal(
                SiteBoundaryIdentity.Stamp(GroundLayer.RoadSurface, Stem, "Main\nStreet", 1),
                $"Mantle Place Road Surface {Stem}/Main Street",
                "the break reads as a space");
            run.Equal(
                SiteBoundaryIdentity.Members(SiteBoundaryIdentity.Batch(
                    SiteBoundaryIdentity.Stamps(GroundLayer.RoadSurface, ["Main\r\nStreet", "Elm"], Stem))).Count,
                2,
                "so a batch of two reads back as two");
        });

        return run.Report("site boundary identity");
    }
}
