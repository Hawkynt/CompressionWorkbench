using System.Buffers.Binary;
using Compression.Core.BitIO;
using Compression.Registry;

namespace Compression.Core.BuildingBlocks;

/// <summary>
/// Clean-room port of the Microsoft DoubleSpace (MS-DOS 6.0 / 6.2) "JM" LZ77
/// compression algorithm used by the DBLS CVF format.
/// <para>
/// The algorithm is an LSB-first variable-bit-length LZ77 with these tokens:
/// </para>
/// <list type="bullet">
///   <item><b>Literal</b> — 1-bit flag <c>0</c> followed by 8 bits of raw byte value.</item>
///   <item><b>Match</b> — 1-bit flag <c>1</c> followed by:
///     <list type="number">
///       <item><b>Length</b> — 2-bit code: <c>00</c>=2, <c>01</c>=3, <c>10</c>=4,
///         <c>11</c>=extended. Extended reads 6 more bits — if all ones (63) then
///         8 further bits are added to a base of 68, giving a maximum length of
///         <see cref="MaxMatchLength"/>.</item>
///       <item><b>Distance</b> — 2-bit class selector followed by class-width bits:
///         class 0 = 6 bits (1..64), class 1 = 8 bits (65..320),
///         class 2 = 12 bits (321..4416), class 3 = 13 bits (4417..12608).
///         DoubleSpace caps at 4096 so class 3 is never emitted.</item>
///     </list>
///   </item>
/// </list>
/// <para>
/// The stream is prefixed with a 4-byte little-endian original-size header so
/// the decoder knows exactly how many bytes to emit (no end-of-block marker
/// is required). Although the on-disk DoubleSpace CVF format uses a separate
/// 2-byte sector header to carry the stored/compressed flag and compressed
/// size, that header is NOT this building block's concern — the CVF writer
/// wraps the output with its own framing.
/// </para>
/// </summary>
public sealed class DoubleSpaceCompressor : IBuildingBlock {

  /// <summary>Sliding-window size for DoubleSpace 6.0 (DBLS) — 4 KiB.</summary>
  internal const int DefaultMaxDistance = 4096;

  /// <summary>Minimum match length emitted as a back-reference.</summary>
  internal const int MinMatchLength = 2;

  /// <summary>Maximum match length (68 base + 255 extended).</summary>
  internal const int MaxMatchLength = 323;

  // Distance class layout: each entry gives {extra bits, base offset}.
  // A distance d is placed in the lowest class whose range covers it.
  internal static readonly (int Bits, int Base, int Max)[] DistanceClasses = [
    (6,    1,    64),
    (8,   65,   320),
    (12, 321,  4416),
    (13, 4417, 12608),
  ];

  private readonly int _maxDistance;

  /// <summary>Creates a compressor using the default DBLS window (4 KiB).</summary>
  public DoubleSpaceCompressor() : this(DefaultMaxDistance) { }

  /// <summary>Creates a compressor with an explicit sliding-window cap.</summary>
  /// <param name="maxDistance">Maximum distance in bytes. Must be in [1, 12608].</param>
  internal DoubleSpaceCompressor(int maxDistance) {
    ArgumentOutOfRangeException.ThrowIfLessThan(maxDistance, 1);
    ArgumentOutOfRangeException.ThrowIfGreaterThan(maxDistance, 12608);
    this._maxDistance = maxDistance;
  }

  /// <inheritdoc/>
  public string Id => "BB_DoubleSpace";

  /// <inheritdoc/>
  public string DisplayName => "DoubleSpace JM";

  /// <inheritdoc/>
  public string Description =>
    "Microsoft DoubleSpace (DBLS) LZ77 — variable-bit length/distance encoding with 4 KiB window";

  /// <inheritdoc/>
  public AlgorithmFamily Family => AlgorithmFamily.Dictionary;

  /// <inheritdoc/>
  public byte[] Compress(ReadOnlySpan<byte> data)
    => CompressCore(data, this._maxDistance);

  /// <inheritdoc/>
  public byte[] Decompress(ReadOnlySpan<byte> data)
    => DecompressCore(data);

  /// <summary>
  /// Compresses <paramref name="data"/> with an explicit sliding-window cap.
  /// Shared entry point used by both <see cref="DoubleSpaceCompressor"/> and
  /// <see cref="DriveSpaceCompressor"/>, and by the DoubleSpace CVF writer
  /// when it needs direct access to a specific window size.
  /// </summary>
  public static byte[] CompressWithWindow(ReadOnlySpan<byte> data, int maxDistance)
    => CompressCore(data, maxDistance);

  /// <summary>
  /// Decompresses a complete DoubleSpace/DriveSpace BB stream (4-byte LE
  /// original-size header followed by the LSB-first token bit stream).
  /// The same decoder handles both variants since they share the token
  /// grammar.
  /// </summary>
  public static byte[] DecompressStream(ReadOnlySpan<byte> data)
    => DecompressCore(data);

  // =========================================================================
  //                              Encoder
  // =========================================================================

  internal static byte[] CompressCore(ReadOnlySpan<byte> data, int maxDistance) {
    using var ms = new MemoryStream();

    // 4-byte LE original-size header.
    Span<byte> header = stackalloc byte[4];
    BinaryPrimitives.WriteInt32LittleEndian(header, data.Length);
    ms.Write(header);

    if (data.Length == 0)
      return ms.ToArray();

    var writer = new BitWriter<LsbBitOrder>(ms);

    // Hash chain built lazily: hashHead[h] = most recent position with hash h;
    // hashNext[p] = previous position with the same hash as p. Both are
    // updated as we walk forward so we never match against the future.
    const int HashSize = 1 << 14;
    var hashHead = new int[HashSize];
    var hashNext = new int[data.Length];
    Array.Fill(hashHead, -1);
    Array.Fill(hashNext, -1);

    var pos = 0;
    while (pos < data.Length) {
      // Insert the current position into the hash chain before matching so
      // we can update skipped-over positions inside a match with the same
      // operation.
      if (pos + 1 < data.Length) {
        var h = Hash2(data, pos);
        hashNext[pos] = hashHead[h];
        hashHead[h] = pos;
      }

      var (bestLen, bestOff) = FindBestMatch(data, pos, maxDistance, hashHead, hashNext);

      if (bestLen >= MinMatchLength) {
        writer.WriteBit(1);
        EncodeLength(writer, bestLen);
        EncodeDistance(writer, bestOff);

        // Update hash chain for every interior byte of the match so later
        // positions can find matches that straddle this one.
        for (var j = 1; j < bestLen && pos + j + 1 < data.Length; j++) {
          var h = Hash2(data, pos + j);
          hashNext[pos + j] = hashHead[h];
          hashHead[h] = pos + j;
        }
        pos += bestLen;
      } else {
        writer.WriteBit(0);
        writer.WriteBits(data[pos], 8);
        pos++;
      }
    }

    writer.FlushBits();
    return ms.ToArray();
  }

  private static int Hash2(ReadOnlySpan<byte> data, int pos)
    => ((data[pos] << 6) ^ data[pos + 1]) & 0x3FFF;

  private static (int Length, int Offset) FindBestMatch(
      ReadOnlySpan<byte> data, int pos, int maxDistance,
      int[] hashHead, int[] hashNext) {
    if (pos + MinMatchLength > data.Length)
      return (0, 0);

    var bestLen = 0;
    var bestOff = 0;
    var minPos = Math.Max(0, pos - maxDistance);
    // hashHead already includes `pos` itself — skip it by starting at hashNext[pos].
    var idx = hashNext[pos];
    var chainLen = 0;
    const int MaxChainLen = 128;

    var maxLen = Math.Min(data.Length - pos, MaxMatchLength);

    while (idx >= minPos && idx < pos && chainLen < MaxChainLen) {
      // Quick reject: only extend if the first two bytes already match.
      if (data[idx] == data[pos] && data[idx + 1] == data[pos + 1]) {
        var len = 2;
        while (len < maxLen && data[idx + len] == data[pos + len])
          len++;
        if (len > bestLen) {
          bestLen = len;
          bestOff = pos - idx;
          if (bestLen >= maxLen) break;
        }
      }
      idx = hashNext[idx];
      chainLen++;
    }
    return (bestLen, bestOff);
  }

  private static void EncodeLength(BitWriter<LsbBitOrder> writer, int length) {
    // length ∈ [2, 323]
    if (length == 2) { writer.WriteBits(0, 2); return; }
    if (length == 3) { writer.WriteBits(1, 2); return; }
    if (length == 4) { writer.WriteBits(2, 2); return; }

    // Extended: 2-bit code 3, then 6 bits (value ∈ [0, 62] → length 5..67).
    writer.WriteBits(3, 2);
    var extended = length - 5;
    if (extended < 63) {
      writer.WriteBits((uint)extended, 6);
      return;
    }

    // Further extended: emit 63 in the 6-bit slot, then 8 bits for [0, 255]
    // added to base 68. length = 68 + tail, so tail = length - 68 ∈ [0, 255].
    writer.WriteBits(63, 6);
    writer.WriteBits((uint)(length - 68), 8);
  }

  private static void EncodeDistance(BitWriter<LsbBitOrder> writer, int distance) {
    for (var cls = 0; cls < DistanceClasses.Length; cls++) {
      var (bits, baseVal, max) = DistanceClasses[cls];
      if (distance <= max) {
        writer.WriteBits((uint)cls, 2);
        writer.WriteBits((uint)(distance - baseVal), bits);
        return;
      }
    }
    throw new InvalidDataException($"DoubleSpace: distance {distance} exceeds maximum class range.");
  }

  // =========================================================================
  //                              Decoder
  // =========================================================================

  internal static byte[] DecompressCore(ReadOnlySpan<byte> data) {
    if (data.Length < 4)
      throw new InvalidDataException("DoubleSpace: input too small for header.");

    var originalSize = BinaryPrimitives.ReadInt32LittleEndian(data);
    if (originalSize < 0)
      throw new InvalidDataException("DoubleSpace: negative original size.");
    if (originalSize == 0)
      return [];

    using var ms = new MemoryStream(data[4..].ToArray());
    var reader = new BitReader<LsbBitOrder>(ms);

    var output = new byte[originalSize];
    var pos = 0;

    while (pos < originalSize) {
      var flag = reader.ReadBit();
      if (flag == 0) {
        output[pos++] = (byte)reader.ReadBits(8);
        continue;
      }

      // Match: decode length then distance.
      var length = DecodeLength(reader);
      var distance = DecodeDistance(reader);

      if (distance < 1 || distance > pos)
        throw new InvalidDataException($"DoubleSpace: invalid distance {distance} at pos {pos}.");
      if (pos + length > originalSize)
        throw new InvalidDataException("DoubleSpace: match would overrun output.");

      var srcPos = pos - distance;
      // Byte-by-byte copy to handle overlapping (RLE-style) runs correctly.
      for (var j = 0; j < length; j++)
        output[pos + j] = output[srcPos + j];
      pos += length;
    }

    return output;
  }

  private static int DecodeLength(BitReader<LsbBitOrder> reader) {
    var code = (int)reader.ReadBits(2);
    if (code < 3)
      return code + 2; // 2, 3, 4

    var extended = (int)reader.ReadBits(6);
    if (extended < 63)
      return 5 + extended; // 5..67

    var tail = (int)reader.ReadBits(8);
    return 68 + tail; // 68..323
  }

  private static int DecodeDistance(BitReader<LsbBitOrder> reader) {
    var cls = (int)reader.ReadBits(2);
    var (bits, baseVal, _) = DistanceClasses[cls];
    var value = (int)reader.ReadBits(bits);
    return baseVal + value;
  }
}
