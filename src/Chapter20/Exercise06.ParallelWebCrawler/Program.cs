// ============================================================================
// Exercise 06 — Parallel Web Crawler with Parallel.ForEachAsync and Dataflow
// ============================================================================
// Uses bounded asynchronous fetch concurrency, a TransformBlock for link parsing,
// an ActionBlock for saving pages, and a ConcurrentDictionary to avoid revisits.
// ============================================================================

using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using System.Threading.Tasks.Dataflow;

await Demo.RunAsync();

// The demo keeps the sample runnable without requiring benchmark tooling.
public static class Demo
{
    // Drive the asynchronous demo workflow for the sample.
    public static async Task RunAsync()
    {
        Uri start = new("https://example.com");
        using HttpClient client = new();
        ParallelWebCrawler crawler = new(client, maxDepth: 1, maxPages: 5, maxConcurrency: 4);
        await crawler.CrawlAsync(start);
        Console.WriteLine("Crawl finished.");
    }
}

// Core implementation used by the demo and benchmark code below.
public sealed class ParallelWebCrawler
{
    private readonly HttpClient _httpClient;
    private readonly int _maxDepth;
    private readonly int _maxPages;
    private readonly int _maxConcurrency;
    private readonly ConcurrentDictionary<string, byte> _visited = new(StringComparer.OrdinalIgnoreCase);
    private int _pageCount;

    public ParallelWebCrawler(HttpClient httpClient, int maxDepth, int maxPages, int maxConcurrency)
    {
        _httpClient = httpClient;
        _maxDepth = maxDepth;
        _maxPages = maxPages;
        _maxConcurrency = maxConcurrency;
    }

    // Coordinate the frontier, parsing, and persistence stages for the crawl.
    public async Task CrawlAsync(Uri start)
    {
        // An unbounded channel keeps the sample simple while still separating producers from consumers.
        Channel<CrawlTarget> frontier = Channel.CreateUnbounded<CrawlTarget>();
        // Give parsing or transformation its own stage so CPU work can scale independently.
        TransformBlock<PageContent, PageContent> parseBlock = new(page =>
        {
            page.ExtractedLinks = ExtractLinks(page.BaseUri, page.Html);
            return page;
        }, new ExecutionDataflowBlockOptions { MaxDegreeOfParallelism = Environment.ProcessorCount, BoundedCapacity = 32 });

        // Push I/O or final side effects into a dedicated stage so upstream work can stay busy.
        ActionBlock<PageContent> saveBlock = new(async page =>
        {
            string name = $"crawl-{Interlocked.Increment(ref _pageCount)}.txt";
            string path = Path.Combine(Path.GetTempPath(), name);
            await File.WriteAllTextAsync(path, page.Html);
        }, new ExecutionDataflowBlockOptions { MaxDegreeOfParallelism = 2, BoundedCapacity = 16 });

        parseBlock.LinkTo(saveBlock, new DataflowLinkOptions { PropagateCompletion = true });

        int activeTasks = 0;

        async ValueTask EnqueueAsync(CrawlTarget target, CancellationToken token)
        {
            Interlocked.Increment(ref activeTasks);
            await frontier.Writer.WriteAsync(target, token);
        }

        await EnqueueAsync(new CrawlTarget(start, 0), default);

        await Parallel.ForEachAsync(frontier.Reader.ReadAllAsync(),
            new ParallelOptions { MaxDegreeOfParallelism = _maxConcurrency },
            async (target, token) =>
            {
                try
                {
                    // Read the shared state with visibility guarantees before making decisions.
                    if (target.Depth > _maxDepth || Volatile.Read(ref _pageCount) >= _maxPages)
                    {
                        return;
                    }

                    if (!_visited.TryAdd(target.Uri.AbsoluteUri, 0))
                    {
                        return;
                    }

                    string html = await _httpClient.GetStringAsync(target.Uri, token);
                    PageContent page = new(target.Uri, html);
                    await parseBlock.SendAsync(page, token);

                    if (target.Depth < _maxDepth)
                    {
                        foreach (Uri link in ExtractLinks(target.Uri, html))
                        {
                            if (_visited.Count >= _maxPages)
                            {
                                break;
                            }

                            await EnqueueAsync(new CrawlTarget(link, target.Depth + 1), token);
                        }
                    }
                }
                catch
                {
                }
                finally
                {
                    if (Interlocked.Decrement(ref activeTasks) == 0)
                    {
                        frontier.Writer.TryComplete();
                    }
                }
            });

        parseBlock.Complete();
        await saveBlock.Completion;
    }

    private static List<Uri> ExtractLinks(Uri baseUri, string html)
    {
        List<Uri> links = new();
        foreach (Match match in Regex.Matches(html, "href=\"(?<url>[^\"]+)\"", RegexOptions.IgnoreCase))
        {
            string value = match.Groups["url"].Value;
            if (Uri.TryCreate(baseUri, value, out Uri? uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
            {
                links.Add(uri);
            }
        }

        return links;
    }
}

// Core implementation used by the demo and benchmark code below.
public sealed class PageContent
{
    public Uri BaseUri { get; }
    public string Html { get; }
    public List<Uri> ExtractedLinks { get; set; } = new();

    public PageContent(Uri baseUri, string html)
    {
        BaseUri = baseUri;
        Html = html;
    }
}

// Small immutable value objects keep measurement and transport code easy to inspect.
public readonly record struct CrawlTarget(Uri Uri, int Depth);
