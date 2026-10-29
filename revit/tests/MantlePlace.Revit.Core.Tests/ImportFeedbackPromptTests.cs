using System.Text;
using MantlePlace.Revit.Client;

namespace MantlePlace.Revit.Core.Tests;

/// <summary>
/// The line the import window shows once a bundle has imported, "Did this import come in right?"
/// (<c>HPS-56</c>): whether to ask, the once-per-bundle memory, and the two links Yes and No open.
/// </summary>
/// <remarks>
/// The window only paints what <see cref="ImportFeedbackPrompt"/> returns and CI never builds it
/// (<c>HPS-02</c>), so every rule is decided here: which endings ask, which order ids can be asked
/// about, that a bundle is asked about once and never again, and that a memory that cannot be kept
/// asks nothing rather than asking twice.
/// </remarks>
internal static class ImportFeedbackPromptTests
{
    private const string Order = "3f2b8c1e-9d4a-4e7b-8a61-0c5d2e9f7a14";
    private const string OtherOrder = "0b6c2d5e-9f31-4a7c-8e12-3d4f5a6b7c8d";

    internal static int Run()
    {
        TestRun run = new();
        string sandbox = Path.Combine(Path.GetTempPath(), "mp-feedback-asked-tests-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(sandbox);

        try
        {
            DecisionCases(run);
            MemoryCases(run);
            StoreCases(run, sandbox);
            WordsCases(run);
        }
        finally
        {
            try
            {
                Directory.Delete(sandbox, recursive: true);
            }
            catch (IOException)
            {
                // A leftover temp folder is not a test failure.
            }
        }

        return run.Report("ImportFeedbackPrompt");
    }

    private static void DecisionCases(TestRun run)
    {
        run.Case("an import that ran to its end asks about its bundle", () =>
        {
            run.Equal(ImportFeedbackPrompt.Ask(ImportEnding.Imported, Order, AskedBundles.Empty), Order, "the order id to ask about");
        });

        run.Case("an import that did not run to its end asks nothing", () =>
        {
            foreach (ImportEnding ending in new[] { ImportEnding.Failed, ImportEnding.CancelledWhileRunning, ImportEnding.DismissedBeforeStart })
            {
                run.True(ImportFeedbackPrompt.Ask(ending, Order, AskedBundles.Empty) is null, $"{ending}");
            }
        });

        run.Case("a bundle with no order id that is a UUID has nothing to scope the answer to, so asks nothing", () =>
        {
            foreach (string? bad in new[] { null, "", " ", "42", "not-a-uuid", Order + "x", "{" + Order + "}", Order + "&x=1" })
            {
                run.True(ImportFeedbackPrompt.Ask(ImportEnding.Imported, bad, AskedBundles.Empty) is null, $"order \"{bad}\"");
            }
        });

        run.Case("the order id comes back in its canonical lower-case form", () =>
        {
            run.Equal(
                ImportFeedbackPrompt.Ask(ImportEnding.Imported, Order.ToUpperInvariant(), AskedBundles.Empty),
                Order,
                "canonical");
        });

        run.Case("a bundle already asked about is not asked about again, however its id is cased", () =>
        {
            AskedBundles asked = AskedBundles.Empty.With(Order);

            run.True(ImportFeedbackPrompt.Ask(ImportEnding.Imported, Order, asked) is null, "the same id");
            run.True(ImportFeedbackPrompt.Ask(ImportEnding.Imported, Order.ToUpperInvariant(), asked) is null, "the same id, upper-case");
            run.Equal(ImportFeedbackPrompt.Ask(ImportEnding.Imported, OtherOrder, asked), OtherOrder, "another bundle is still asked");
        });
    }

    private static void MemoryCases(TestRun run)
    {
        run.Case("what is remembered reads back", () =>
        {
            AskedBundles asked = AskedBundles.Empty.With(Order).With(OtherOrder);
            AskedBundles back = AskedBundles.Parse(asked.ToBytes())!;

            run.True(back.Contains(Order), "the first");
            run.True(back.Contains(OtherOrder), "the second");
            run.Equal(back.Count, 2, "nothing else");
        });

        run.Case("remembering the same bundle twice is one entry", () =>
        {
            run.Equal(AskedBundles.Empty.With(Order).With(Order.ToUpperInvariant()).Count, 1, "one entry");
        });

        run.Case("an id that is not a UUID is never remembered", () =>
        {
            run.Equal(AskedBundles.Empty.With("not-a-uuid").Count, 0, "refused");
            run.False(AskedBundles.Empty.Contains("not-a-uuid"), "and not contained");
        });

        run.Case("the memory is bounded, and the oldest bundle is the one forgotten", () =>
        {
            AskedBundles asked = AskedBundles.Empty;
            string first = Guid.NewGuid().ToString("D");
            asked = asked.With(first);

            for (int index = 0; index < AskedBundles.Capacity; index++)
            {
                asked = asked.With(Guid.NewGuid().ToString("D"));
            }

            run.Equal(asked.Count, AskedBundles.Capacity, "at the cap");
            run.False(asked.Contains(first), "the oldest is gone");
        });

        run.Case("a file that is empty, not JSON, not an object or of another shape is no memory", () =>
        {
            foreach (string text in new[] { "", "{ not json", "[]", """{ "asked": ["x"] }""", """{ "shape": 2, "asked": [] }""", """{ "shape": "1" }""" })
            {
                run.True(AskedBundles.Parse(Encoding.UTF8.GetBytes(text)) is null, $"\"{text}\"");
            }

            run.True(AskedBundles.Parse(null) is null, "null");
        });

        run.Case("an entry it does not understand is skipped, and the rest is used", () =>
        {
            AskedBundles back = AskedBundles.Parse(Encoding.UTF8.GetBytes(
                $$"""{ "shape": 1, "asked": ["{{Order}}", 7, "nonsense", null, "{{OtherOrder.ToUpperInvariant()}}"] }"""))!;

            run.Equal(back.Count, 2, "two usable entries");
            run.True(back.Contains(Order), "a UUID");
            run.True(back.Contains(OtherOrder), "a UUID in upper-case, stored canonical");
        });
    }

    private static void StoreCases(TestRun run, string sandbox)
    {
        run.Case("the first import of a bundle claims the question and the second does not", () =>
        {
            FeedbackAskedStore store = new(Path.Combine(sandbox, "asked", "feedback-asked.json"));

            run.Equal(store.Claim(ImportEnding.Imported, Order), Order, "asked the first time");
            run.True(store.Claim(ImportEnding.Imported, Order) is null, "never the second time");
            run.Equal(store.Claim(ImportEnding.Imported, OtherOrder), OtherOrder, "another bundle is still asked");
        });

        run.Case("the memory outlives the process: a new store over the same file does not ask again", () =>
        {
            string path = Path.Combine(sandbox, "outlives.json");
            new FeedbackAskedStore(path).Claim(ImportEnding.Imported, Order);

            run.True(new FeedbackAskedStore(path).Claim(ImportEnding.Imported, Order) is null, "a restart");
        });

        run.Case("an import that did not finish claims nothing and remembers nothing", () =>
        {
            FeedbackAskedStore store = new(Path.Combine(sandbox, "unfinished.json"));

            run.True(store.Claim(ImportEnding.Failed, Order) is null, "failed");
            run.Equal(store.Claim(ImportEnding.Imported, Order), Order, "a later good import still asks");
        });

        run.Case("a damaged file is no memory: the question is asked, and the file is repaired", () =>
        {
            string damaged = Path.Combine(sandbox, "damaged.json");
            File.WriteAllText(damaged, "{ not json");
            FeedbackAskedStore store = new(damaged);

            run.Equal(store.Claim(ImportEnding.Imported, Order), Order, "asked");
            run.True(store.Claim(ImportEnding.Imported, Order) is null, "and remembered");
        });

        run.Case("a memory that cannot be kept asks nothing, rather than asking twice", () =>
        {
            string locked = Path.Combine(sandbox, "locked.json");
            File.WriteAllText(locked, "{}");
            using FileStream hold = new(locked, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

            run.True(new FeedbackAskedStore(locked).Claim(ImportEnding.Imported, Order) is null, "held open by another process");
        });
    }

    private static void WordsCases(TestRun run)
    {
        run.Case("the prompt carries the words the standard fixes", () =>
        {
            run.Equal(ImportFeedbackPrompt.Question, "Did this import come in right?", "the question");
            run.Equal(ImportFeedbackPrompt.Yes, "Yes", "Yes");
            run.Equal(ImportFeedbackPrompt.No, "No", "No");
        });

        run.Case("Yes and No open the form at up and down, scoped to the imported bundle", () =>
        {
            ImportFeedbackLinks links = ImportFeedbackPrompt.Links("https://mantle.place", "0.6.0", "2026", Order);

            run.Equal(
                links.Yes,
                $"https://mantle.place/feedback?source=dcc-revit&v=0.6.0&hv=2026&order={Order}&rating=up",
                "Yes");
            run.Equal(
                links.No,
                $"https://mantle.place/feedback?source=dcc-revit&v=0.6.0&hv=2026&order={Order}&rating=down",
                "No");
        });
    }
}
