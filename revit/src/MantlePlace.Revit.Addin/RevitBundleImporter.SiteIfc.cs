// UseWPF switches the SDK to the WindowsDesktop implicit-usings set, which drops System.IO.
using System.IO;
using Autodesk.Revit.DB;
using MantlePlace.Revit.Core;

namespace MantlePlace.Revit.Addin;

// The site IFC step: the link, found again on a re-import rather than created twice.
internal sealed partial class RevitBundleImporter
{
    /// <summary>
    /// Links the IFC site model as a coordinated reference — two steps, not one: convert the IFC to
    /// a companion <c>.rvt</c> named for this Revit version, then link that.
    /// </summary>
    private void LinkSiteIfc(ImportStep step)
    {
        // Both the IFC and the companion .rvt are referenced by the link for the life of the
        // project, so the kind's lifetime carries the .rvt written next to it too.
        string ifcPath = _archive.Extract(step.EntryName, ImportStepKinds.LifetimeOf(step.Kind), step.ExpectedSha256);

        // ⛔ Named for THIS Revit. The cache is one per order for the machine, not one per Revit, so
        // a single shared companion meant the second version to import an order upgraded the first
        // one's file in place — one way, unrepairable by re-importing, and visible only as a broken
        // link in the older project's Manage Links. See SiteCompanionPath.
        string companionRvt = SiteCompanionPath.ForVersion(ifcPath, _application.VersionNumber);

        // CreateFromIFC does NOT convert; it links an ALREADY-CONVERTED Revit file and throws
        // FileArgumentNotFoundException when that file is missing. OpenIFCDocument is the
        // conversion step, and it must run outside any transaction on the host document.
        //
        // This runs BEFORE the already-linked check below, and the order is load-bearing rather
        // than incidental. Converting first costs nothing on the ordinary re-import — the companion
        // is there and File.Exists skips the work — and it is what rebuilds a companion that Remove
        // download took while the project kept its link. Checking first would return early and
        // leave that link permanently unresolvable, which is the failure this whole step is about.
        if (!File.Exists(companionRvt))
        {
            Document? converted = null;
            try
            {
                converted = _application.OpenIFCDocument(ifcPath);
                converted.SaveAs(companionRvt);
            }
            catch (Exception ex) when (ex is Autodesk.Revit.Exceptions.ApplicationException or IOException)
            {
                Say($"Could not convert the IFC site model for linking ({step.EntryName}): {ex.Message}");
                return;
            }
            finally
            {
                converted?.Close(false);
            }
        }

        // ⛔ A re-import used to die here, and take the whole rest of the import with it:
        // CreateFromIFC throws when the document already carries a link at that path, and the
        // curator cannot fix it by selecting the site and pressing Delete — a RevitLinkType is not
        // in any view, it lives in Manage Links. Every other repeatable step in this import already
        // recognises its own earlier work (the boundary stamps, the drape's type-by-name); this one
        // simply had not been run twice yet. Reusing the existing link is also the honest answer:
        // the conversion above has just made sure the file the link points at is on disk, so the
        // link that is already there is the link this step would create.
        if (ExistingSiteLink(ifcPath) is { } alreadyLinked)
        {
            Say($"The IFC site model ({step.EntryName}) is already linked into this project, so it "
                + "was left as it is. Remove it under Manage ▸ Manage Links if you want it rebuilt.");
            EnsureLinkInstance(alreadyLinked);
            return;
        }

        ImportFailureSwallower swallower = new("Linking the IFC site");
        using Transaction transaction = BeginTransaction("Mantle Place: link IFC site", swallower);
        LinkLoadResult result = RevitLinkType.CreateFromIFC(
            _document,
            ifcPath,
            companionRvt,
            false,
            new RevitLinkOptions(false));

        if (result.ElementId != ElementId.InvalidElementId)
        {
            RevitLinkInstance.Create(_document, result.ElementId);
        }

        if (!CommitAndReport(transaction, swallower))
        {
            // The swallower already said why. Reporting the work below as done would be a lie:
            // the rollback took all of it.
            return;
        }

        Say(result.ElementId != ElementId.InvalidElementId
            ? $"Linked the IFC site model ({step.EntryName})."
            : $"Revit declined to link the IFC site model ({step.EntryName}).");
    }

    /// <summary>
    /// The <see cref="RevitLinkType"/> already pointing at this site model, or <c>null</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The IFC and its companion are both accepted because <c>CreateFromIFC</c> takes two paths and
    /// Revit records the one it prefers: the IFC that was converted, or the <c>.rvt</c> it was
    /// converted into. Which of the two lands in the external file reference is not worth depending
    /// on.
    /// </para>
    /// <para>
    /// "Its companion" means ⛔<see cref="SiteCompanionPath.IsCompanionOf"/>, not this Revit's
    /// companion. The link may have been created by a different Revit version, or by a build from
    /// before companions were version-qualified at all, and either way it is a link this project
    /// already has. Failing to recognise it would call <c>CreateFromIFC</c> against an
    /// already-linked path, which throws — the very failure the reuse below exists to prevent.
    /// </para>
    /// </remarks>
    private ElementId? ExistingSiteLink(string ifcPath)
    {
        foreach (RevitLinkType link in new FilteredElementCollector(_document)
            .OfClass(typeof(RevitLinkType))
            .Cast<RevitLinkType>())
        {
            if (!link.IsExternalFileReference())
            {
                continue;
            }

            string linked;
            try
            {
                linked = ModelPathUtils.ConvertModelPathToUserVisiblePath(
                    link.GetExternalFileReference().GetAbsolutePath());
            }
            catch (Autodesk.Revit.Exceptions.ApplicationException)
            {
                // A link whose path Revit will not resolve is not one this step can match against,
                // and it is certainly not a reason to abandon the search.
                continue;
            }

            if (string.Equals(linked, ifcPath, StringComparison.OrdinalIgnoreCase)
                || SiteCompanionPath.IsCompanionOf(ifcPath, linked))
            {
                return link.Id;
            }
        }

        return null;
    }

    /// <summary>
    /// Puts one instance of <paramref name="linkTypeId"/> in the document if none is there.
    /// </summary>
    /// <remarks>
    /// The type can outlive its instances — deleting the site from a view deletes the instance and
    /// leaves the type in Manage Links, which is exactly the state the curator who hit this was in.
    /// Reusing the type without restoring an instance would report a link nobody can see.
    /// </remarks>
    private void EnsureLinkInstance(ElementId linkTypeId)
    {
        bool placed = new FilteredElementCollector(_document)
            .OfClass(typeof(RevitLinkInstance))
            .Cast<RevitLinkInstance>()
            .Any(instance => instance.GetTypeId() == linkTypeId);

        if (placed)
        {
            return;
        }

        ImportFailureSwallower swallower = new("Placing the existing IFC site link");
        using Transaction transaction = BeginTransaction("Mantle Place: place IFC site link", swallower);
        RevitLinkInstance.Create(_document, linkTypeId);

        if (CommitAndReport(transaction, swallower))
        {
            Say("Put the existing IFC site link back into the model — the link type was still in "
                + "Manage Links, but nothing in the project was showing it.");
        }
    }
}
