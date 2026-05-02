// ============================================================================
// Exercise 02 — TPL Dataflow Image Pipeline
// ============================================================================
// Demonstrates a one-to-many image processing pipeline with BroadcastBlock and a
// JoinBlock that collects grayscale, blur, and sharpen results for each image.
// ============================================================================

using System.Threading.Tasks.Dataflow;

string temp = Path.Combine(Path.GetTempPath(), "chapter20-image-pipeline");
Directory.CreateDirectory(temp);

string input1 = Path.Combine(temp, "image1.txt");
string input2 = Path.Combine(temp, "image2.txt");
await File.WriteAllTextAsync(input1, "raw-image-1");
await File.WriteAllTextAsync(input2, "raw-image-2");

await ImagePipeline.RunAsync(new[] { input1, input2 }, temp);
Console.WriteLine($"Output directory: {temp}");

// Core implementation used by the demo and benchmark code below.
public static class ImagePipeline
{
    // Drive the asynchronous demo workflow for the sample.
    public static async Task RunAsync(IEnumerable<string> inputFiles, string outputDirectory)
    {
        ExecutionDataflowBlockOptions parallelOptions = new()
        {
            MaxDegreeOfParallelism = Environment.ProcessorCount,
            BoundedCapacity = 32
        };

        // Give parsing or transformation its own stage so CPU work can scale independently.
        TransformBlock<string, ImageFrame> loadBlock = new(async path =>
        {
            string content = await File.ReadAllTextAsync(path);
            return new ImageFrame(Path.GetFileNameWithoutExtension(path), content);
        }, parallelOptions);

        BroadcastBlock<ImageFrame> broadcast = new(frame => frame);

        // Give parsing or transformation its own stage so CPU work can scale independently.
        TransformBlock<ImageFrame, FilteredImage> grayscale = new(frame => Apply(frame, "grayscale"), parallelOptions);
        // Give parsing or transformation its own stage so CPU work can scale independently.
        TransformBlock<ImageFrame, FilteredImage> blur = new(frame => Apply(frame, "blur"), parallelOptions);
        // Give parsing or transformation its own stage so CPU work can scale independently.
        TransformBlock<ImageFrame, FilteredImage> sharpen = new(frame => Apply(frame, "sharpen"), parallelOptions);

        JoinBlock<FilteredImage, FilteredImage, FilteredImage> join = new(new GroupingDataflowBlockOptions { BoundedCapacity = 32 });

        // Push I/O or final side effects into a dedicated stage so upstream work can stay busy.
        ActionBlock<Tuple<FilteredImage, FilteredImage, FilteredImage>> save = new(async triple =>
        {
            FilteredImage[] items = { triple.Item1, triple.Item2, triple.Item3 };
            string baseName = items[0].FrameName;
            string output = Path.Combine(outputDirectory, baseName + ".processed.txt");
            string content = string.Join(Environment.NewLine, items.Select(i => $"{i.FilterName}: {i.Payload}"));
            await File.WriteAllTextAsync(output, content);
        }, new ExecutionDataflowBlockOptions { MaxDegreeOfParallelism = 2, BoundedCapacity = 16 });

        loadBlock.LinkTo(broadcast, new DataflowLinkOptions { PropagateCompletion = true });
        broadcast.LinkTo(grayscale, new DataflowLinkOptions { PropagateCompletion = true });
        broadcast.LinkTo(blur, new DataflowLinkOptions { PropagateCompletion = true });
        broadcast.LinkTo(sharpen, new DataflowLinkOptions { PropagateCompletion = true });
        grayscale.LinkTo(join.Target1, new DataflowLinkOptions { PropagateCompletion = true });
        blur.LinkTo(join.Target2, new DataflowLinkOptions { PropagateCompletion = true });
        sharpen.LinkTo(join.Target3, new DataflowLinkOptions { PropagateCompletion = true });
        join.LinkTo(save, new DataflowLinkOptions { PropagateCompletion = true });

        foreach (string input in inputFiles)
        {
            await loadBlock.SendAsync(input);
        }

        loadBlock.Complete();
        await save.Completion;
    }

    private static FilteredImage Apply(ImageFrame frame, string filter)
    {
        return new FilteredImage(frame.Name, filter, $"{frame.RawPayload}|{filter}");
    }
}

// Small immutable value objects keep measurement and transport code easy to inspect.
public readonly record struct ImageFrame(string Name, string RawPayload);
// Small immutable value objects keep measurement and transport code easy to inspect.
public readonly record struct FilteredImage(string FrameName, string FilterName, string Payload);
