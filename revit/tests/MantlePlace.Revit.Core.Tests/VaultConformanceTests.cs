using System.Text.Json;
using MantlePlace.Revit.Core;

namespace MantlePlace.Revit.Core.Tests;

/// <summary>
/// Drives the shared corpus' <c>vault</c> group (<c>HPS-18</c> … <c>HPS-25</c>, <c>HPS-48</c>).
/// </summary>
/// <remarks>
/// Fourteen cases, each driving a DIFFERENT entry point — which is why this group needs the
/// <see cref="ConformanceCorpus.UndrivenCases"/> check the single-loop <c>manifest</c> suite does
/// not: a case added upstream would otherwise be loaded and silently ignored.
/// </remarks>
internal static class VaultConformanceTests
{
    internal static int Run()
    {
        TestRun run = new();

        if (ConformanceCorpus.LoadGroup("vault", out List<ConformanceCorpus.CorpusCase> cases) is { } problem)
        {
            run.Fail(problem);
            return run.Report("vault conformance");
        }

        HashSet<string> driven = new(StringComparer.Ordinal);

        foreach (ConformanceCorpus.CorpusCase corpusCase in cases)
        {
            run.Case(corpusCase.Id, () =>
            {
                Drive(run, corpusCase);
                driven.Add(corpusCase.Id);

                foreach (string unasserted in ConformanceCorpus.UnassertedExpectations(
                             corpusCase,
                             ConsumedExpectationKeys))
                {
                    run.Fail(unasserted);
                }

                foreach (string unread in ConformanceCorpus.UnassertedNestedExpectations(corpusCase))
                {
                    run.Fail(unread);
                }
            });
        }

        foreach (string undriven in ConformanceCorpus.UndrivenCases(cases, driven))
        {
            run.Fail($"corpus case '{undriven}' loaded but nothing drove it (HPS-41)");
        }

        return run.Report("vault conformance");
    }

    private static readonly HashSet<string> ConsumedExpectationKeys =
    [
        "itemCount",
        "items",
        "orderIds",
        "warningCount",
        "jobId",
        "alreadyRunning",
        "url",
        "expiresAt",
    ];

    private static void Drive(TestRun run, ConformanceCorpus.CorpusCase corpusCase)
    {
        switch (corpusCase.Id)
        {
            case "vault.list.empty":
            case "vault.list.fullAndLegacy":
            case "vault.list.skipsMalformedRows":
            case "vault.list.wrongTopLevelKey":
            case "vault.reject.notJson":
                DriveListing(run, corpusCase);
                break;

            case "vault.materialize.started":
            case "vault.materialize.alreadyRunning":
            case "vault.materialize.startNoJobId":
            case "vault.materialize.noop":
            case "vault.materialize.queued":
            case "vault.materialize.coalesced":
            case "vault.materialize.activeJobWithoutId":
                DriveMaterializeStart(run, corpusCase);
                break;

            case "vault.download.presigned":
            case "vault.download.missingUrl":
                DrivePresign(run, corpusCase);
                break;

            case "vault.materialize.statusVectors":
                DriveWithVectors(run, corpusCase, DriveStatusVectors);
                break;
            case "vault.materialize.deliveryVectors":
                DriveWithVectors(run, corpusCase, DriveDeliveryVectors);
                break;
            case "vault.materializeTokenList":
                DriveWithVectors(run, corpusCase, DriveTokenList);
                break;

            case "vault.downloadRequestBody":
                DriveWithVectors(run, corpusCase, DriveDownloadRequestBody);
                break;
            case "vault.statusWordBuckets":
                DriveWithVectors(run, corpusCase, DriveStatusWords);
                break;
            case "vault.errorBodyPrecedence":
                DriveWithVectors(run, corpusCase, DriveErrorPrecedence);
                break;

            default:
                run.Fail($"no driver for corpus case '{corpusCase.Id}' — a case added upstream that nothing "
                    + "here asserts (HPS-41)");
                break;
        }
    }

    private static void DriveWithVectors(
        TestRun run,
        ConformanceCorpus.CorpusCase corpusCase,
        Action<TestRun, VectorNode> driver)
    {
        using VectorDocument vectors = VectorDocument.Parse(corpusCase.Payload);
        driver(run, vectors.Root);

        foreach (string unread in vectors.UnreadPaths())
        {
            run.Fail($"'{unread}' is a vector value nothing in this suite read.");
        }
    }

    private static void DriveListing(TestRun run, ConformanceCorpus.CorpusCase corpusCase)
    {
        string? error = VaultListingReader.TryParse(corpusCase.Payload, out VaultListing listing);

        if (corpusCase.IsReject)
        {
            run.True(error is not null, "rejected");
            if (corpusCase.ErrorContains.Length > 0)
            {
                run.Contains(error, corpusCase.ErrorContains, "names the reason");
            }

            return;
        }

        run.True(error is null, $"accepted ({error})");

        if (ConformanceCorpus.WantsInt(corpusCase, "itemCount", out int itemCount))
        {
            run.Equal(listing.Bundles.Count, itemCount, "item count");
        }

        if (ConformanceCorpus.WantsInt(corpusCase, "warningCount", out int warningCount))
        {
            // ⛔HPS-21: the odd rows were skipped WITH a warning, and the call still succeeded.
            // "One row was odd" and "the response was not a vault listing" have opposite correct
            // responses, and collapsing them tells a paying curator their vault is empty.
            run.Equal(listing.Warnings.Count, warningCount, "warning count");
        }

        if (ConformanceCorpus.WantsRows(corpusCase, "orderIds", out IReadOnlyList<ExpectationNode> orderIds))
        {
            int index = 0;
            foreach (ExpectationNode expected in orderIds)
            {
                run.True(index < listing.Bundles.Count, $"a row survived for orderIds[{index}]");
                if (index < listing.Bundles.Count)
                {
                    run.Equal(listing.Bundles[index].OrderId, expected.AsString(), $"orderIds[{index}]");
                }

                index++;
            }
        }

        if (ConformanceCorpus.WantsRows(corpusCase, "items", out IReadOnlyList<ExpectationNode> items))
        {
            DriveItems(run, listing, items);
        }
    }

    private static void DriveItems(TestRun run, VaultListing listing, IReadOnlyList<ExpectationNode> items)
    {
        int index = 0;
        foreach (ExpectationNode expected in items)
        {
            if (index >= listing.Bundles.Count)
            {
                run.Fail($"items[{index}] has no parsed row");
                index++;
                continue;
            }

            VaultBundle bundle = listing.Bundles[index];
            string where = $"items[{index}]";

            run.Equal(bundle.OrderId, expected.Str("orderId"), $"{where}.orderId");
            run.Equal(bundle.Status.ToString(), expected.Str("status"), $"{where}.status");
            run.Equal(bundle.AoiLabel, expected.Str("aoiLabel"), $"{where}.aoiLabel");
            run.Equal(bundle.CreatedAt, expected.Str("createdAt"), $"{where}.createdAt");
            run.Equal(bundle.IsDownloadable, expected.Bool("downloadable") ?? false, $"{where}.downloadable");

            if (expected.Double("areaKm2") is { } areaKm2)
            {
                run.Within(bundle.AreaKm2 ?? double.NaN, areaKm2, 1e-9, $"{where}.areaKm2");
            }

            // ⛔HPS-20, and the whole reason the legacy row is in this vector: null means UNKNOWN.
            // A host that coerces it to 0 later compares a real 134 MB download against an expected
            // size of zero and declares a mismatch on a bundle it never knew the size of.
            run.Equal(bundle.Layers is not null, expected.Bool("layersKnown") ?? false, $"{where}.layersKnown");
            run.Equal(bundle.ManifestVersion is not null, expected.Bool("hasManifestVersion") ?? false, $"{where}.hasManifestVersion");
            run.Equal(bundle.SizeBytes is not null, expected.Bool("hasSizeBytes") ?? false, $"{where}.hasSizeBytes");
            run.Equal(bundle.Sha256 is not null, expected.Bool("hasSha256") ?? false, $"{where}.hasSha256");

            if (expected.Object("layers") is { } layers)
            {
                run.True(bundle.Layers is not null, $"{where}.layers is known");
                if (bundle.Layers is { } known)
                {
                    // elevation is known-and-FALSE. Reading "the layers object is present" as "all
                    // three true" passes a laxer check and is wrong.
                    run.Equal(known.Imagery, layers.Bool("imagery") ?? false, $"{where}.layers.imagery");
                    run.Equal(known.Basemap, layers.Bool("basemap") ?? false, $"{where}.layers.basemap");
                    run.Equal(known.Elevation, layers.Bool("elevation") ?? false, $"{where}.layers.elevation");
                }
            }

            if (expected.Version("manifestVersion") is { } manifestVersion)
            {
                run.Equal(bundle.ManifestVersion ?? "(absent)", manifestVersion, $"{where}.manifestVersion");
            }

            if (expected.Double("sizeBytes") is { } sizeBytes)
            {
                run.Equal(bundle.SizeBytes == (long)sizeBytes, true, $"{where}.sizeBytes");
            }

            if (expected.Str("sha256") is { } sha256)
            {
                run.Equal(bundle.Sha256, sha256, $"{where}.sha256");
            }

            DriveFormats(run, bundle, expected, where);

            index++;
        }
    }

    private static void DriveFormats(TestRun run, VaultBundle bundle, ExpectationNode expected, string where)
    {
        if (expected.Items("formats") is { } formats)
        {
            int index = 0;
            foreach (ExpectationNode format in formats)
            {
                run.True(index < bundle.Formats.Count, $"{where}.formats[{index}] parsed");
                if (index < bundle.Formats.Count)
                {
                    run.Equal(bundle.Formats[index], format.AsString(), $"{where}.formats[{index}]");
                }

                index++;
            }

            run.Equal(bundle.Formats.Count, index, $"{where}.formats count");
        }

        if (expected.Items("downloadFormats") is not { } downloadFormats)
        {
            return;
        }

        int position = 0;
        foreach (ExpectationNode format in downloadFormats)
        {
            run.True(position < bundle.DownloadFormats.Count, $"{where}.downloadFormats[{position}] parsed");
            if (position < bundle.DownloadFormats.Count)
            {
                BundleDownloadFormat parsed = bundle.DownloadFormats[position];
                run.Equal(parsed.Format, format.Str("format"), $"{where}.downloadFormats[{position}].format");

                // byteSize 0 means UNRECORDED, not an empty file. A host that hides zero-size
                // formats hides half the download menu.
                run.Equal(
                    parsed.ByteSize == (long)(format.Double("byteSize") ?? -1.0),
                    true,
                    $"{where}.downloadFormats[{position}].byteSize");
            }

            position++;
        }

        run.Equal(bundle.DownloadFormats.Count, position, $"{where}.downloadFormats count");
    }

    private static void DriveMaterializeStart(TestRun run, ConformanceCorpus.CorpusCase corpusCase)
    {
        string? error = MaterializeJobs.TryParseStart(corpusCase.Payload, out MaterializeStart start);

        if (corpusCase.IsReject)
        {
            run.True(error is not null, "rejected");
            run.Contains(error, corpusCase.ErrorContains, "names the reason");
            return;
        }

        run.True(error is null, $"accepted ({error})");

        if (ConformanceCorpus.WantsString(corpusCase, "jobId", out string jobId))
        {
            run.Equal(start.JobId, jobId, "job id");
        }

        if (ConformanceCorpus.WantsBool(corpusCase, "alreadyRunning", out bool alreadyRunning))
        {
            // HPS-24: recognised by BODY SHAPE, not by status code. A single-flight response is a
            // SUCCESS that joins the running job — two curators on one order must not queue two
            // ETL runs.
            run.Equal(start.AlreadyRunning, alreadyRunning, "already running");
        }

        // ⛔ The outcome, not the presence of an id. Two of the platform's five start shapes are
        // successes that name no job at all, and inferring failure from a missing `jobId` is what
        // stopped this host importing any bundle with nothing left to build.
        if (ConformanceCorpus.WantsString(corpusCase, "outcome", out string outcome))
        {
            run.Equal(start.Outcome.ToString(), outcome, "outcome");
        }

        if (ConformanceCorpus.WantsRows(corpusCase, "tokens", out IReadOnlyList<ExpectationNode> tokens))
        {
            run.Equal(start.Tokens.Count, tokens.Count, "token count");
            for (int index = 0; index < tokens.Count && index < start.Tokens.Count; index++)
            {
                run.Equal(start.Tokens[index], tokens[index].AsString(), $"token[{index}]");
            }
        }
    }

    /// <summary>
    /// Drives the delivery-state table: the shape this endpoint actually answers polls with.
    /// </summary>
    /// <remarks>
    /// There is no status word to read, so every row asserts a DERIVED state. The `requested` array
    /// at the top of the file is the yardstick — without it there is nothing to compare `delivered`
    /// against and no way to know a build has finished.
    /// </remarks>
    private static void DriveDeliveryVectors(TestRun run, VectorNode root)
    {
        List<string> requested = [];
        foreach (VectorNode token in root.Items("requested"))
        {
            requested.Add(token.Element.GetString() ?? string.Empty);
        }

        root.MarkConsumed("requested");

        foreach (VectorNode vector in root.Items("vectors"))
        {
            string body = vector.Element.GetProperty("body").GetRawText();
            vector.MarkConsumed("body");

            string? error = MaterializeJobs.TryParseStatus(body, requested, out MaterializeStatus status);
            bool expectParsed = vector.Bool("parseSucceeds") ?? true;

            run.Equal(error is null, expectParsed, $"parse {body}");

            if (!expectParsed)
            {
                run.Contains(error, vector.Str("errorContains")!, "failure names the reason");
                continue;
            }

            run.Equal(status.State.ToString(), vector.Str("state")!, $"state for {body}");
            run.Within(status.Fraction, vector.Double("fraction") ?? double.NaN, 1e-9, $"fraction for {body}");

            if (vector.Str("jobId") is { } jobId)
            {
                run.Equal(status.JobId, jobId, "job id");
            }

            if (vector.Str("messageContains") is { } fragment)
            {
                run.Contains(status.Message, fragment, "message names the cause");
            }

            if (vector.Element.TryGetProperty("unproducible", out JsonElement gaps))
            {
                vector.MarkConsumed("unproducible");
                run.Equal(status.Unproducible.Count, gaps.GetArrayLength(), "permanent gap count");

                int index = 0;
                foreach (JsonElement gap in gaps.EnumerateArray())
                {
                    if (index < status.Unproducible.Count)
                    {
                        run.Equal(status.Unproducible[index].Token, gap.GetString(), $"gap[{index}]");
                    }

                    index++;
                }
            }
        }
    }

    private static void DrivePresign(TestRun run, ConformanceCorpus.CorpusCase corpusCase)
    {
        string? error = PresignedDownloads.TryParse(corpusCase.Payload, out PresignedDownload download);

        if (corpusCase.IsReject)
        {
            run.True(error is not null, "rejected");
            run.Contains(error, corpusCase.ErrorContains, "the platform's own words reach the curator");
            return;
        }

        run.True(error is null, $"accepted ({error})");

        if (ConformanceCorpus.WantsString(corpusCase, "url", out string url))
        {
            run.Equal(download.Url, url, "url");
        }

        if (ConformanceCorpus.WantsString(corpusCase, "expiresAt", out string expiresAt))
        {
            run.Equal(download.ExpiresAt, expiresAt, "expiry");
        }
    }

    private static void DriveStatusVectors(TestRun run, VectorNode root)
    {
        foreach (VectorNode vector in root.Items("vectors"))
        {
            string body = vector.Element.GetProperty("body").GetRawText();
            vector.MarkConsumed("body");

            string? error = MaterializeJobs.TryParseStatus(body, out MaterializeStatus status);
            bool expectParsed = vector.Bool("parseSucceeds") ?? true;

            run.Equal(error is null, expectParsed, $"parse {body}");

            if (!expectParsed)
            {
                run.Contains(error, vector.Str("errorContains")!, "failure names the reason");
                continue;
            }

            run.Equal(status.State.ToString(), vector.Str("state")!, $"state for {body}");

            // Absent progress is INDETERMINATE (-1), not 0. A progress bar sitting at 0% and a
            // spinner say different things to a curator deciding whether to wait.
            run.Within(status.Fraction, vector.Double("fraction") ?? double.NaN, 1e-9, $"fraction for {body}");

            if (vector.Str("jobId") is { } jobId)
            {
                run.Equal(status.JobId, jobId, "job id");
            }

            if (vector.Str("message") is { } message)
            {
                // A failed job is still a VALID body: it parses, and the reason reaches the curator.
                run.Equal(status.Message, message, "failure message");
            }
        }
    }

    private static void DriveTokenList(TestRun run, VectorNode root)
    {
        // The vector's token list is the reference host's. Each host substitutes its own; the SHAPE
        // is the normative part, so that is what gets asserted.
        List<string> referenceTokens = [];
        foreach (VectorNode token in root.Items("tokens"))
        {
            referenceTokens.Add(token.AsString()!);
        }

        run.True(referenceTokens.Count > 0, "the reference host enumerates tokens rather than sending a keyword");

        List<string> explicitBody = [];
        foreach (VectorNode token in root.Obj("bodyForExplicitScope")!.Items("tokens"))
        {
            explicitBody.Add(token.AsString()!);
        }

        run.Equal(
            string.Join(",", explicitBody),
            string.Join(",", referenceTokens),
            "the explicit-scope body is the token list, not a keyword");

        run.Equal(root.Obj("bodyForAllScope")!.Str("tokens")!, "all", "the all-scope body IS the keyword");

        // ⛔HPS-23 for THIS host: an explicit array for its own scope, the keyword only for "all".
        string hostBody = MaterializeJobs.BuildRequestBody(MaterializeJobs.HostScope);
        run.Contains(hostBody, "\"tokens\":[", "this host sends an array for its own scope");
        foreach (string token in MaterializeJobs.RevitTokens)
        {
            run.Contains(hostBody, token, $"and it names {token}");
        }

        run.False(
            hostBody.Contains($"\"{MaterializeJobs.HostScope}\"", StringComparison.Ordinal),
            "and never the host-name keyword, which the server would expand to its own smaller set");
        run.Equal(MaterializeJobs.BuildRequestBody(MaterializeJobs.AllScope), """{"tokens":"all"}""", "all scope");

        foreach (VectorNode scope in root.Items("validScopes"))
        {
            run.True(
                MaterializeJobs.IsValidScope(SubstituteHost(scope.AsString()!)),
                "each valid scope, with this host's name substituted, is valid here");
        }

        foreach (VectorNode vector in root.Items("scopeVectors"))
        {
            string scope = SubstituteHost(vector.Str("scope")!);
            run.Equal(
                MaterializeJobs.IsValidScope(scope),
                vector.Bool("valid") ?? false,
                $"scope '{scope}'");
        }
    }

    private static void DriveDownloadRequestBody(TestRun run, VectorNode root)
    {
        string wholeBundle = root.Str("wholeBundleFormat")!;

        run.Equal(
            PresignedDownloads.WholeBundleFormat,
            wholeBundle,
            "this host names the archive with the corpus's whole-bundle token");

        run.False(
            string.Equals(
                PresignedDownloads.WholeBundleFormat,
                root.Str("deprecatedWholeBundleAlias")!,
                StringComparison.OrdinalIgnoreCase),
            "and never with the deprecated alias, whose meaning depends on the order's own data");

        // ⛔HPS-49: the route validates its body, so "{}" is a 400 and not a default. The reference
        // body is the whole assertion — a host that omits `format` cannot download at all.
        string expectedFormat = root.Obj("body")!.Str("format")!;
        string body = PresignedDownloads.BuildRequestBody();

        run.Equal(body, $$"""{"format":"{{expectedFormat}}"}""", "the presign body names the format");
        run.Contains(body, "\"format\"", "and it is not an empty object");

        foreach (VectorNode vector in root.Items("formatVectors"))
        {
            string format = vector.Str("format")!;
            bool wholeBundleVector = vector.Bool("wholeBundle") ?? false;
            bool presignable = vector.Bool("presignable") ?? false;

            run.Equal(
                string.Equals(format, wholeBundle, StringComparison.OrdinalIgnoreCase),
                wholeBundleVector,
                $"'{format}' names the whole archive: {wholeBundleVector}");

            // This host asks for the archive and nothing else, so it carries no format allow-list to
            // assert against — the reference host does. What binds here is the implication: a token
            // naming the archive must be one the route will presign, or this host cannot download.
            run.True(
                !wholeBundleVector || presignable,
                $"'{format}' naming the archive implies the route presigns it");
        }
    }

    private static void DriveStatusWords(TestRun run, VectorNode root)
    {
        VectorNode bundleStatus = root.Obj("bundleStatus")!;
        foreach (string word in bundleStatus.Keys())
        {
            run.Equal(
                VaultListingReader.ParseStatus(word).ToString(),
                bundleStatus.Str(word)!,
                $"bundle status '{word}'");
        }

        VectorNode materializeState = root.Obj("materializeState")!;
        foreach (string state in materializeState.Keys())
        {
            foreach (VectorNode synonym in materializeState.Items(state))
            {
                string word = synonym.AsString()!;
                run.Equal(MaterializeJobs.ParseState(word).ToString(), state, $"materialize state '{word}'");
            }
        }

        // The prose says case-insensitive and never-an-error; both are then asserted rather than
        // taken on trust.
        run.Contains(root.Str("matching"), "case-insensitive", "the stated matching rule");
        run.Equal(VaultListingReader.ParseStatus("AVAILABLE").ToString(), "Available", "upper-case status word");
        run.Equal(MaterializeJobs.ParseState("PROCESSING").ToString(), "Processing", "upper-case state word");
        run.Equal(VaultListingReader.ParseStatus("brand-new-word").ToString(), "Unknown", "unlisted is Unknown");
        run.Equal(MaterializeJobs.ParseState("brand-new-word").ToString(), "Unknown", "and never an error");
    }

    private static void DriveErrorPrecedence(TestRun run, VectorNode root)
    {
        List<string> precedence = [];
        foreach (VectorNode key in root.Items("keyPrecedence"))
        {
            precedence.Add(key.AsString()!);
        }

        foreach (VectorNode vector in root.Items("vectors"))
        {
            string body = vector.Element.GetProperty("body").GetRawText();
            vector.MarkConsumed("body");

            PlatformError? error = PlatformErrors.FromBody(body);
            bool expectParsed = vector.Bool("parseSucceeds") ?? true;

            run.Equal(error is not null, expectParsed, $"read {body}");

            if (!expectParsed)
            {
                continue;
            }

            run.Equal(error!.Value.Message, vector.Str("message")!, $"message from {body}");
            run.Equal(error.Value.Code, vector.Str("code")!, $"code from {body}");
        }

        // The vectors cover competing pairs; this walks the whole chain, so an implementation that
        // happens to get every listed pair right by accident still has to get the order right.
        for (int skip = 0; skip < precedence.Count; skip++)
        {
            Dictionary<string, string> body = [];
            for (int i = skip; i < precedence.Count; i++)
            {
                body[precedence[i]] = "value-of-" + precedence[i];
            }

            PlatformError? error = PlatformErrors.FromBody(JsonSerializer.Serialize(body));
            run.Equal(
                error?.Message,
                "value-of-" + precedence[skip],
                $"'{precedence[skip]}' wins once the keys above it are gone");
        }
    }

    /// <summary>
    /// Rewrites the reference host's name in a scope to this host's, keeping the capitalisation the
    /// vector was testing — the case-insensitivity is the normative part, so it must survive.
    /// </summary>
    private static string SubstituteHost(string scope)
    {
        if (!scope.Equals("unreal", StringComparison.OrdinalIgnoreCase))
        {
            return scope;
        }

        return scope switch
        {
            "unreal" => MaterializeJobs.HostScope,
            "UNREAL" => MaterializeJobs.HostScope.ToUpperInvariant(),
            _ => char.ToUpperInvariant(MaterializeJobs.HostScope[0]) + MaterializeJobs.HostScope[1..],
        };
    }
}
