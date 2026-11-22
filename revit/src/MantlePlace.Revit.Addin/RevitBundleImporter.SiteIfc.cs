using System.Diagnostics;
using System.IO;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.IFC;
using MantlePlace.Core;

namespace MantlePlace.Revit.Addin;

internal sealed partial class RevitBundleImporter
{
    private void LinkSiteIfc(ImportStep step)
    {
        if (HeightsRefused(step))
        {
            return;
        }

        string ifcPath = _archive.Extract(step.EntryName, step.Lifetime, step.ExpectedSha256);

        string companionRvt = SiteCompanionPath.ForVersion(ifcPath, _application.VersionNumber);

        bool companionExists = File.Exists(companionRvt);
        Announce(SlowStepNotice.ForSiteModel(convertedFileExists: companionExists));
        if (companionExists)
        {
            Trace($"  site model: {Path.GetFileName(companionRvt)} is already converted, so it was linked without converting {step.EntryName} again.");
        }
        else
        {
            Document? converted = null;
            int errorsBefore = SessionErrorCount;
            try
            {
                Stopwatch converting = Stopwatch.StartNew();
                converted = OpenSiteModel(ifcPath);
                Trace($"  site model: converted {step.EntryName} in {converting.Elapsed.TotalSeconds:0.0} s, to link it.");
                converted.SaveAs(companionRvt);
            }
            catch (Exception ex) when (ex is Autodesk.Revit.Exceptions.ApplicationException or IOException)
            {
                Say($"Could not convert the IFC site model for linking ({step.EntryName}): {ex.Message}");
                if (SiteModelRefusal(step.EntryName, errorsBefore, "the site model was not linked") is { } why)
                {
                    Say(why);
                }

                return;
            }
            finally
            {
                converted?.Close(false);
            }
        }

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
            MoveToAnchor(RevitLinkInstance.Create(_document, result.ElementId).Id, "IFC site link");
        }

        if (!CommitAndReport(transaction, swallower))
        {
            return;
        }

        Say(result.ElementId != ElementId.InvalidElementId
            ? $"Linked the IFC site model ({step.EntryName})."
            : $"Revit declined to link the IFC site model ({step.EntryName}).");
    }

    private Document OpenSiteModel(string ifcPath)
    {
        using IFCImportOptions options = new() { Intent = IFCImportIntent.Reference, AutoJoin = false };
        return _application.OpenIFCDocument(ifcPath, options);
    }

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
        MoveToAnchor(RevitLinkInstance.Create(_document, linkTypeId).Id, "IFC site link");

        if (CommitAndReport(transaction, swallower))
        {
            Say("Put the existing IFC site link back into the model — the link type was still in "
                + "Manage Links, but nothing in the project was showing it.");
        }
    }
}
