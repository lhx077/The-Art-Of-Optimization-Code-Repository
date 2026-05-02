// ============================================================================
// Exercise 04 — Zero-Copy TCP Header Interpretation
// ============================================================================
// Reinterprets a 20-byte TCP header without heap allocations, then converts the
// network-order fields to host-order values through BinaryPrimitives helpers.
// ============================================================================

using System.Buffers.Binary;
using System.Runtime.InteropServices;

byte[] tcpHeaderBytes =
[
    0x30, 0x39,
    0x01, 0xBB,
    0x11, 0x22, 0x33, 0x44,
    0x55, 0x66, 0x77, 0x88,
    0x50, 0x18,
    0x04, 0x00,
    0x12, 0x34,
    0x00, 0x00
];

// Reinterpret the existing bytes directly instead of copying them into another buffer.
TcpHeader raw = MemoryMarshal.AsRef<TcpHeader>(tcpHeaderBytes);
TcpHeaderView view = TcpHeaderView.FromNetwork(raw);

Console.WriteLine("=== TCP Header Demo ===");
Console.WriteLine($"Source Port:      {view.SourcePort}");
Console.WriteLine($"Destination Port: {view.DestinationPort}");
Console.WriteLine($"Sequence Number:  0x{view.SequenceNumber:X8}");
Console.WriteLine($"Ack Number:       0x{view.AcknowledgmentNumber:X8}");
Console.WriteLine($"Header Length:    {view.DataOffsetBytes} bytes");
Console.WriteLine($"Flags:            0x{view.Flags:X3}");
Console.WriteLine($"Window Size:      {view.WindowSize}");
Console.WriteLine($"Checksum:         0x{view.Checksum:X4}");

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct TcpHeader
{
    public ushort SourcePort;
    public ushort DestinationPort;
    public uint SequenceNumber;
    public uint AcknowledgmentNumber;
    public ushort DataOffsetAndFlags;
    public ushort WindowSize;
    public ushort Checksum;
    public ushort UrgentPointer;
}

// Small immutable value objects keep measurement and transport code easy to inspect.
public readonly record struct TcpHeaderView(
    ushort SourcePort,
    ushort DestinationPort,
    uint SequenceNumber,
    uint AcknowledgmentNumber,
    byte DataOffsetBytes,
    ushort Flags,
    ushort WindowSize,
    ushort Checksum,
    ushort UrgentPointer)
{
    public static TcpHeaderView FromNetwork(TcpHeader header)
    {
        ushort sourcePort = BinaryPrimitives.ReverseEndianness(header.SourcePort);
        ushort destinationPort = BinaryPrimitives.ReverseEndianness(header.DestinationPort);
        uint sequence = BinaryPrimitives.ReverseEndianness(header.SequenceNumber);
        uint acknowledgment = BinaryPrimitives.ReverseEndianness(header.AcknowledgmentNumber);
        ushort dataOffsetAndFlags = BinaryPrimitives.ReverseEndianness(header.DataOffsetAndFlags);
        ushort windowSize = BinaryPrimitives.ReverseEndianness(header.WindowSize);
        ushort checksum = BinaryPrimitives.ReverseEndianness(header.Checksum);
        ushort urgentPointer = BinaryPrimitives.ReverseEndianness(header.UrgentPointer);

        byte dataOffsetBytes = (byte)(((dataOffsetAndFlags >> 12) & 0xF) * 4);
        ushort flags = (ushort)(dataOffsetAndFlags & 0x01FF);

        return new TcpHeaderView(
            sourcePort,
            destinationPort,
            sequence,
            acknowledgment,
            dataOffsetBytes,
            flags,
            windowSize,
            checksum,
            urgentPointer);
    }
}
