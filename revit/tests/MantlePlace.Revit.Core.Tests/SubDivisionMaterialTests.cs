using MantlePlace.Revit.Core;

namespace MantlePlace.Revit.Core.Tests;

/// <summary>
/// How a subdivision is given its drape material, which differs by Revit version, and what the type
/// that carries it on 2026 and later is called.
/// </summary>
internal static class SubDivisionMaterialTests
{
    private const string Imagery = "Mantle Place Site Imagery 9d2dfdbf-7a53-4d5f-8514-125edadc7a5f";

    internal static int Run()
    {
        TestRun run = new();

        run.Case("a writable instance Material is used first: the 2025 shape", () =>
        {
            run.True(SubDivisionMaterial.Route(instanceMaterialWritable: true, typedAsToposolid: false)
                    == SubDivisionMaterialRoute.Instance,
                "a typeless subdivision takes the material on the instance");

            // Never retype when the instance write is available. A retype costs seconds per
            // subdivision, and the instance write costs nothing.
            run.True(SubDivisionMaterial.Route(instanceMaterialWritable: true, typedAsToposolid: true)
                    == SubDivisionMaterialRoute.Instance,
                "the instance write wins even when a type is also there");
        });

        run.Case("a typed subdivision with no instance Material wears it through its type: 2026 and later", () =>
        {
            run.True(SubDivisionMaterial.Route(instanceMaterialWritable: false, typedAsToposolid: true)
                    == SubDivisionMaterialRoute.Type,
                "measured in 2027: TOPOSOLID_SUBDIVIDE_MATERIAL is absent and the type is a ToposolidType");
        });

        run.Case("neither route is a refusal, not a guess", () =>
        {
            run.True(SubDivisionMaterial.Route(instanceMaterialWritable: false, typedAsToposolid: false)
                    == SubDivisionMaterialRoute.Refused,
                "no instance parameter and no toposolid type leaves nothing to write");
        });

        run.Case("the type is named after the material it wears, apart from the terrain's own", () =>
        {
            string material = GroundMaterialNames.PerSubDivision(Imagery, GroundLayer.LandUse, "3", "grass");
            string type = SubDivisionMaterial.TypeName(material);

            run.True(type.StartsWith(material, StringComparison.Ordinal),
                "derived from the material name, so a re-import finds the type an earlier import made");
            run.True(type != material, "named apart from the material itself");

            // Under flat shading a subdivision with no keyword shares the ground's material, whose
            // name IS the terrain's imagery type's name. Reusing that type would put the subdivision
            // on the terrain's own layer stack, and editing it would edit the terrain.
            run.True(SubDivisionMaterial.TypeName(GroundMaterialNames.Shared(Imagery, null)) != DrapeLayering.ImageryName(
                    "9d2dfdbf-7a53-4d5f-8514-125edadc7a5f"),
                "never the terrain's imagery type");
        });

        run.Case("a retype is due only when the subdivision is not already on its material's type", () =>
        {
            string material = GroundMaterialNames.PerSubDivision(Imagery, GroundLayer.LandCover, "7", "grass");

            run.True(SubDivisionMaterial.NeedsRetype(SubDivisionMaterialRoute.Type, "Toposolid 1", material),
                "a first import: the subdivision is on the project's default type");
            run.False(SubDivisionMaterial.NeedsRetype(SubDivisionMaterialRoute.Type, SubDivisionMaterial.TypeName(material), material),
                "a re-import: already on the type, so no retype and no wait to announce");

            // A keyword that changed between builds names a different material, so the subdivision
            // IS retyped — and the notice has to count it, or the wait arrives unannounced.
            string before = GroundMaterialNames.PerSubDivision(Imagery, GroundLayer.LandCover, "7", null);
            run.True(SubDivisionMaterial.NeedsRetype(SubDivisionMaterialRoute.Type, SubDivisionMaterial.TypeName(before), material),
                "on this bundle's type for another material");

            run.False(SubDivisionMaterial.NeedsRetype(SubDivisionMaterialRoute.Instance, "Toposolid 1", material),
                "the instance route never retypes");
            run.False(SubDivisionMaterial.NeedsRetype(SubDivisionMaterialRoute.Refused, null, material),
                "nor does a refusal");
        });

        run.Case("a type whose top layer is already a photograph is recognised, whichever bundle made it", () =>
        {
            // Splitting such a type again would stack a second thin layer or be refused as too thin,
            // so its top layer's material is re-pointed instead.
            run.True(SubDivisionMaterial.IsLayeredImageryType(Imagery), "the terrain's imagery type");
            run.True(SubDivisionMaterial.IsLayeredImageryType(SubDivisionMaterial.TypeName(
                    GroundMaterialNames.PerSubDivision(Imagery, GroundLayer.LandUse, "3", null))),
                "this bundle's subdivision type");
            run.True(SubDivisionMaterial.IsLayeredImageryType(DrapeLayering.ImageryName("another-order")),
                "another bundle's imagery type");
            run.False(SubDivisionMaterial.IsLayeredImageryType("Toposolid 1"), "the project's default type");
            run.False(SubDivisionMaterial.IsLayeredImageryType(DrapeLayering.ImageryNamePrefix + "ry"),
                "a name that only starts with the same letters");
            run.False(SubDivisionMaterial.IsLayeredImageryType(null), "no type at all");
        });

        run.Case("a type name keeps the characters Revit refuses out", () =>
        {
            // The material name is already sanitised, and the suffix adds none of Revit's refused
            // characters. Asserted through the sanitiser itself, so a later suffix edit that adds one
            // is caught without this test keeping a second copy of the list.
            string suffix = SubDivisionMaterial.TypeName(string.Empty);
            run.Equal(
                GroundMaterialNames.PerSubDivision("a", GroundLayer.LandUse, suffix, null),
                "a " + GroundLayerWords.For(GroundLayer.LandUse).MaterialKind + " " + suffix,
                "the sanitiser leaves the suffix untouched");
        });

        return run.Report("subdivision material route");
    }
}
