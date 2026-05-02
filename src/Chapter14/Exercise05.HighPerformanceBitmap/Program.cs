// ============================================================================
// Exercise 05 — High-Performance Bitmap
// ============================================================================
// Stores flags densely in ulong-sized blocks and supports both single-bit and
// range operations. PopCount relies on hardware-accelerated bit counting through
// BitOperations.PopCount when available.
// ============================================================================

using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

var bitmap = new Bitmap(130);
bitmap.Set(1);
bitmap.SetRange(64, 10);
bitmap.Toggle(1);

Console.WriteLine("=== High-Performance Bitmap Demo ===");
Console.WriteLine($"Bit 1: {bitmap.Get(1)}");
Console.WriteLine($"Bit 70: {bitmap.Get(70)}");
Console.WriteLine($"Population count: {bitmap.PopCount()}");

// Core implementation used by the demo and benchmark code below.
public sealed class Bitmap
{
    private readonly ulong[] _words;

    public Bitmap(int bitCount)
    {
        if (bitCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(bitCount));
        }

        BitCount = bitCount;
        _words = new ulong[(bitCount + 63) / 64];
    }

    public int BitCount { get; }

    // Publish updates while keeping the shared state internally consistent.
    public void Set(int index)
    {
        ValidateIndex(index);
        (int wordIndex, int bitOffset) = GetPosition(index);
        // Shift within the target word so one operation updates the desired bit.
        _words[wordIndex] |= 1UL << bitOffset;
    }

    public void Clear(int index)
    {
        ValidateIndex(index);
        (int wordIndex, int bitOffset) = GetPosition(index);
        _words[wordIndex] &= ~(1UL << bitOffset);
    }

    // Serve reads from the current state without unnecessary extra work.
    public bool Get(int index)
    {
        ValidateIndex(index);
        (int wordIndex, int bitOffset) = GetPosition(index);
        return ((_words[wordIndex] >> bitOffset) & 1UL) != 0;
    }

    public void Toggle(int index)
    {
        ValidateIndex(index);
        (int wordIndex, int bitOffset) = GetPosition(index);
        _words[wordIndex] ^= 1UL << bitOffset;
    }

    public void SetRange(int start, int length)
    {
        ApplyRange(start, length, setBits: true);
    }

    public void ClearRange(int start, int length)
    {
        ApplyRange(start, length, setBits: false);
    }

    public int PopCount()
    {
        int total = 0;
        foreach (ulong word in _words)
        {
            total += BitOperations.PopCount(word);
        }

        return total;
    }

    private void ApplyRange(int start, int length, bool setBits)
    {
        if (length < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(length));
        }

        if (length == 0)
        {
            return;
        }

        ValidateIndex(start);
        ValidateIndex(start + length - 1);

        int end = start + length;
        int current = start;

        while (current < end && (current & 63) != 0)
        {
            // Walk the misaligned prefix one bit at a time until the range reaches a word boundary.
            if (setBits) Set(current); else Clear(current);
            current++;
        }

        int fullWordCount = (end - current) / 64;
        if (fullWordCount > 0)
        {
            // Reinterpret the existing bytes directly instead of copying them into another buffer.
            ref ulong wordRef = ref MemoryMarshal.GetArrayDataReference(_words);
            ulong fillValue = setBits ? ulong.MaxValue : 0UL;
            for (int i = 0; i < fullWordCount; i++)
            {
                Unsafe.Add(ref wordRef, (current / 64) + i) = fillValue;
            }

            current += fullWordCount * 64;
        }

        while (current < end)
        {
            // Finish the tail bits after any full-word bulk operation.
            if (setBits) Set(current); else Clear(current);
            current++;
        }
    }

    private static (int WordIndex, int BitOffset) GetPosition(int index)
    {
        return (index >> 6, index & 63);
    }

    private void ValidateIndex(int index)
    {
        if ((uint)index >= (uint)BitCount)
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }
    }
}
