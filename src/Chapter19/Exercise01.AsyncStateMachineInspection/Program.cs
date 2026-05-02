// ============================================================================
// Exercise 01 — Async State Machine Inspection Guide
// ============================================================================
// Provides sample async methods that can be pasted into SharpLab or examined in
// ILSpy to study generated state-machine fields and transitions.
// ============================================================================

using System.Runtime.CompilerServices;

Console.WriteLine("Open this assembly in ILSpy or paste the methods into SharpLab.");
Console.WriteLine($"SimpleAsync state machine: {GetStateMachineTypeName(typeof(Samples), nameof(Samples.SimpleAsync))}");
Console.WriteLine($"ComplexAsync state machine: {GetStateMachineTypeName(typeof(Samples), nameof(Samples.ComplexAsync))}");

static string GetStateMachineTypeName(Type owner, string methodName)
{
    var method = owner.GetMethod(methodName)!;
    var attribute = method.GetCustomAttributes(typeof(AsyncStateMachineAttribute), false)
        .Cast<AsyncStateMachineAttribute>()
        .Single();
    return attribute.StateMachineType.FullName ?? attribute.StateMachineType.Name;
}

// Core implementation used by the demo and benchmark code below.
public static class Samples
{
    public static async Task<int> SimpleAsync(int value)
    {
        await Task.Delay(10);
        return value * 2;
    }

    public static async Task<string> ComplexAsync(Stream stream, CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[64];

        try
        {
            using MemoryStream copy = new();
            int read = await stream.ReadAsync(buffer, cancellationToken);
            await copy.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            await Task.Yield();
            return Convert.ToHexString(copy.ToArray());
        }
        catch (Exception ex)
        {
            await Task.Delay(1, cancellationToken);
            return ex.GetType().Name;
        }
    }
}
