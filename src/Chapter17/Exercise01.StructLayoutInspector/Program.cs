// ============================================================================
// Exercise 01 — Struct Layout Inspector
// ============================================================================
// Uses reflection together with Marshal.OffsetOf and Marshal.SizeOf to print a
// simple topology map for sequential structs. The demo contrasts a poorly ordered
// layout with a manually reordered variant.
// ============================================================================

using System.Reflection;
using System.Runtime.InteropServices;

InspectType<MixedFields>();
Console.WriteLine();
InspectType<ReorderedMixedFields>();

static void InspectType<T>() where T : struct
{
    Type type = typeof(T);
    Console.WriteLine($"=== {type.Name} ===");
    Console.WriteLine($"Total size: {Marshal.SizeOf<T>()} bytes");

    FieldInfo[] fields = type
        .GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
        .OrderBy(field => Marshal.OffsetOf<T>(field.Name).ToInt32())
        .ToArray();

    int current = 0;
    foreach (FieldInfo field in fields)
    {
        int offset = Marshal.OffsetOf<T>(field.Name).ToInt32();
        int size = GetFieldSize(field.FieldType);

        if (offset > current)
        {
            Console.WriteLine($"Padding: offset {current}..{offset - 1} ({offset - current} bytes)");
        }

        Console.WriteLine($"Field {field.Name,-12} offset {offset,2} size {size,2} type {field.FieldType.Name}");
        current = offset + size;
    }

    int totalSize = Marshal.SizeOf<T>();
    if (current < totalSize)
    {
        Console.WriteLine($"Tail padding: offset {current}..{totalSize - 1} ({totalSize - current} bytes)");
    }
}

static int GetFieldSize(Type type)
{
    if (type.IsEnum)
    {
        return Marshal.SizeOf(Enum.GetUnderlyingType(type));
    }

    return Marshal.SizeOf(type);
}

[StructLayout(LayoutKind.Sequential)]
public struct MixedFields
{
    public byte A;
    public double B;
    public short C;
    public int D;
    public byte E;
    public long F;
}

[StructLayout(LayoutKind.Sequential)]
public struct ReorderedMixedFields
{
    public double B;
    public long F;
    public int D;
    public short C;
    public byte A;
    public byte E;
}
