// ============================================================================
// Exercise 04 — Trimming Compatible Library
// ============================================================================
// Shows how to isolate reflection behind annotated APIs so trimming analysis can
// preserve the required members without keeping unrelated metadata alive.
// ============================================================================

using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;

Demo.Run();

public static class Demo
{
    public static void Run()
    {
        // The call site explicitly names the type, so the trimmer can preserve only the members this helper requires.
        PluginDescriptor descriptor = TrimSafeActivator.CreateDescriptor<ReportExporter>();
        object instance = TrimSafeActivator.CreateInstance(typeof(ReportExporter));

        Console.WriteLine("=== Trimming-Compatible Library Demo ===");
        Console.WriteLine(JsonSerializer.Serialize(descriptor, TrimDemoJsonContext.Default.PluginDescriptor));
        Console.WriteLine($"Created instance: {instance.GetType().Name}");
    }
}

public static class TrimSafeActivator
{
    public static PluginDescriptor CreateDescriptor<
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicParameterlessConstructor | DynamicallyAccessedMemberTypes.PublicProperties)] T>()
    {
        Type type = typeof(T);
        string[] propertyNames = type.GetProperties().Select(property => property.Name).ToArray();
        return new PluginDescriptor(type.FullName ?? type.Name, propertyNames);
    }

    public static object CreateInstance(
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicParameterlessConstructor)] Type type)
    {
        return Activator.CreateInstance(type)
            ?? throw new InvalidOperationException($"Could not create {type.FullName}.");
    }
}

public sealed class ReportExporter
{
    public string Format { get; set; } = "csv";
    public string Destination { get; set; } = "exports/";
}

public sealed record PluginDescriptor(string TypeName, string[] PublicProperties);

[JsonSerializable(typeof(PluginDescriptor))]
public partial class TrimDemoJsonContext : JsonSerializerContext
{
}
