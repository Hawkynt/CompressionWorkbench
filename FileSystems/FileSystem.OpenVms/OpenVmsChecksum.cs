#pragma warning disable CS1591
using System.Buffers.Binary;

namespace FileSystem.OpenVms;

/// <summary>
/// Files-11 ODS-2 16-bit "checksum1" — additive sum over little-endian
/// 16-bit words, truncated to 16 bits. The same algorithm is applied to:
/// <list type="bullet">
///   <item>The home block (sum of words 0..253; result stored at the
///         home-block-specific HM2$W_CHECKSUM1/CHECKSUM2 slots — out of
///         scope for our writer since VMS-mountability is deferred).</item>
///   <item>Every File Header in INDEXF.SYS (sum of words 0..254;
///         result stored at FH2$W_CHECKSUM = byte offset 510).</item>
/// </list>
/// </summary>
public static class OpenVmsChecksum {
  /// <summary>
  /// Returns the 16-bit additive checksum of <paramref name="data"/> reading
  /// <paramref name="wordCount"/> little-endian unsigned 16-bit words from
  /// the start of the span. Carry is discarded — the result is the low 16
  /// bits of the running 32-bit sum.
  /// </summary>
  public static ushort Compute(ReadOnlySpan<byte> data, int wordCount) {
    ArgumentOutOfRangeException.ThrowIfNegative(wordCount);
    if (wordCount * 2 > data.Length)
      throw new ArgumentException("wordCount × 2 exceeds buffer length", nameof(wordCount));

    uint sum = 0;
    for (var i = 0; i < wordCount; i++)
      sum += BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(i * 2, 2));
    return (ushort)(sum & 0xFFFF);
  }
}
