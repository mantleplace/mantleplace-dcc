using MantlePlace.Revit.Core;

namespace MantlePlace.Revit.Core.Tests;

/// <summary>
/// Open in Curator: which extent a project already knows, and the link that carries it.
/// </summary>
/// <remarks>
/// The shim only reads two facts out of the model (the order id on Project Information, the site
/// latitude and longitude) and opens what <see cref="CuratorLink"/> returns, and it is never built
/// in CI (<c>HPS-02</c>). Every decision is therefore pinned here, for all three cases the ticket
/// names: a project with a Mantle Place import, a project with only a site location, and a project
/// with neither.
/// </remarks>
internal static class CuratorExtentTests
{
    private const string Site = "https://mantle.place";
    private const string Order = "3f2b8c1e-9d4a-4e7b-8a61-0c5d2e9f7a14";
    private const string OtherOrder = "9a1c4d70-2b3e-4f58-b6d1-7e0a8c3f5b22";

    private static readonly CuratorExtent OrderExtent = new(-122.4194, 37.7749, -122.4094, 37.7849);

    internal static int Run()
    {
        TestRun run = new();

        // --- The three cases ---

        run.Case("a project with a Mantle Place import opens on that order's extent", () =>
        {
            CuratorResolution resolved = CuratorExtents.Resolve(new CuratorProjectState
            {
                ImportedOrderId = Order,
                VaultBundles = [Bundle(Order, OrderExtent), Bundle(OtherOrder, new CuratorExtent(1, 2, 3, 4))],
                Site = new SiteLocationPoint(37.0, -122.0),
            });

            run.Equal(resolved.Source, CuratorExtentSource.PriorImport, "the import outranks the site location");
            run.True(resolved.Extent == OrderExtent, "the order's own extent, as the vault record states it");
            run.Equal(resolved.SizeChoices.Count, 0, "no size is asked for when the extent is known");
            run.False(resolved.OrderExtentUnavailable, "nothing to apologise for");
        });

        run.Case("a project with only a site location offers sizes and opens around it", () =>
        {
            SiteLocationPoint site = new(37.7749, -122.4194);
            CuratorResolution resolved = CuratorExtents.Resolve(new CuratorProjectState { Site = site });

            run.Equal(resolved.Source, CuratorExtentSource.SiteLocation, "the site location is the source");
            run.True(resolved.Extent is null, "no extent until the curator picks a size");
            run.True(resolved.SizeChoices.Count >= 3, "a short list, not a single guess");

            foreach (CuratorSizeChoice choice in resolved.SizeChoices)
            {
                CuratorExtent extent = choice.Extent;
                run.True(extent.IsValid, $"{choice.Label}: a drawable extent");
                run.Within((extent.West + extent.East) / 2, site.LongitudeDegrees, 1e-9, $"{choice.Label}: centred east-west");
                run.Within((extent.South + extent.North) / 2, site.LatitudeDegrees, 1e-9, $"{choice.Label}: centred north-south");

                // East-west metres per degree shrink with latitude; the box is still choice.SideMetres square.
                double northSouthMetres = (extent.North - extent.South) * 111_132.0;
                run.Within(northSouthMetres, choice.SideMetres, choice.SideMetres * 0.01, $"{choice.Label}: north-south side");
                double eastWestMetres = (extent.East - extent.West) * 111_320.0 * Math.Cos(site.LatitudeDegrees * Math.PI / 180.0);
                run.Within(eastWestMetres, choice.SideMetres, choice.SideMetres * 0.01, $"{choice.Label}: east-west side");
            }

            for (int i = 1; i < resolved.SizeChoices.Count; i++)
            {
                run.True(
                    resolved.SizeChoices[i].SideMetres > resolved.SizeChoices[i - 1].SideMetres,
                    "the list runs smallest to largest");
            }

            run.True(resolved.SizeChoices.All(choice => choice.Label.Length > 0), "every size is labelled");
        });

        run.Case("a project with neither opens the plain curator", () =>
        {
            CuratorResolution resolved = CuratorExtents.Resolve(new CuratorProjectState());

            run.Equal(resolved.Source, CuratorExtentSource.None, "nothing known");
            run.True(resolved.Extent is null, "no extent");
            run.Equal(resolved.SizeChoices.Count, 0, "no sizes");
            run.Equal(CuratorLink.Build(Site, resolved.Extent), "https://mantle.place/curator", "the plain link");
        });

        // --- The edges of each case ---

        run.Case("an import whose order the vault no longer lists falls back to the site location and says so", () =>
        {
            CuratorResolution resolved = CuratorExtents.Resolve(new CuratorProjectState
            {
                ImportedOrderId = Order,
                VaultBundles = [Bundle(OtherOrder, OrderExtent)],
                Site = new SiteLocationPoint(37.7749, -122.4194),
            });

            run.Equal(resolved.Source, CuratorExtentSource.SiteLocation, "the site location");
            run.True(resolved.OrderExtentUnavailable, "the curator is told why the import was not used");
        });

        run.Case("a signed-out project (no listing at all) falls back, and one with nothing else opens plain", () =>
        {
            CuratorResolution withSite = CuratorExtents.Resolve(new CuratorProjectState
            {
                ImportedOrderId = Order,
                VaultBundles = null,
                Site = new SiteLocationPoint(51.5, -0.12),
            });
            run.Equal(withSite.Source, CuratorExtentSource.SiteLocation, "the site location");
            run.True(withSite.OrderExtentUnavailable, "flagged");

            CuratorResolution neither = CuratorExtents.Resolve(new CuratorProjectState
            {
                ImportedOrderId = Order,
                VaultBundles = null,
            });
            run.Equal(neither.Source, CuratorExtentSource.None, "plain");
            run.True(neither.OrderExtentUnavailable, "flagged, so the message can say the import could not be read");
        });

        run.Case("a listed order with no extent, or an undrawable one, is not used", () =>
        {
            foreach (CuratorExtent? bad in new CuratorExtent?[]
            {
                null,
                new CuratorExtent(10, 10, 10, 11),
                new CuratorExtent(10, 11, 11, 10),
                new CuratorExtent(double.NaN, 0, 1, 1),
                new CuratorExtent(-181, 0, 1, 1),
                new CuratorExtent(0, 0, 1, 91),
            })
            {
                CuratorResolution resolved = CuratorExtents.Resolve(new CuratorProjectState
                {
                    ImportedOrderId = Order,
                    VaultBundles = [Bundle(Order, bad)],
                });
                run.Equal(resolved.Source, CuratorExtentSource.None, $"{bad} is not drawn");
                run.True(resolved.OrderExtentUnavailable, $"{bad} is flagged");
            }
        });

        run.Case("a blank order id means the project was never imported into", () =>
        {
            foreach (string? blank in new string?[] { null, "", "   " })
            {
                CuratorResolution resolved = CuratorExtents.Resolve(new CuratorProjectState
                {
                    ImportedOrderId = blank,
                    VaultBundles = [Bundle(Order, OrderExtent)],
                });
                run.Equal(resolved.Source, CuratorExtentSource.None, $"'{blank}' reads no order");
                run.False(resolved.OrderExtentUnavailable, $"'{blank}' is not a failed lookup");
            }
        });

        run.Case("the order id matches the listing whatever its case", () =>
        {
            CuratorResolution resolved = CuratorExtents.Resolve(new CuratorProjectState
            {
                ImportedOrderId = Order.ToUpperInvariant(),
                VaultBundles = [Bundle(Order, OrderExtent)],
            });
            run.Equal(resolved.Source, CuratorExtentSource.PriorImport, "a UUID is one value in either case");
        });

        run.Case("a site location of exactly 0,0, or off the globe, is not a location", () =>
        {
            foreach (SiteLocationPoint bad in new[]
            {
                new SiteLocationPoint(0, 0),
                new SiteLocationPoint(double.NaN, 10),
                new SiteLocationPoint(95, 10),
                new SiteLocationPoint(10, 181),
                new SiteLocationPoint(89.95, 10),
            })
            {
                CuratorResolution resolved = CuratorExtents.Resolve(new CuratorProjectState { Site = bad });
                run.Equal(resolved.Source, CuratorExtentSource.None, $"({bad.LatitudeDegrees}, {bad.LongitudeDegrees}) offers nothing");
            }
        });

        run.Case("a site location whose box would leave the world offers only the sizes that fit", () =>
        {
            CuratorResolution resolved = CuratorExtents.Resolve(new CuratorProjectState
            {
                Site = new SiteLocationPoint(10.0, 179.999),
            });

            run.True(resolved.SizeChoices.All(choice => choice.Extent.IsValid), "every offered size is drawable");
            run.True(
                resolved.SizeChoices.Count > 0 || resolved.Source == CuratorExtentSource.None,
                "with no size that fits, the plain curator is what opens");
        });

        run.Case("Revit's radians become degrees", () =>
        {
            SiteLocationPoint point = SiteLocationPoint.FromRadians(Math.PI / 4, -Math.PI / 2);
            run.Within(point.LatitudeDegrees, 45.0, 1e-12, "latitude");
            run.Within(point.LongitudeDegrees, -90.0, 1e-12, "longitude");
        });

        // --- The link ---

        run.Case("the link is the curator, the extent as W,S,E,N, and from=revit", () =>
        {
            run.Equal(
                CuratorLink.Build(Site, OrderExtent),
                "https://mantle.place/curator#aoi=-122.4194,37.7749,-122.4094,37.7849&from=revit",
                "the production link");
            run.Equal(CuratorLink.From, "revit", "the value the curator records for this host");
        });

        run.Case("bounds print at the curator's four decimals, rounded outward", () =>
        {
            run.Equal(
                CuratorLink.Build(Site, new CuratorExtent(-122.41946, 37.77491, -122.40941, 37.78491)),
                "https://mantle.place/curator#aoi=-122.4195,37.7749,-122.4094,37.785&from=revit",
                "west and south floor, east and north ceil, trailing zeros trimmed");
            run.Equal(
                CuratorLink.Build(Site, new CuratorExtent(10, 20, 11, 21)),
                "https://mantle.place/curator#aoi=10,20,11,21&from=revit",
                "whole degrees print with no decimal point");
            run.Equal(CuratorLink.FormatBound(12.3456, up: false), "12.3456", "binary error does not move 12.3456");
            run.Equal(CuratorLink.FormatBound(12.3456, up: true), "12.3456", "nor upward");
            run.Equal(CuratorLink.FormatBound(-0.00005, up: false), "-0.0001", "a negative below zero keeps its sign");
            run.Equal(CuratorLink.FormatBound(-0.00005, up: true), "0", "ceil to zero prints 0, never -0");
        });

        run.Case("an extent that is not drawable yields the plain curator", () =>
        {
            run.Equal(
                CuratorLink.Build(Site, new CuratorExtent(5, 5, 5, 6)),
                "https://mantle.place/curator",
                "no area");
        });

        run.Case("the base is the deployment the add-in talks to, and an unusable one falls back to the site", () =>
        {
            run.Equal(
                CuratorLink.Build("https://dev.mantle.place/", OrderExtent),
                "https://dev.mantle.place/curator#aoi=-122.4194,37.7749,-122.4094,37.7849&from=revit",
                "a trailing slash is not doubled");
            run.Equal(CuratorLink.Build("not a url", null), "https://mantle.place/curator", "unusable base");
            run.Equal(CuratorLink.Build(null, null), "https://mantle.place/curator", "no base");
        });

        // --- The vault record carries the extent ---

        run.Case("the vault listing reader keeps the row's extent", () =>
        {
            const string body = """
                {"bundles":[
                  {"id":"a","extent":{"west":-122.4194,"south":37.7749,"east":-122.4094,"north":37.7849}},
                  {"id":"b"},
                  {"id":"c","extent":{"west":1,"south":2,"east":3}},
                  {"id":"d","extent":null},
                  {"id":"e","extent":{"west":"west","south":2,"east":3,"north":4}}
                ]}
                """;
            string? error = VaultListingReader.TryParse(body, out VaultListing listing);
            run.True(error is null, "the listing parses");
            run.True(listing.Bundles[0].Extent == OrderExtent, "a row that states its extent keeps it");
            run.True(listing.Bundles[1].Extent is null, "no extent is unknown, not 0,0,0,0");
            run.True(listing.Bundles[2].Extent is null, "a bound missing makes the whole extent unknown");
            run.True(listing.Bundles[3].Extent is null, "null is unknown");
            run.True(listing.Bundles[4].Extent is null, "a bound that is not a number makes the extent unknown");
        });

        return run.Report("curator extent");
    }

    private static VaultBundle Bundle(string orderId, CuratorExtent? extent) => new()
    {
        OrderId = orderId,
        Extent = extent,
    };
}
