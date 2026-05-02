// ============================================================================
// Exercise 02 — Zero-Copy Struct Serializer
// ============================================================================
// Serializes unmanaged payloads with a small header containing a type id,
// version, and endianness marker. Parsing exposes the payload through a ref
// return so the caller can read the underlying bytes without creating a copy.
//
// Endianness for composite structs is handled per-field via the
// IEndiannessReversible<T> static abstract interface, because reversing the
// entire byte span would scramble the field layout. Zero-copy parsing requires
// the payload endianness to match the host machine.
// ============================================================================

using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

Span<byte> buffer = stackalloc byte[PacketSerializer<TradeTick>.GetPacketSize()];
var serializer = new PacketSerializer<TradeTick>(typeId: 7, version: 1);
var payload = new TradeTick(1234, 98.75, 42);
serializer.Write(buffer, payload, Endianness.LittleEndian);

ref readonly TradeTick parsed = ref serializer.Parse(buffer, out PacketHeader header);
Console.WriteLine("=== Zero-Copy Struct Serializer Demo ===");
Console.WriteLine($"TypeId={header.TypeId}, Version={header.Version}, Endianness={header.Endianness}");
Console.WriteLine($"SymbolId={parsed.SymbolId}, Price={parsed.Price}, Quantity={parsed.Quantity}");

public enum Endianness : byte
{
    LittleEndian = 1,
    BigEndian = 2
}

public interface IEndiannessReversible<T> where T : unmanaged
{
    static abstract T ReverseEndianness(T value);
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public readonly struct PacketHeader
{
    public PacketHeader(int typeId, short version, Endianness endianness, int payloadLength)
    {
        TypeId = typeId;
        Version = version;
        Endianness = endianness;
        PayloadLength = payloadLength;
    }

    public int TypeId { get; }
    public short Version { get; }
    public Endianness Endianness { get; }
    public int PayloadLength { get; }
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public readonly struct TradeTick : IEndiannessReversible<TradeTick>
{
    public TradeTick(int symbolId, double price, int quantity)
    {
        SymbolId = symbolId;
        Price = price;
        Quantity = quantity;
    }

    public int SymbolId { get; }
    public double Price { get; }
    public int Quantity { get; }

    public static TradeTick ReverseEndianness(TradeTick value)
    {
        return new TradeTick(
            BinaryPrimitives.ReverseEndianness(value.SymbolId),
            BitConverter.Int64BitsToDouble(
                BinaryPrimitives.ReverseEndianness(
                    BitConverter.DoubleToInt64Bits(value.Price))),
            BinaryPrimitives.ReverseEndianness(value.Quantity));
    }
}

public sealed class PacketSerializer<T> where T : unmanaged, IEndiannessReversible<T>
{
    private const int HeaderSize = 11;
    private readonly int _typeId;
    private readonly short _version;

    public PacketSerializer(int typeId, short version)
    {
        _typeId = typeId;
        _version = version;
    }

    public static int GetPacketSize() => HeaderSize + Unsafe.SizeOf<T>();

    public void Write(Span<byte> destination, T value, Endianness endianness)
    {
        if (destination.Length < GetPacketSize())
        {
            throw new ArgumentException("Destination buffer is too small.", nameof(destination));
        }

        WriteHeader(destination, new PacketHeader(_typeId, _version, endianness, Unsafe.SizeOf<T>()), endianness);

        T toWrite = NeedByteReverse(endianness) ? T.ReverseEndianness(value) : value;
        Span<byte> payloadBytes = destination.Slice(HeaderSize, Unsafe.SizeOf<T>());
        MemoryMarshal.AsBytes(MemoryMarshal.CreateSpan(ref toWrite, 1)).CopyTo(payloadBytes);
    }

    public ref readonly T Parse(ReadOnlySpan<byte> source, out PacketHeader header)
    {
        if (source.Length < GetPacketSize())
        {
            throw new ArgumentException("Source buffer is too small.", nameof(source));
        }

        header = ReadHeader(source);
        if (header.TypeId != _typeId || header.Version != _version || header.PayloadLength != Unsafe.SizeOf<T>())
        {
            throw new InvalidDataException("Header metadata does not match the expected payload type.");
        }

        if (header.Endianness != HostEndianness())
        {
            throw new NotSupportedException("Zero-copy parsing requires payload endianness to match the current machine.");
        }

        ReadOnlySpan<byte> payloadBytes = source.Slice(HeaderSize, Unsafe.SizeOf<T>());
        return ref MemoryMarshal.AsRef<T>(payloadBytes);
    }

    private static void WriteHeader(Span<byte> destination, PacketHeader header, Endianness endianness)
    {
        if (endianness == Endianness.LittleEndian)
        {
            BinaryPrimitives.WriteInt32LittleEndian(destination, header.TypeId);
            BinaryPrimitives.WriteInt16LittleEndian(destination.Slice(4), header.Version);
            destination[6] = (byte)header.Endianness;
            BinaryPrimitives.WriteInt32LittleEndian(destination.Slice(7), header.PayloadLength);
            return;
        }

        BinaryPrimitives.WriteInt32BigEndian(destination, header.TypeId);
        BinaryPrimitives.WriteInt16BigEndian(destination.Slice(4), header.Version);
        destination[6] = (byte)header.Endianness;
        BinaryPrimitives.WriteInt32BigEndian(destination.Slice(7), header.PayloadLength);
    }

    private static PacketHeader ReadHeader(ReadOnlySpan<byte> source)
    {
        Endianness endianness = (Endianness)source[6];
        if (endianness == Endianness.LittleEndian)
        {
            return new PacketHeader(
                BinaryPrimitives.ReadInt32LittleEndian(source),
                BinaryPrimitives.ReadInt16LittleEndian(source.Slice(4)),
                endianness,
                BinaryPrimitives.ReadInt32LittleEndian(source.Slice(7)));
        }

        return new PacketHeader(
            BinaryPrimitives.ReadInt32BigEndian(source),
            BinaryPrimitives.ReadInt16BigEndian(source.Slice(4)),
            endianness,
            BinaryPrimitives.ReadInt32BigEndian(source.Slice(7)));
    }

    private static Endianness HostEndianness() => BitConverter.IsLittleEndian ? Endianness.LittleEndian : Endianness.BigEndian;

    private static bool NeedByteReverse(Endianness payloadEndianness) => payloadEndianness != HostEndianness();
}
