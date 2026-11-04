namespace MantlePlace.Revit.Core;

/// <summary>
/// The host id this plugin is to the platform: the name its sign-in, its client header and its
/// connected-host presence carry.
/// </summary>
/// <remarks>
/// The Revit sources compile into the AutoCAD plugin by include, and the platform tells the two
/// apart by this one value (the platform auth and export contracts name <c>revit</c> and
/// <c>autocad</c>). The AutoCAD core build defines <c>MANTLEPLACE_AUTOCAD</c>, so the same file
/// answers for the plugin that is loading it. Only the connection's identity moves: the manifest
/// reader's host block is a separate fact and stays as it is until AutoCAD has a reader of its own.
/// </remarks>
public static class HostIdentity
{
#if MANTLEPLACE_AUTOCAD
    /// <summary>The host id of the AutoCAD plugin.</summary>
    public const string Id = "autocad";
#else
    /// <summary>The host id of the Revit plugin.</summary>
    public const string Id = "revit";
#endif
}
