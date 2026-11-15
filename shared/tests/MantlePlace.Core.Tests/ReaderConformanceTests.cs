using System.Text;
using System.Text.Json;
using MantlePlace.Client;

namespace MantlePlace.Core.Tests;

using CorpusCase = ConformanceCorpus.CorpusCase;

/// <summary>
/// Drives the shared corpus' <c>reader</c> group against <see cref="BundleReader"/>: for each case,
/// build the bundle its case file names, read it, and compare what was found with the case's result.
/// </summary>
/// <remarks>
/// <para>
/// The cases are the answer sheet every reader of either major runs (<c>corpus/README.md</c>, "The
/// reader group"), so this suite transcribes nothing: it iterates <c>index.json</c> and builds each
/// bundle from the <c>bundle</c> case file it names (HPS-40). Each bundle is built three ways, as the
/// group says: a folder, a zip, and a zip whose every entry sits in one wrapper folder. The two zips
/// are read through <see cref="LocalBundleArchive.ReadBundle"/>, the path a host takes.
/// </para>
/// <para>
/// Internal so the AutoCAD suite, which compiles these sources in, runs the same cases as AutoCAD.
/// </para>
/// </remarks>
internal static class ReaderConformanceTests
{
    private const string Group = "reader";

    /// <summary>The folder a wrapped zip puts every entry under, as a re-zipped download does.</summary>
    private const string Wrapper = "mantleplace_2026-11-15_corpus/";

    private static readonly string[] Forms = ["folder", "zip", "wrapped"];

    internal static int Run()
    {
        TestRun run = new();

        string? loadError = ConformanceCorpus.LoadGroup(Group, out List<CorpusCase> cases);
        string? root = ConformanceCorpus.FindCorpusDirectory();
        if (loadError is not null || root is null)
        {
            run.Case("corpus loads", () => run.Fail($"conformance corpus unusable: {loadError ?? "not found"}"));
            return run.Report("reader conformance");
        }

        foreach (CorpusCase corpusCase in cases)
        {
            foreach (string form in Forms)
            {
                run.Case($"{corpusCase.Id} ({form})", () => DriveCase(run, corpusCase, root, form));
            }
        }

        return run.Report("reader conformance");
    }

    private static void DriveCase(TestRun run, CorpusCase corpusCase, string root, string form)
    {
        using JsonDocument body = JsonDocument.Parse(corpusCase.Payload);
        string bundleFile = body.RootElement.GetProperty("bundle").GetString() ?? string.Empty;
        JsonElement expected = body.RootElement.GetProperty("result");

        using JsonDocument fixture = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(root, bundleFile.Replace('/', Path.DirectorySeparatorChar))));
        Dictionary<string, byte[]> files = Materialise(fixture.RootElement.GetProperty("files"));

        BundleManifest? manifest = Build(files, form);
        if (manifest is null)
        {
            run.Fail($"the reader found no manifest in the {form} built from {bundleFile}");
            return;
        }

        BundleContents? contents = manifest.Contents;
        if (contents is null)
        {
            run.Fail("the reader's result carries no contents");
            return;
        }

        string outcome = expected.GetProperty("outcome").GetString() ?? string.Empty;
        if (corpusCase.IsReject)
        {
            run.False(manifest.IsValid, "refused");
            run.Contains(manifest.Error, corpusCase.ErrorContains, "the refusal names the version");
        }
        else if (corpusCase.IsAccept)
        {
            run.True(manifest.IsValid, $"read (refused with: {manifest.Error})");
        }
        else
        {
            run.Fail($"unsupported expect '{corpusCase.Expect}' in the reader group");
            return;
        }

        run.Equal(manifest.IsValid ? "read" : "refused", outcome, "outcome");
        run.Equal(contents.Major, expected.GetProperty("major").GetInt32(), "major");
        run.Equal(manifest.Version, expected.GetProperty("version").GetString(), "version");
        run.Equal(contents.ManifestPath, expected.GetProperty("manifestPath").GetString(), "manifestPath");
        run.Equal(LevelToken(contents.Level), NullableString(expected.GetProperty("level")), "level");
        run.Equal(RefusalToken(contents.Refusal), NullableString(expected.GetProperty("refusal")), "refusal");

        CompareRows(
            run,
            "files",
            contents.Files.ToDictionary(
                file => file.Path,
                file => Row(FamilyToken(file.Family), FrameToken(file.Frame), file.Count?.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                StringComparer.Ordinal),
            expected.GetProperty("files").EnumerateArray().ToDictionary(
                row => row.GetProperty("path").GetString()!,
                row => Row(
                    NullableString(row.GetProperty("family")),
                    NullableString(row.GetProperty("frame")),
                    row.GetProperty("count").ValueKind == JsonValueKind.Number ? row.GetProperty("count").GetInt64().ToString(System.Globalization.CultureInfo.InvariantCulture) : null),
                StringComparer.Ordinal));

        CompareRows(
            run,
            "skipped",
            contents.Skipped.ToDictionary(
                skip => skip.Path,
                skip => Row(WhyToken(skip.Why), skip.Reason),
                StringComparer.Ordinal),
            expected.GetProperty("skipped").EnumerateArray().ToDictionary(
                row => row.GetProperty("path").GetString()!,
                row => Row(NullableString(row.GetProperty("why")), NullableString(row.GetProperty("reason"))),
                StringComparer.Ordinal));
    }

    /// <summary>Builds the bundle in <paramref name="form"/> and reads it; the reader's answer, or null.</summary>
    private static BundleManifest? Build(Dictionary<string, byte[]> files, string form)
    {
        string where = Path.Combine(Path.GetTempPath(), "MantlePlace.ReaderCases", Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(where);
        try
        {
            if (string.Equals(form, "folder", StringComparison.Ordinal))
            {
                string folder = Path.Combine(where, "bundle");
                foreach ((string name, byte[] data) in files)
                {
                    string target = Path.Combine(folder, name.Replace('/', Path.DirectorySeparatorChar));
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    File.WriteAllBytes(target, data);
                }

                List<string> entries = [.. Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
                    .Select(path => Path.GetRelativePath(folder, path).Replace(Path.DirectorySeparatorChar, '/'))];
                return BundleReader.Read(
                    entries,
                    entry => File.ReadAllText(Path.Combine(folder, entry.Replace('/', Path.DirectorySeparatorChar))));
            }

            string zipPath = Path.Combine(where, form + ".zip");
            string prefix = string.Equals(form, "wrapped", StringComparison.Ordinal) ? Wrapper : string.Empty;
            using (FileStream stream = File.Create(zipPath))
            using (System.IO.Compression.ZipArchive builder = new(stream, System.IO.Compression.ZipArchiveMode.Create))
            {
                foreach ((string name, byte[] data) in files)
                {
                    using Stream entry = builder.CreateEntry(prefix + name).Open();
                    entry.Write(data);
                }
            }

            using LocalBundleArchive archive = LocalBundleArchive.Open(zipPath, Path.Combine(where, "cache"));
            return archive.ReadBundle();
        }
        finally
        {
            try
            {
                Directory.Delete(where, recursive: true);
            }
            catch (IOException)
            {
                // A scratch folder a virus scanner still holds is left to the temp sweep.
            }
        }
    }

    /// <summary>
    /// A case's <c>files</c> as bytes: a string is the file's text, and anything else is a JSON
    /// document written out, as the corpus's own builder writes a manifest.
    /// </summary>
    private static Dictionary<string, byte[]> Materialise(JsonElement files)
    {
        Dictionary<string, byte[]> bytes = new(StringComparer.Ordinal);
        foreach (JsonProperty file in files.EnumerateObject())
        {
            bytes[file.Name] = file.Value.ValueKind == JsonValueKind.String
                ? Encoding.UTF8.GetBytes(file.Value.GetString()!)
                : Encoding.UTF8.GetBytes(
                    JsonSerializer.Serialize(file.Value, new JsonSerializerOptions { WriteIndented = true }) + "\n");
        }

        return bytes;
    }

    private static void CompareRows(
        TestRun run, string label, Dictionary<string, string> actual, Dictionary<string, string> expected)
    {
        foreach ((string path, string row) in expected)
        {
            if (!actual.TryGetValue(path, out string? found))
            {
                run.Fail($"{label}: {path} is missing; expected {row}");
            }
            else if (!string.Equals(found, row, StringComparison.Ordinal))
            {
                run.Fail($"{label}: {path} is {found}; expected {row}");
            }
        }

        foreach (string path in actual.Keys.Where(path => !expected.ContainsKey(path)).Order(StringComparer.Ordinal))
        {
            run.Fail($"{label}: {path} is reported and the case does not name it");
        }
    }

    private static string Row(params string?[] members) => string.Join(" | ", members.Select(m => m ?? "null"));

    private static string? NullableString(JsonElement value) =>
        value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static string? FamilyToken(FileFamily family) => family switch
    {
        FileFamily.Georeferenced => "georeferenced",
        FileFamily.HostReady => "host-ready",
        _ => null,
    };

    private static string? FrameToken(FileCoordinates frame) => frame switch
    {
        FileCoordinates.Order => "order",
        FileCoordinates.Site => "site",
        _ => null,
    };

    private static string? LevelToken(FidelityLevel? level) => level switch
    {
        FidelityLevel.Max => "Max",
        FidelityLevel.Med => "Med",
        FidelityLevel.Min => "Min",
        null => null,
        _ => level.ToString(),
    };

    private static string? RefusalToken(BundleRefusal refusal) => refusal switch
    {
        BundleRefusal.None => null,
        BundleRefusal.MajorAhead => "major-ahead",
        _ => refusal.ToString(),
    };

    private static string WhyToken(SkipCause why) => why switch
    {
        SkipCause.FileMissing => "file-missing",
        SkipCause.NotHeld => "not-held",
        _ => why.ToString(),
    };
}
