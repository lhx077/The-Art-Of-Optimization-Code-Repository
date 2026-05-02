// ============================================================================
// Exercise 05 — DI Source Generator
// ============================================================================
// Demonstrates a real IIncrementalGenerator that discovers [Service] annotated
// classes at compile time and emits explicit IServiceCollection registration
// calls, eliminating runtime assembly scanning.
// ============================================================================

using Microsoft.Extensions.DependencyInjection;

Console.WriteLine("=== DI Source Generator Demo ===");
var services = new ServiceCollection();
services.AddGeneratedServices();

foreach (ServiceDescriptor sd in services)
{
    Console.WriteLine($"  {sd.Lifetime}: {sd.ServiceType.Name} -> {sd.ImplementationType?.Name}");
}

[Service(ServiceLifetime.Singleton)]
public sealed class ClockService : IClockService
{
}

public interface IClockService
{
}

[Service(ServiceLifetime.Scoped)]
public sealed class OrderService
{
}
