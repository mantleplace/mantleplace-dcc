using System.Text.Json.Nodes;

namespace MantlePlace.Core.Tests;

/// <summary>
/// The frames a reader found, written back in the words the manifest states them in
/// (<c>spec/format.md</c> §9.4), so a reader case can state them once as the manifest does and the
/// suite compares member by member. Nothing is derived: each value is the reader's own field.
/// </summary>
internal static class FrameJson
{
    internal static JsonObject Of(BundleFrames frames)
    {
        GeoreferencedFrame order = frames.Georeferenced;
        LocalSiteFrame site = frames.Local;
        return new JsonObject
        {
            ["georeferenced"] = new JsonObject
            {
                ["horizontal_crs"] = Crs(order.HorizontalCrs),
                ["vertical_crs"] = Crs(order.VerticalCrs),
                ["geoid_model"] = order.GeoidModel,
                ["horizontal_unit"] = Unit(order.HorizontalUnit),
                ["vertical_unit"] = Unit(order.VerticalUnit),
                ["axis_order"] = order.AxisOrder switch
                {
                    AxisOrder.EastNorth => "east-north",
                    AxisOrder.NorthEast => "north-east",
                    _ => order.AxisOrder.ToString(),
                },
                ["grid_rotation_deg"] = order.GridRotationDegrees,
            },
            ["local"] = new JsonObject
            {
                ["origin"] = new JsonObject
                {
                    ["longitude"] = site.Origin.Longitude,
                    ["latitude"] = site.Origin.Latitude,
                    ["ellipsoidal_height_m"] = site.Origin.EllipsoidalHeightMetres,
                    ["height_m"] = site.Origin.HeightMetres,
                    ["projected"] = new JsonObject
                    {
                        ["crs"] = Crs(site.Origin.Projected.Crs),
                        ["x"] = site.Origin.Projected.X,
                        ["y"] = site.Origin.Projected.Y,
                        ["unit"] = Unit(site.Origin.Projected.Unit),
                    },
                },
                ["vertical_crs"] = Crs(site.VerticalCrs),
                ["geoid_model"] = site.GeoidModel,
                ["unit"] = Unit(site.Unit),
                ["axes"] = new JsonObject { ["x"] = "east", ["y"] = "north", ["z"] = "up" },
                ["grid_rotation_deg"] = site.GridRotationDegrees,
            },
        };
    }

    private static JsonObject Unit(FrameUnit unit) => new()
    {
        ["name"] = unit.Name,
        ["to_metres"] = unit.ToMetres,
    };

    private static JsonObject Crs(FrameCrs crs)
    {
        JsonObject written = [];
        if (crs.Code is not null)
        {
            written["code"] = crs.Code;
        }

        if (crs.ProjJson is not null)
        {
            written["projjson"] = JsonNode.Parse(crs.ProjJson);
        }

        if (crs.Wkt2 is not null)
        {
            written["wkt2"] = crs.Wkt2;
        }

        return written;
    }
}
