using Autodesk.Revit.DB;
using Autodesk.Revit.DB.ExtensibleStorage;
using MantlePlace.Revit.Core;

namespace MantlePlace.Revit.Addin;

// The attribution step: the "Mantle Place Attribution" drafting view, and the provenance record on
// Project Information. Both are the manifest's words, copied; this file decides nothing about them.
internal sealed partial class RevitBundleImporter
{
    /// <summary>
    /// How wide the note wraps, in paper feet: about 180 mm, the width of an A4 or Letter sheet's
    /// text block, so the view drops onto a sheet without the note running off it.
    /// </summary>
    private const double AttributionNoteWidthFeet = 0.59;

    /// <summary>
    /// Writes the attribution view and the provenance record, in one transaction.
    /// </summary>
    /// <remarks>
    /// One transaction because each half is how the next import reads the other: the record says
    /// which note in the view is this plugin's (<see cref="AttributionView.Decide"/>). A view
    /// rewritten under a record that failed to store would leave the next import unable to find the
    /// note, and it would add a second one.
    /// </remarks>
    private void WriteAttributionAndProvenance(ImportStep step)
    {
        if (step.Provenance is not { } provenance)
        {
            return;
        }

        Schema schema = ProvenanceSchema();
        string? previousText = ReadPreviousProvenance(schema);
        string wantedText = AttributionView.NoteText(provenance.Sources);

        ImportFailureSwallower swallower = new("Writing the attribution");
        using Transaction transaction = BeginTransaction("Mantle Place: attribution", swallower);
        string viewLine = WriteAttributionView(previousText, wantedText, provenance.Sources.Count);
        WriteProvenance(schema, provenance);
        if (!CommitAndReport(transaction, swallower))
        {
            return;
        }

        Say(viewLine);
        Say($"Recorded on Project Information that this project was imported from order {Quoted(provenance.OrderId)}, "
            + $"build {Quoted(provenance.JobId)} (manifest {Quoted(provenance.ManifestVersion)}).");
    }

    /// <summary>
    /// The text the note was written with last time, from the project's own record — and the record
    /// said aloud, which is the evidence that it survives a save and a reopen.
    /// </summary>
    private string? ReadPreviousProvenance(Schema schema)
    {
        Entity entity = _document.ProjectInformation.GetEntity(schema);
        if (!entity.IsValid())
        {
            return null;
        }

        Say($"This project already records an import of order {Quoted(entity.Get<string>(ProvenanceStorage.OrderIdField))}, "
            + $"build {Quoted(entity.Get<string>(ProvenanceStorage.JobIdField))}.");

        return AttributionView.NoteText(
            ProvenanceStorage.DecodeSources(entity.Get<string>(ProvenanceStorage.SourcesField)));
    }

    /// <summary>Carries out <see cref="AttributionView.Decide"/>, and says what it did.</summary>
    private string WriteAttributionView(string? previousText, string wantedText, int sourceCount)
    {
        const string name = AttributionView.ViewName;

        View? existing = new FilteredElementCollector(_document)
            .OfClass(typeof(View))
            .Cast<View>()
            .FirstOrDefault(view => !view.IsTemplate && string.Equals(view.Name, name, StringComparison.Ordinal));

        if (existing is not null and not ViewDrafting)
        {
            return $"A view named \"{name}\" already exists and is not a drafting view, so the attribution was "
                + "not written into it. Rename that view and import again to get one.";
        }

        List<TextNote> notes = existing is null
            ? []
            : [.. new FilteredElementCollector(_document, existing.Id).OfClass(typeof(TextNote)).Cast<TextNote>()];

        AttributionNotePlan plan = AttributionView.Decide(
            [.. notes.Select(note => note.Text)],
            previousText,
            wantedText);

        switch (plan.Action)
        {
            case AttributionNoteAction.NothingToWrite:
                return existing is null
                    ? "The manifest names no data sources, so no attribution view was made."
                    : $"The manifest names no data sources, so the \"{name}\" view was left as it was.";

            case AttributionNoteAction.Keep:
                return $"The \"{name}\" view already lists this bundle's {sourceCount} data source(s).";

            case AttributionNoteAction.Rewrite:
                notes[plan.NoteIndex].Text = wantedText;
                return $"Rewrote the \"{name}\" view for this build: {sourceCount} data source(s).";

            default:
                ViewDrafting? view = existing as ViewDrafting ?? CreateAttributionView();
                if (view is null)
                {
                    return "This project has no drafting view type, so the attribution view was not made.";
                }

                AddAttributionNote(view, wantedText);
                return existing is null
                    ? $"Made the drafting view \"{name}\", listing {sourceCount} data source(s). Place it on a "
                        + "sheet to credit the data."
                    : $"Added a note listing {sourceCount} data source(s) to the \"{name}\" view. No note already "
                        + "in it was this plugin's, so any note there — including one edited by hand — was left "
                        + "as it was.";
        }
    }

    private ViewDrafting? CreateAttributionView()
    {
        ViewFamilyType? drafting = new FilteredElementCollector(_document)
            .OfClass(typeof(ViewFamilyType))
            .Cast<ViewFamilyType>()
            .FirstOrDefault(type => type.ViewFamily == ViewFamily.Drafting);
        if (drafting is null)
        {
            return null;
        }

        ViewDrafting view = ViewDrafting.Create(_document, drafting.Id);
        view.Name = AttributionView.ViewName;
        return view;
    }

    private void AddAttributionNote(ViewDrafting view, string text)
    {
        ElementId typeId = _document.GetDefaultElementTypeId(ElementTypeGroup.TextNoteType);
        double width = Math.Clamp(
            AttributionNoteWidthFeet,
            TextElement.GetMinimumAllowedWidth(_document, typeId),
            TextElement.GetMaximumAllowedWidth(_document, typeId));

        TextNote.Create(_document, view.Id, XYZ.Zero, width, text, typeId);
    }

    private void WriteProvenance(Schema schema, ProjectProvenance provenance)
    {
        Entity entity = new(schema);
        entity.Set(ProvenanceStorage.OrderIdField, provenance.OrderId);
        entity.Set(ProvenanceStorage.JobIdField, provenance.JobId);
        entity.Set(ProvenanceStorage.ManifestVersionField, provenance.ManifestVersion);
        entity.Set(ProvenanceStorage.SourcesField, ProvenanceStorage.EncodeSources(provenance.Sources));
        _document.ProjectInformation.SetEntity(entity);
    }

    /// <summary>
    /// The provenance schema, from this session if a document already brought it in, and built from
    /// the core's constants otherwise.
    /// </summary>
    /// <remarks>
    /// Read access is public, so any tool can ask which bundle a project came from; write access is
    /// this vendor's, so only this add-in can say.
    /// </remarks>
    private static Schema ProvenanceSchema()
    {
        if (Schema.Lookup(ProvenanceStorage.SchemaGuid) is { } existing)
        {
            return existing;
        }

        SchemaBuilder builder = new(ProvenanceStorage.SchemaGuid);
        builder.SetSchemaName(ProvenanceStorage.SchemaName);
        builder.SetVendorId(ProvenanceStorage.VendorId);
        builder.SetReadAccessLevel(AccessLevel.Public);
        builder.SetWriteAccessLevel(AccessLevel.Vendor);
        builder.SetDocumentation("Which Mantle Place bundle this project was imported from, and its data sources.");
        builder.AddSimpleField(ProvenanceStorage.OrderIdField, typeof(string));
        builder.AddSimpleField(ProvenanceStorage.JobIdField, typeof(string));
        builder.AddSimpleField(ProvenanceStorage.ManifestVersionField, typeof(string));
        builder.AddSimpleField(ProvenanceStorage.SourcesField, typeof(string));
        return builder.Finish();
    }

    private static string Quoted(string? value) => string.IsNullOrEmpty(value) ? "(none)" : value;
}
