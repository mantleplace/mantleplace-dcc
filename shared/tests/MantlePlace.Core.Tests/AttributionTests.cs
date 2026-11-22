using System.Xml.Linq;

namespace MantlePlace.Core.Tests;

internal static class AttributionTests
{
    private const string TwoSources = """
        {
          "version": "1.0.1",
          "job_id": "c28c5e1f-52e3-4535-979d-2043c1fa6ebc",
          "order_id": "9d2dfdbf-7a53-4d5f-8514-125edadc7a5f",
          "attribution": {
            "order_id": "c28c5e1f-52e3-4535-979d-2043c1fa6ebc",
            "sources": [
              {
                "provider_id": "naip",
                "attribution_text": "USDA National Agriculture Imagery Program (NAIP)",
                "license": "NAIP imagery is released into the public domain.",
                "license_url": "https://www.fsa.usda.gov/naip",
                "provenance": {}
              },
              {
                "provider_id": "worldcover",
                "attribution_text": "ESA WorldCover 10m 2021 v200 (CC BY 4.0) © ESA WorldCover project",
                "license": "See provider worldcover for license details.",
                "license_url": null,
                "provenance": {}
              }
            ]
          }
        }
        """;

    private const string OrderA = "order-a";

    private const string JobA = "job-a";

    private static AttributionNotePlan Decide(
        AttributionViewFound view,
        IReadOnlyList<string> notes,
        RecordedAttribution? recorded,
        string wanted,
        string orderId = OrderA,
        string jobId = JobA)
        => AttributionView.Decide(view, notes, recorded, orderId, jobId, wanted);

    internal static int Run()
    {
        TestRun run = new();

        run.Case("every source is read verbatim, in the manifest's order", () =>
        {
            BundleManifest manifest = BundleManifestReader.Parse(TwoSources);

            run.Equal(manifest.AttributionSources.Count, 2, "two sources");
            AttributionSource naip = manifest.AttributionSources[0];
            run.Equal(naip.ProviderId, "naip", "provider id");
            run.Equal(naip.AttributionText, "USDA National Agriculture Imagery Program (NAIP)", "attribution text");
            run.Equal(naip.License, "NAIP imagery is released into the public domain.", "licence text");
            run.Equal(naip.LicenseUrl, "https://www.fsa.usda.gov/naip", "licence URL");

            AttributionSource worldcover = manifest.AttributionSources[1];
            run.Equal(worldcover.ProviderId, "worldcover", "second in manifest order");
            run.Equal(
                worldcover.AttributionText,
                "ESA WorldCover 10m 2021 v200 (CC BY 4.0) © ESA WorldCover project",
                "non-ASCII survives");
            run.True(worldcover.LicenseUrl is null, "a null URL is unknown, not an empty string");
        });

        run.Case("an absent or malformed attribution block is no sources, never a refusal", () =>
        {
            BundleManifest bare = BundleManifestReader.Parse("""{"version": "1.0.0"}""");
            run.Equal(bare.AttributionSources.Count, 0, "absent — which the schema says means no source contributed");

            foreach (string block in (string[])["\"none\"", "{}", "{\"sources\": \"naip\"}", "{\"sources\": [1, null]}"])
            {
                BundleManifest manifest = BundleManifestReader.Parse(
                    "{\"version\": \"1.0.0\", \"attribution\": " + block + "}");

                run.Equal(manifest.AttributionSources.Count, 0, $"{block} yields no sources");
                run.Equal(manifest.IsValid, bare.IsValid, $"{block} does not change the verdict");
                run.Equal(manifest.Error, bare.Error, $"{block} adds no refusal");
            }
        });

        run.Case("a source entry that is not an object, or says nothing, is skipped alone", () =>
        {
            BundleManifest manifest = BundleManifestReader.Parse(
                """
                {
                  "version": "1.0.0",
                  "attribution": {
                    "sources": [
                      "naip",
                      {},
                      { "provider_id": "overture", "attribution_text": "© Overture Maps Foundation" }
                    ]
                  }
                }
                """);

            run.Equal(manifest.AttributionSources.Count, 1, "only the source that carries something");
            run.Equal(manifest.AttributionSources[0].ProviderId, "overture", "the one kept");
            run.True(manifest.AttributionSources[0].License is null, "an absent licence stays absent");
        });

        run.Case("the note has one line per source, fields verbatim and in order", () =>
        {
            BundleManifest manifest = BundleManifestReader.Parse(TwoSources);
            string text = AttributionView.NoteText(manifest.AttributionSources);

            string[] lines = text.Split(AttributionView.LineBreak);
            run.Equal(lines.Length, 2, "one line per source");
            run.Equal(
                lines[0],
                "USDA National Agriculture Imagery Program (NAIP) — NAIP imagery is released into the public "
                + "domain. — https://www.fsa.usda.gov/naip",
                "text, licence and URL, as published");
            run.Equal(
                lines[1],
                "ESA WorldCover 10m 2021 v200 (CC BY 4.0) © ESA WorldCover project — See provider worldcover "
                + "for license details.",
                "a missing URL is left out rather than written as a placeholder");
        });

        run.Case("a source with no attribution text still names its provider", () =>
        {
            string line = AttributionView.Line(new AttributionSource("mapterhorn", null, "CC BY", null));

            run.Equal(line, "mapterhorn — CC BY", "the provider id is the only name the manifest gave it");
        });

        run.Case("a line break inside a field does not split one source into two lines", () =>
        {
            string line = AttributionView.Line(
                new AttributionSource("x", "First part\r\nsecond part", "Licence\nline two", null));

            run.Equal(line, "First part second part — Licence line two", "breaks become spaces");
        });

        run.Case("no sources is nothing to write, and no view is made to hold nothing", () =>
        {
            run.Equal(AttributionView.NoteText([]), string.Empty, "empty text");

            AttributionNotePlan plan = Decide(AttributionViewFound.None, [], null, string.Empty);
            run.True(plan.Action == AttributionNoteAction.NothingToWrite, "nothing to write");
            run.False(plan.CreateView, "no empty view");
        });

        run.Case("a first import makes the view and records the text it wrote", () =>
        {
            AttributionNotePlan plan = Decide(AttributionViewFound.None, [], null, "A — B");

            run.True(plan.Action == AttributionNoteAction.Add, "added");
            run.True(plan.CreateView, "into a new view");
            run.Equal(plan.TextToRecord, "A — B", "the record says what the note says");
        });

        run.Case("a view of that name that is not a drafting view is not written into", () =>
        {
            AttributionNotePlan plan = Decide(
                AttributionViewFound.NotADraftingView,
                [],
                new RecordedAttribution(OrderA, "Old — licence"),
                "New — licence");

            run.True(plan.Action == AttributionNoteAction.ViewNameTaken, "refused");
            run.False(plan.CreateView, "and no second view is attempted under a taken name");
            run.Equal(plan.TextToRecord, "Old — licence", "the record keeps pointing at the note that still exists");
        });

        run.Case("a note that already says it is left alone, whatever Revit did to its line breaks", () =>
        {
            AttributionNotePlan plan = Decide(
                AttributionViewFound.DraftingView,
                ["a curator's own note", "A — B\rC — D\r"],
                new RecordedAttribution(OrderA, "an older build's text"),
                "A — B\rC — D");

            run.True(plan.Action == AttributionNoteAction.Keep, "kept");
            run.Equal(plan.NoteIndex, 1, "the matching note");
            run.Equal(plan.TextToRecord, "A — B\rC — D", "and recorded as this build's");
        });

        run.Case("the note this order's earlier build wrote is rewritten when the build changes", () =>
        {
            AttributionNotePlan plan = Decide(
                AttributionViewFound.DraftingView,
                ["a curator's own note", "Old — licence\r"],
                new RecordedAttribution(OrderA, "Old — licence"),
                "New — licence");

            run.True(plan.Action == AttributionNoteAction.Rewrite, "rewritten");
            run.Equal(plan.NoteIndex, 1, "the note the record's text identifies");
            run.False(plan.CreateView, "in the view it is already in");
            run.Equal(plan.TextToRecord, "New — licence", "and the record moves on with it");
        });

        run.Case("the recorded text is what identifies the note, not today's line format", () =>
        {
            AttributionNotePlan plan = Decide(
                AttributionViewFound.DraftingView,
                ["NAIP / public domain"],
                new RecordedAttribution(OrderA, "NAIP / public domain"),
                "NAIP — public domain");

            run.True(plan.Action == AttributionNoteAction.Rewrite, "found by what it says");
        });

        run.Case("another order's note is never rewritten — this order's credits go beside it", () =>
        {
            AttributionNotePlan plan = Decide(
                AttributionViewFound.DraftingView,
                ["Old — licence"],
                new RecordedAttribution(OrderA, "Old — licence"),
                "New — licence",
                orderId: "order-b");

            run.True(plan.Action == AttributionNoteAction.Add, "added beside, not written over");
            run.Equal(plan.TextToRecord, "New — licence", "the record is now B's");
        });

        run.Case("a bundle that names no order rewrites its own note, found by its build", () =>
        {
            AttributionNotePlan plan = Decide(
                AttributionViewFound.DraftingView,
                ["Old — licence"],
                new RecordedAttribution(string.Empty, "Old — licence") { JobId = "job-7" },
                "New — licence",
                orderId: string.Empty,
                jobId: "job-7");

            run.True(plan.Action == AttributionNoteAction.Rewrite, "the same bundle rewrites its note");
            run.Equal(plan.NoteIndex, 0, "in place");
        });

        run.Case("and so does a project whose record an earlier build left, with the job id as its order", () =>
        {
            AttributionNotePlan plan = Decide(
                AttributionViewFound.DraftingView,
                ["Old — licence"],
                new RecordedAttribution("job-7", "Old — licence") { JobId = "job-7" },
                "New — licence",
                orderId: string.Empty,
                jobId: "job-7");

            run.True(plan.Action == AttributionNoteAction.Rewrite, "the upgraded project's note is rewritten, not doubled");
        });

        run.Case("two bundles that name no order never touch each other's note", () =>
        {
            AttributionNotePlan plan = Decide(
                AttributionViewFound.DraftingView,
                ["Old — licence"],
                new RecordedAttribution(string.Empty, "Old — licence") { JobId = "job-7" },
                "New — licence",
                orderId: string.Empty,
                jobId: "job-9");

            run.True(plan.Action == AttributionNoteAction.Add, "a second note, beside the first");

            AttributionNotePlan anonymous = Decide(
                AttributionViewFound.DraftingView,
                ["Old — licence"],
                new RecordedAttribution(string.Empty, "Old — licence"),
                "New — licence",
                orderId: string.Empty,
                jobId: string.Empty);

            run.True(anonymous.Action == AttributionNoteAction.Add, "and a bundle naming neither an order nor a build claims nothing");
        });

        run.Case("a bundle that names its order is still matched by its order, not its build", () =>
        {
            AttributionNotePlan rebuilt = Decide(
                AttributionViewFound.DraftingView,
                ["Old — licence"],
                new RecordedAttribution(OrderA, "Old — licence") { JobId = JobA },
                "New — licence",
                jobId: "job-b");

            run.True(rebuilt.Action == AttributionNoteAction.Rewrite, "a rebuild of the order rewrites its note");

            AttributionNotePlan sameBuild = Decide(
                AttributionViewFound.DraftingView,
                ["Old — licence"],
                new RecordedAttribution("order-b", "Old — licence") { JobId = JobA },
                "New — licence");

            run.True(sameBuild.Action == AttributionNoteAction.Add, "another order's note is not claimed by a build id");
        });

        run.Case("a note a curator edited is not ours any more, so a fresh one is added beside it", () =>
        {
            AttributionNotePlan plan = Decide(
                AttributionViewFound.DraftingView,
                ["Old — licence, and a curator's addition"],
                new RecordedAttribution(OrderA, "Old — licence"),
                "New — licence");

            run.True(plan.Action == AttributionNoteAction.Add, "added, and the edited note is left as it is");
            run.False(plan.CreateView, "into the existing view");
        });

        run.Case("with no record of what was written before, only an exact match is ours", () =>
        {
            AttributionNotePlan plan = Decide(AttributionViewFound.DraftingView, ["Old — licence"], null, "New — licence");

            run.True(plan.Action == AttributionNoteAction.Add, "nothing is claimed on a guess");
        });

        run.Case("no sources this build leaves this order's earlier note where the record can find it", () =>
        {
            AttributionNotePlan plan = Decide(
                AttributionViewFound.DraftingView,
                ["Old — licence"],
                new RecordedAttribution(OrderA, "Old — licence"),
                string.Empty);

            run.True(plan.Action == AttributionNoteAction.NothingToWrite, "nothing written");
            run.Equal(plan.TextToRecord, "Old — licence", "a later build with sources still rewrites it");
        });

        run.Case("the view name is the identity a re-import finds it by", () =>
        {
            run.Equal(AttributionView.ViewName, "Mantle Place Attribution", "the name the issue fixes");
        });

        run.Case("provenance carries the vault order id, not the attribution block's", () =>
        {
            ProjectProvenance provenance = ProjectProvenance.From(BundleManifestReader.Parse(TwoSources));

            run.Equal(provenance.OrderId, "9d2dfdbf-7a53-4d5f-8514-125edadc7a5f", "top-level order_id (HPS-37)");
            run.Equal(provenance.JobId, "c28c5e1f-52e3-4535-979d-2043c1fa6ebc", "the build");
            run.Equal(provenance.ManifestVersion, "1.0.1", "the manifest version, verbatim");
            run.Equal(provenance.Sources.Count, 2, "every source");
        });

        run.Case("the stored sources round-trip, nulls included", () =>
        {
            IReadOnlyList<AttributionSource> sources = BundleManifestReader.Parse(TwoSources).AttributionSources;

            string stored = ProvenanceStorage.EncodeSources(sources);
            IReadOnlyList<AttributionSource> back = ProvenanceStorage.DecodeSources(stored);

            run.Equal(back.Count, 2, "both sources");
            run.True(back.SequenceEqual(sources), "field for field");
            run.True(back[1].LicenseUrl is null, "a null URL comes back null");
            run.Equal(
                AttributionView.NoteText(back),
                AttributionView.NoteText(sources),
                "the stored record rebuilds the note it wrote — which is how a re-import finds it");
        });

        run.Case("an unreadable stored record reads as no sources, never a throw", () =>
        {
            foreach (string? junk in (string?[])[null, string.Empty, "not json", "{}", "[1, \"x\", null]"])
            {
                run.Equal(ProvenanceStorage.DecodeSources(junk).Count, 0, $"'{junk}' reads as nothing");
            }
        });

        run.Case("the schema's names are ones Revit accepts", () =>
        {
            foreach (string name in (string[])[
                ProvenanceStorage.SchemaName,
                ProvenanceStorage.OrderIdField,
                ProvenanceStorage.JobIdField,
                ProvenanceStorage.ManifestVersionField,
                ProvenanceStorage.SourcesField,
                ProvenanceStorage.NoteTextField])
            {
                run.True(
                    name.Length > 0 && char.IsAsciiLetter(name[0]) && name.All(c => char.IsAsciiLetterOrDigit(c) || c == '_'),
                    $"'{name}' is a legal ExtensibleStorage identifier");
            }

            run.True(ProvenanceStorage.SchemaGuid != Guid.Empty, "a real GUID");
        });

        run.Case("the schema's vendor is the add-in manifest's", () =>
        {
            string? dir = RepoTree.Find(
                "revit/src/MantlePlace.Revit.Addin",
                candidate => File.Exists(Path.Combine(candidate, "MantlePlace.addin")));
            if (dir is null)
            {
                run.Fail("could not find the add-in manifest from the test assembly");
                return;
            }

            string? vendor = XDocument.Load(Path.Combine(dir, "MantlePlace.addin"))
                .Descendants("VendorId")
                .FirstOrDefault()?.Value;
            run.Equal(ProvenanceStorage.VendorId, vendor, "one vendor id");
        });

        return run.Report("attribution and provenance");
    }
}
