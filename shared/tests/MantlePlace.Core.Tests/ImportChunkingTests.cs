using MantlePlace.Core;

namespace MantlePlace.Core.Tests;

internal static class ImportChunkingTests
{
    internal static int Run()
    {
        TestRun run = new();

        run.Case("nothing to create is no chunks, not one empty transaction", () =>
            run.Equal(ImportChunking.Chunks(0).Count, 0, "no chunks"));

        run.Case("a set smaller than a chunk is one chunk", () =>
        {
            IReadOnlyList<ImportChunk> chunks = ImportChunking.Chunks(44);
            run.Equal(chunks.Count, 1, "one chunk");
            run.True(chunks[0] == new ImportChunk(0, 44), "the whole set");
        });

        run.Case("a large set is cut into full chunks and a short last one", () =>
        {
            int size = ImportChunking.ElementsPerTransaction;
            IReadOnlyList<ImportChunk> chunks = ImportChunking.Chunks((2 * size) + 7);

            run.Equal(chunks.Count, 3, "two full chunks and a remainder");
            run.True(chunks[0] == new ImportChunk(0, size), "first");
            run.True(chunks[1] == new ImportChunk(size, size), "second starts where the first ended");
            run.True(chunks[2] == new ImportChunk(2 * size, 7), "the remainder is last");
        });

        run.Case("every element lands in exactly one chunk", () =>
        {
            const int count = 48_311;
            int next = 0;
            foreach (ImportChunk chunk in ImportChunking.Chunks(count))
            {
                run.Equal(chunk.Start, next, "chunks are contiguous and in order");
                run.True(chunk.Count is > 0 && chunk.Count <= ImportChunking.ElementsPerTransaction, "no chunk is empty or oversize");
                next = chunk.Start + chunk.Count;
            }

            run.Equal(next, count, "the chunks cover the whole set");
        });

        run.Case("the chunk size keeps Cancel prompt without cutting a step into a slice per element", () =>
        {
            run.True(ImportChunking.ElementsPerTransaction >= 50, "not a slice per handful of elements");
            run.True(ImportChunking.ElementsPerTransaction <= 1_000, "small enough that a chunk is a moment");
        });

        run.Case("a negative count is treated as nothing rather than thrown on Revit's thread", () =>
            run.Equal(ImportChunking.Chunks(-3).Count, 0, "no chunks"));

        return run.Report("import chunking");
    }
}
