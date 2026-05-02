// ============================================================================
// Exercise 01 — Parallel Text Search with PLINQ
// ============================================================================
// Searches all text files under a directory tree in parallel and returns file path,
// line number, and line content for every matching line while tolerating file errors.
// ============================================================================

string root = args.Length > 0 ? args[0] : Environment.CurrentDirectory;
string keyword = args.Length > 1 ? args[1] : "async";

IReadOnlyList<SearchResult> results = ParallelTextSearcher.Search(root, keyword, maxDegreeOfParallelism: Environment.ProcessorCount);
Console.WriteLine($"Found {results.Count} matches for '{keyword}'.");
foreach (SearchResult result in results.Take(10))
{
    Console.WriteLine($"{result.FilePath}:{result.LineNumber}: {result.Line}");
}

// Core implementation used by the demo and benchmark code below.
public static class ParallelTextSearcher
{
    public static IReadOnlyList<SearchResult> Search(string rootDirectory, string keyword, int maxDegreeOfParallelism)
    {
        EnumerationOptions options = new()
        {
            IgnoreInaccessible = true,
            RecurseSubdirectories = true
        };

        return Directory.EnumerateFiles(rootDirectory, "*.*", options)
            .Where(IsTextLike)
            .AsParallel()
            .WithDegreeOfParallelism(maxDegreeOfParallelism)
            .SelectMany(path => SearchFile(path, keyword))
            .ToList();
    }

    private static IEnumerable<SearchResult> SearchFile(string path, string keyword)
    {
        List<SearchResult> results = new();

        try
        {
            int lineNumber = 0;
            foreach (string line in File.ReadLines(path))
            {
                lineNumber++;
                if (line.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                {
                    results.Add(new SearchResult(path, lineNumber, line));
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        return results;
    }

    private static bool IsTextLike(string path)
    {
        string extension = Path.GetExtension(path);
        return extension is ".txt" or ".cs" or ".md" or ".json" or ".xml" or ".log";
    }
}

// Small immutable value objects keep measurement and transport code easy to inspect.
public readonly record struct SearchResult(string FilePath, int LineNumber, string Line);
