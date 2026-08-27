#pragma warning disable CS1591
using System.Buffers.Binary;

namespace FileSystem.Refs;

/// <summary>
/// Exact normal-row codec for root #6 / schema 0xe0b0. The modification stamp
/// at +0x10 and trailing dword at +0x81c are intentionally preserved verbatim;
/// their mutation semantics are not guessed.
/// </summary>
internal static class RefsBlockRefcountCodec {
  public const int EntriesPerRow = 0x400;
  public const int NormalValueSize = 0x820;
  public const int EntriesOffset = 0x1C;
  public const ushort CountMask = 0x3FFF;
  public const ushort DedupMetadataMask = 0x4000;
  public const ushort DedupManagedMask = 0x8000;

  public static bool TryGetRange(ReadOnlySpan<byte> value, out ulong start, out ulong count) {
    start = count = 0;
    if (value.Length < NormalValueSize) return false;
    start = BinaryPrimitives.ReadUInt64LittleEndian(value[..8]);
    count = BinaryPrimitives.ReadUInt64LittleEndian(value.Slice(8, 8));
    return count == EntriesPerRow;
  }

  public static ushort ReadRaw(ReadOnlySpan<byte> value, int index) {
    ValidateIndex(value, index);
    return BinaryPrimitives.ReadUInt16LittleEndian(value.Slice(EntriesOffset + index * 2, 2));
  }

  public static ushort ReadCount(ReadOnlySpan<byte> value, int index)
    => (ushort)(ReadRaw(value, index) & CountMask);

  public static bool HasValidTotal(ReadOnlySpan<byte> value) {
    if (value.Length < NormalValueSize) return false;
    uint sum = 0;
    for (var i = 0; i < EntriesPerRow; ++i)
      sum = checked(sum + (uint)(ReadRaw(value, i) & CountMask));
    return sum == BinaryPrimitives.ReadUInt32LittleEndian(value.Slice(0x18, 4));
  }

  public static byte[] AdjustCounts(
      ReadOnlySpan<byte> original,
      IReadOnlyDictionary<int, int> deltas) {
    ArgumentNullException.ThrowIfNull(deltas);
    if (!TryGetRange(original, out _, out _) || !HasValidTotal(original))
      throw new InvalidDataException("ReFS Block Refcount row is not a valid normal 0x820-byte row.");
    var result = original.ToArray();

    foreach (var (index, delta) in deltas) {
      ValidateIndex(result, index);
      if (delta == 0) continue;
      var raw = ReadRaw(result, index);
      var count = raw & CountMask;
      var changed = checked((int)count + delta);
      if (changed is < 0 or > CountMask)
        throw new InvalidOperationException(
          $"ReFS Block Refcount entry {index} would leave the 14-bit count range ({count} {delta:+#;-#;0}).");
      var updated = (ushort)((raw & ~CountMask) | (ushort)changed);
      BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(EntriesOffset + index * 2, 2), updated);
    }

    RefreshTotal(result);
    return result;
  }

  public static bool IsUnflaggedZeroRow(ReadOnlySpan<byte> value) {
    if (!TryGetRange(value, out _, out _) || !HasValidTotal(value)) return false;
    for (var i = 0; i < EntriesPerRow; ++i)
      if (ReadRaw(value, i) != 0) return false;
    return true;
  }

  public static byte[] BuildKey(ulong startVirtualLcn) {
    var key = new byte[16];
    BinaryPrimitives.WriteUInt64LittleEndian(key.AsSpan(0, 8), startVirtualLcn);
    BinaryPrimitives.WriteUInt64LittleEndian(key.AsSpan(8, 8), EntriesPerRow);
    return key;
  }

  public static void RefreshTotal(Span<byte> value) {
    if (value.Length < NormalValueSize)
      throw new InvalidDataException("ReFS Block Refcount value is shorter than 0x820 bytes.");
    uint sum = 0;
    for (var i = 0; i < EntriesPerRow; ++i)
      sum = checked(sum + (uint)(BinaryPrimitives.ReadUInt16LittleEndian(
        value.Slice(EntriesOffset + i * 2, 2)) & CountMask));
    BinaryPrimitives.WriteUInt32LittleEndian(value.Slice(0x18, 4), sum);
  }

  private static void ValidateIndex(ReadOnlySpan<byte> value, int index) {
    if (value.Length < NormalValueSize)
      throw new InvalidDataException("ReFS Block Refcount value is shorter than 0x820 bytes.");
    if ((uint)index >= EntriesPerRow) throw new ArgumentOutOfRangeException(nameof(index));
  }
}
