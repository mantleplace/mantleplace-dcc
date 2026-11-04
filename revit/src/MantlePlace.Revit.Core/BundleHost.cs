namespace MantlePlace.Revit.Core;

/// <summary>
/// The Autodesk host a release bundle is for. <see cref="AutodeskBundleManifest.Build"/> takes one, so
/// the two plugins share one manifest function (their source is shared) and differ only in what the
/// host decides: the platform it names, its series numbering and the module each year loads.
/// </summary>
public enum BundleHost
{
    /// <summary>The Revit plugin: one add-in manifest, selected by <c>R&lt;year&gt;</c>.</summary>
    Revit,

    /// <summary>The AutoCAD plugin, which covers plain AutoCAD and Civil 3D as one host.</summary>
    AutoCad,
}
