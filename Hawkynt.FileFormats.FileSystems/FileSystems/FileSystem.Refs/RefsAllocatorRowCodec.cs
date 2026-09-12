#pragma warning disable CS1591
using System.Buffers.Binary;

namespace FileSystem.Refs;

/// <summary>
/// Shared exact codec for ReFS allocator row state. Native CoW allocator
/// publication and offline allocator updates must produce identical compact and
/// bitmap encodings, including version-specific Medium Allocator header tags.
/// </summary>
internal static class RefsAllocatorRowCodec {
  public const int BitmapOffset = 0x18;
  public const int BitmapBytes = 2048;
  public const ushort PartialFlag = 0x01;
  public const ushort CompactAllocatedFlag = 0x02;
  public const ushort FullyFreeFlag = 0x05;
  public const ushort FullyFreeAlternativeFlag = 0x09;

  public static bool TryGetRange(ReadOnlySpan<byte> value, out ulong start, out ulong length) {
    start = length = 0;
    if (value.Length < 24) return false;
    start = BinaryPrimitives.ReadUInt64LittleEndian(value[..8]);
    length = BinaryPrimitives.ReadUInt64LittleEndian(value.Slice(8, 8));
    return length is > 0 and <= BitmapBytes * 8UL;
  }

  public static bool ReadAllocated(ReadOnlySpan<byte> value, ulong rangeLength, ulong index) {
    if (index >= rangeLength) throw new InvalidDataException("ReFS allocator index lies outside its row range.");
    if (value.Length < 24) throw new InvalidDataException("ReFS allocator row is shorter than its fixed header.");
    var flags = BinaryPrimitives.ReadUInt16LittleEndian(value.Slice(0x12, 2));
    return flags switch {
      PartialFlag when value.Length >= BitmapOffset + BitmapBytes
        => (value[BitmapOffset + checked((int)(index >> 3))] & (1 << checked((int)(index & 7)))) != 0,
      CompactAllocatedFlag => true,
      FullyFreeFlag or FullyFreeAlternativeFlag => false,
      _ => throw new InvalidDataException($"ReFS allocator row has unsupported flags 0x{flags:X4}/size {value.Length}.")
    };
  }

  public static bool IsStructurallyValid(ReadOnlySpan<byte> value, ulong rangeLength) {
    if (value.Length < 24 || rangeLength == 0 || rangeLength > BitmapBytes * 8UL) return false;
    var free = BinaryPrimitives.ReadUInt16LittleEndian(value.Slice(0x10, 2));
    var flags = BinaryPrimitives.ReadUInt16LittleEndian(value.Slice(0x12, 2));
    var used = BinaryPrimitives.ReadUInt16LittleEndian(value.Slice(0x16, 2));
    if ((ulong)free + used != rangeLength) return false;

    if (flags == PartialFlag) {
      if (value.Length < BitmapOffset + BitmapBytes) return false;
      var popcount = 0;
      for (ulong i = 0; i < rangeLength; ++i)
        if ((value[BitmapOffset + checked((int)(i >> 3))] & (1 << checked((int)(i & 7)))) != 0) ++popcount;
      return popcount == used;
    }
    if (flags == CompactAllocatedFlag) return free == 0 && used == rangeLength;
    if (flags is FullyFreeFlag or FullyFreeAlternativeFlag) return used == 0 && free == rangeLength;
    return false;
  }

  public static byte[] SetAllocated(
      ReadOnlySpan<byte> original,
      ulong rangeLength,
      IReadOnlyCollection<ulong> indices,
      bool allocated,
      RefsAllocatorTier tier,
      uint refsMinorVersion) {
    if (rangeLength == 0 || rangeLength > BitmapBytes * 8UL)
      throw new InvalidOperationException(
        $"ReFS {tier} Allocator row length {rangeLength:N0} exceeds the decoded bitmap capacity.");
    if (!IsStructurallyValid(original, rangeLength))
      throw new InvalidDataException("ReFS allocator row is structurally inconsistent before mutation.");

    var bitmap = new byte[BitmapBytes];
    var flags = BinaryPrimitives.ReadUInt16LittleEndian(original.Slice(0x12, 2));
    switch (flags) {
      case PartialFlag when original.Length >= BitmapOffset + BitmapBytes:
        original.Slice(BitmapOffset, BitmapBytes).CopyTo(bitmap);
        break;
      case CompactAllocatedFlag:
        bitmap.AsSpan().Fill(0xFF);
        break;
      case FullyFreeFlag:
      case FullyFreeAlternativeFlag:
        break;
      default:
        throw new InvalidDataException($"ReFS allocator row has unsupported flags 0x{flags:X4}/size {original.Length}.");
    }

    foreach (var index in indices) {
      if (index >= rangeLength) throw new InvalidOperationException("ReFS allocator bit lies outside its row range.");
      var byteIndex = checked((int)(index >> 3));
      var mask = (byte)(1 << checked((int)(index & 7)));
      if (allocated) bitmap[byteIndex] |= mask;
      else bitmap[byteIndex] &= unchecked((byte)~mask);
    }

    var used = 0;
    for (ulong i = 0; i < rangeLength; ++i)
      if ((bitmap[checked((int)(i >> 3))] & (1 << checked((int)(i & 7)))) != 0) ++used;
    var free = checked((int)rangeLength - used);
    if (used > ushort.MaxValue || free > ushort.MaxValue)
      throw new InvalidOperationException("ReFS allocator counts exceed their on-disk fields.");

    if (used == 0 || free == 0) {
      var compact = new byte[24];
      original[..Math.Min(24, original.Length)].CopyTo(compact);
      BinaryPrimitives.WriteUInt16LittleEndian(compact.AsSpan(0x10, 2), checked((ushort)free));
      BinaryPrimitives.WriteUInt16LittleEndian(compact.AsSpan(0x12, 2), used == 0 ? FullyFreeFlag : CompactAllocatedFlag);
      BinaryPrimitives.WriteUInt16LittleEndian(compact.AsSpan(0x14, 2), HeaderTag(tier, refsMinorVersion, bitmap: false));
      BinaryPrimitives.WriteUInt16LittleEndian(compact.AsSpan(0x16, 2), checked((ushort)used));
      return compact;
    }

    var result = new byte[BitmapOffset + BitmapBytes];
    original[..Math.Min(BitmapOffset, original.Length)].CopyTo(result);
    BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(0x10, 2), checked((ushort)free));
    BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(0x12, 2), PartialFlag);
    BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(0x14, 2), HeaderTag(tier, refsMinorVersion, bitmap: true));
    BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(0x16, 2), checked((ushort)used));
    bitmap.CopyTo(result, BitmapOffset);
    return result;
  }

  private static ushort HeaderTag(RefsAllocatorTier tier, uint refsMinorVersion, bool bitmap) {
    var baseTag = tier == RefsAllocatorTier.Medium && refsMinorVersion >= 7 ? 0x0200 : 0x0100;
    return checked((ushort)(baseTag + (bitmap ? 0x18 : 0)));
  }
}
