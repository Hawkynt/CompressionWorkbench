#pragma warning disable CS1591
using System.Buffers.Binary;

namespace FileSystem.Zfs;

/// <summary>
/// ZFS Fletcher-4 checksum (<c>ZIO_CHECKSUM_FLETCHER_4</c>). Process input as little-endian
/// <c>uint32</c> words into four running 64-bit accumulators (a,b,c,d). Input length must
/// be a multiple of 4 bytes.
/// <para>
/// Reference: <c>include/sys/zio_checksum.h</c> and <c>module/zcommon/zfs_fletcher.c</c> in
/// OpenZFS. The result is stored as four LE uint64 values in the <c>blkptr_t.checksum</c>
/// field (little-endian on-disk per ZFS native-endian convention).
/// </para>
/// </summary>
internal static class Fletcher4 {
  /// <summary>A single Fletcher-4 checksum value: four uint64 accumulators.</summary>
  public readonly record struct Value(ulong A, ulong B, ulong C, ulong D) {
    public void WriteLe(Span<byte> dest32) {
      BinaryPrimitives.WriteUInt64LittleEndian(dest32[..8], this.A);
      BinaryPrimitives.WriteUInt64LittleEndian(dest32.Slice(8, 8), this.B);
      BinaryPrimitives.WriteUInt64LittleEndian(dest32.Slice(16, 8), this.C);
      BinaryPrimitives.WriteUInt64LittleEndian(dest32.Slice(24, 8), this.D);
    }

    public static Value ReadLe(ReadOnlySpan<byte> src32) => new(
      BinaryPrimitives.ReadUInt64LittleEndian(src32[..8]),
      BinaryPrimitives.ReadUInt64LittleEndian(src32.Slice(8, 8)),
      BinaryPrimitives.ReadUInt64LittleEndian(src32.Slice(16, 8)),
      BinaryPrimitives.ReadUInt64LittleEndian(src32.Slice(24, 8))
    );
  }

  /// <summary>
  /// Computes Fletcher-4 over <paramref name="data"/>. Length must be a multiple of 4.
  /// </summary>
  public static Value Compute(ReadOnlySpan<byte> data) {
    if ((data.Length & 3) != 0)
      throw new ArgumentException("Fletcher-4 requires length multiple of 4.", nameof(data));
    ulong a = 0, b = 0, c = 0, d = 0;
    for (var i = 0; i < data.Length; i += 4) {
      ulong w = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(i, 4));
      a += w;
      b += a;
      c += b;
      d += c;
    }
    return new Value(a, b, c, d);
  }
}
