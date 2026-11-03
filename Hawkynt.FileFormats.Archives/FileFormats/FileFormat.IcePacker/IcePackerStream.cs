using System.Buffers.Binary;
using Compression.Core.Dictionary.MatchFinders;

namespace FileFormat.IcePacker;

/// <summary>
/// Pack-Ice, the Atari ST cruncher of the demo scene. Reads every Pack-Ice generation and writes
/// the 2.31+ format (<c>ICE!</c>).
/// </summary>
/// <remarks>
/// <para>The decoder is a C# conversion of <c>IceDecompressor.cpp</c> from Teemu Suutari's
/// ancient (BSD-2-Clause; see <c>THIRD-PARTY-NOTICE.ancient.txt</c>). The encoder is ours and
/// writes only what that decoder reads.</para>
/// <para>A file is a 12-byte header (<c>ICE!</c>, packed size including the header, unpacked
/// size; big-endian) and a stream the decruncher consumes from its end towards the header. The
/// last byte anchors the bit reader: its lowest set bit is a marker and the bits above it are
/// the first bits. Bits are taken most significant first from bytes read backwards; literal bytes
/// are read from the same backward stream as they are needed. Output is produced from its end
/// towards its start. Each step is a flag bit (1: a literal run follows, its length a cascaded
/// code), then, unless the output is complete, a match: a cascaded length class and its extra bits
/// (2..1033 bytes) and a distance in one of five classes. Pack-Ice 2.0-2.20 (<c>Ice!</c> and its
/// renamed variants) and 1.1-1.14 (trailer <c>Ice!</c>) read 32-bit words and use slightly
/// different length and distance rules; both decode. A trailing picture flag selects the Degas
/// plane-interleave the 2.x packers can apply to 32000-byte screens.</para>
/// </remarks>
public static class IcePackerStream {
  /// <summary>Hash-chain candidates the default compressor inspects per position.</summary>
  public const int DefaultSearchDepth = 64;

  // ── Public API ─────────────────────────────────────────────────────────────

  /// <summary>Decompresses a Pack-Ice file.</summary>
  /// <param name="input">Stream positioned at the start of the file.</param>
  /// <param name="output">Receives the unpacked data.</param>
  /// <exception cref="InvalidDataException">The input is not a valid Pack-Ice file.</exception>
  public static void Decompress(Stream input, Stream output) {
    ArgumentNullException.ThrowIfNull(input);
    ArgumentNullException.ThrowIfNull(output);
    output.Write(DecompressCore(ReadAllBytes(input)));
  }

  /// <summary>Compresses <paramref name="input"/> into a Pack-Ice 2.31+ file.</summary>
  /// <param name="input">The data to compress.</param>
  /// <param name="output">Receives the <c>ICE!</c> file.</param>
  public static void Compress(Stream input, Stream output)
    => Compress(input, output, DefaultSearchDepth);

  /// <summary>
  /// Compresses with the given hash-chain search depth; deeper searches are slower and may find a
  /// smaller parse. The depth is not stored in the file.
  /// </summary>
  /// <param name="input">The data to compress.</param>
  /// <param name="output">Receives the <c>ICE!</c> file.</param>
  /// <param name="searchDepth">Maximum number of hash-chain candidates inspected per position.</param>
  public static void Compress(Stream input, Stream output, int searchDepth) {
    ArgumentNullException.ThrowIfNull(input);
    ArgumentNullException.ThrowIfNull(output);
    ArgumentOutOfRangeException.ThrowIfLessThan(searchDepth, 1);
    output.Write(CompressCore(ReadAllBytes(input), searchDepth));
  }

  /// <summary>Decompresses a Pack-Ice file.</summary>
  /// <param name="data">The complete file.</param>
  /// <returns>The unpacked data.</returns>
  public static byte[] Decompress(ReadOnlySpan<byte> data) => DecompressCore(data);

  /// <summary>Compresses <paramref name="data"/> into a Pack-Ice 2.31+ file.</summary>
  /// <param name="data">The data to compress; must not be empty.</param>
  /// <returns>The <c>ICE!</c> file.</returns>
  public static byte[] Compress(ReadOnlySpan<byte> data) => CompressCore(data, DefaultSearchDepth);

  /// <summary>Compresses with the given hash-chain search depth.</summary>
  /// <param name="data">The data to compress; must not be empty.</param>
  /// <param name="searchDepth">Maximum number of hash-chain candidates inspected per position.</param>
  /// <returns>The <c>ICE!</c> file.</returns>
  public static byte[] Compress(ReadOnlySpan<byte> data, int searchDepth) {
    ArgumentOutOfRangeException.ThrowIfLessThan(searchDepth, 1);
    return CompressCore(data, searchDepth);
  }

  // ── Decompression (conversion of ancient's IceDecompressor) ───────────────

  private static byte[] DecompressCore(ReadOnlySpan<byte> data) {
    if (data.Length < 8)
      throw new InvalidDataException("Input is shorter than a Pack-Ice header.");

    var header = BinaryPrimitives.ReadUInt32BigEndian(data);
    var footer = BinaryPrimitives.ReadUInt32BigEndian(data[^4..]);
    int version, packedSize;
    long rawSize;
    if (footer == IcePackerConstants.MagicV1) {
      version = 0;
      packedSize = data.Length;
      rawSize = BinaryPrimitives.ReadUInt32BigEndian(data[^8..]);
    } else if (header == IcePackerConstants.MagicV2 || header == IcePackerConstants.MagicV1 || Array.IndexOf(IcePackerConstants.MagicV1Aliases, header) >= 0) {
      if (data.Length < IcePackerConstants.HeaderSize)
        throw new InvalidDataException("Input is shorter than a Pack-Ice header.");
      var declared = BinaryPrimitives.ReadUInt32BigEndian(data[4..]);
      if (declared < IcePackerConstants.HeaderSize || declared > data.Length)
        throw new InvalidDataException($"Pack-Ice packed size {declared} does not fit the {data.Length}-byte input.");
      packedSize = (int)declared;
      rawSize = BinaryPrimitives.ReadUInt32BigEndian(data[8..]);
      version = header == IcePackerConstants.MagicV2 ? 2 : 1;
    } else
      throw new InvalidDataException($"Not a Pack-Ice file (header 0x{header:X8}).");

    if (rawSize == 0 || rawSize > Array.MaxLength)
      throw new InvalidDataException($"Pack-Ice unpacked size {rawSize} is out of range.");

    // Pack-Ice 2.0-2.20 share one identifier with two bit-stream layouts; the word-oriented
    // one is tried first, as ancient does.
    if (version == 1) {
      try {
        return DecodeStream(data[..packedSize], version, (int)rawSize, useBytes: false);
      } catch (InvalidDataException) {
        // fall through to the byte-oriented layout
      }
    }

    return DecodeStream(data[..packedSize], version, (int)rawSize, useBytes: version != 0);
  }

  private static byte[] DecodeStream(ReadOnlySpan<byte> packed, int version, int rawSize, bool useBytes) {
    var reader = new BackwardReader(packed, version != 0 ? IcePackerConstants.HeaderSize : 0, version != 0 ? packed.Length : packed.Length - 8, useBytes);
    reader.Anchor();

    var output = new byte[rawSize];
    var position = rawSize;
    while (true) {
      if (reader.Bits(1) != 0) {
        var length = (version != 0 ? LiteralLengthNew(ref reader) : LiteralLengthOld(ref reader)) + 1;
        if (length > position)
          throw new InvalidDataException("Pack-Ice literal run passes the start of the output.");
        for (var i = 0; i < length; ++i)
          output[--position] = reader.Byte();
      }

      if (position == 0)
        break;

      var countBase = Cascade(ref reader, CountBaseBits, CountBaseOffsets);
      var count = countBase switch {
        0 => 0,
        1 => 1,
        2 => 2 + reader.Bits(1),
        3 => 4 + reader.Bits(2),
        _ => 8 + reader.Bits(10),
      } + 2;

      int distance;
      if (count == 2) {
        distance = reader.Bits(1) != 0 ? reader.Bits(9) + 0x40 : reader.Bits(6);
        distance += count - (useBytes ? 1 : 0);
      } else {
        var distanceBase = Cascade(ref reader, DistanceBaseBits, DistanceBaseOffsets);
        if (distanceBase < 2)
          distanceBase ^= 1;
        distance = distanceBase switch {
          0 => reader.Bits(5),
          1 => 32 + reader.Bits(8),
          _ => 288 + reader.Bits(12),
        };
        if (useBytes)
          distance = distance != 0 ? distance + count - 1 : 1;
        else
          distance += count;
      }

      if (distance == 0 || count > position || (long)position + distance > rawSize)
        throw new InvalidDataException("Pack-Ice match reaches outside the output.");
      for (var i = 0; i < count; ++i, --position)
        output[position - 1] = output[position + distance - 1];
    }

    // The picture flag follows the last token when any input is left.
    if (version != 0 && reader.Available > 0 && reader.Bits(1) != 0)
      Deinterleave(ref reader, output, version);

    if (!reader.AtStart)
      throw new InvalidDataException("Pack-Ice stream has bytes left over.");
    return output;
  }

  /// <summary>Undoes the Degas plane-interleave applied to the last screen of the output.</summary>
  private static void Deinterleave(ref BackwardReader reader, byte[] output, int version) {
    var pictureSize = 32000;
    if (version == 2 && reader.Available >= 17 && reader.Bits(1) != 0)
      pictureSize = reader.Bits(16) * 8 + 8;
    if (output.Length < pictureSize)
      throw new InvalidDataException("Pack-Ice picture is larger than the output.");

    Span<ushort> values = stackalloc ushort[4];
    for (var i = output.Length - pictureSize; i < output.Length; i += 8) {
      values.Clear();
      for (var j = 0; j < 8; j += 2) {
        var tmp = BinaryPrimitives.ReadUInt16BigEndian(output.AsSpan(i + 6 - j));
        for (var k = 0; k < 16; ++k) {
          values[k & 3] = (ushort)(values[k & 3] << 1 | tmp >> 15);
          tmp <<= 1;
        }
      }

      for (var j = 0; j < 4; ++j)
        BinaryPrimitives.WriteUInt16BigEndian(output.AsSpan(i + j * 2), values[j]);
    }
  }

  // Cascaded codes: each level reads its bits; an all-ones value escapes to the next level.
  private static readonly int[] LiteralNewBits = [1, 2, 2, 3, 8, 15];
  private static readonly int[] LiteralNewOffsets = Offsets(LiteralNewBits);
  private static readonly int[] LiteralOldBits = [1, 2, 2, 3, 10];
  private static readonly int[] LiteralOldOffsets = Offsets(LiteralOldBits);
  private static readonly int[] CountBaseBits = [1, 1, 1, 1];
  private static readonly int[] CountBaseOffsets = Offsets(CountBaseBits);
  private static readonly int[] DistanceBaseBits = [1, 1];
  private static readonly int[] DistanceBaseOffsets = Offsets(DistanceBaseBits);

  private static int[] Offsets(int[] bits) {
    var offsets = new int[bits.Length];
    for (int i = 0, length = 0; i < bits.Length; length += 1 << bits[i], ++i)
      offsets[i] = length;
    return offsets;
  }

  private static int LiteralLengthNew(ref BackwardReader reader) => Cascade(ref reader, LiteralNewBits, LiteralNewOffsets);

  private static int LiteralLengthOld(ref BackwardReader reader) => Cascade(ref reader, LiteralOldBits, LiteralOldOffsets);

  private static int Cascade(ref BackwardReader reader, int[] bits, int[] offsets) {
    for (var i = 0; ; ++i) {
      var value = reader.Bits(bits[i]);
      if (i == bits.Length - 1 || value != (1 << bits[i]) - 1)
        return offsets[i] - i + value;
    }
  }

  /// <summary>Reads the packed stream from its end: bytes or big-endian words, bits MSB first.</summary>
  private ref struct BackwardReader(ReadOnlySpan<byte> data, int start, int end, bool useBytes) {
    private readonly ReadOnlySpan<byte> _data = data;
    private readonly int _start = start;
    private int _position = end;
    private uint _content;
    private int _length;

    public readonly bool AtStart => this._position == this._start;

    /// <summary>
    /// What ancient's reader reports as available: the buffered bits once every byte is read,
    /// and an effectively unlimited count while bytes remain (its backward stream's count wraps).
    /// </summary>
    public readonly long Available => this._position > this._start ? long.MaxValue : this._length;

    public byte Byte() {
      if (this._position <= this._start)
        throw new InvalidDataException("Pack-Ice stream is truncated.");
      return this._data[--this._position];
    }

    private uint Word() {
      uint b0 = this.Byte(), b1 = this.Byte(), b2 = this.Byte(), b3 = this.Byte();
      return b3 << 24 | b2 << 16 | b1 << 8 | b0;
    }

    /// <summary>The first unit carries the first bits above its lowest set bit, a marker.</summary>
    public void Anchor() {
      var value = useBytes ? this.Byte() : this.Word();
      if (value == 0)
        return;
      var count = 32 - System.Numerics.BitOperations.TrailingZeroCount(value) - 1;
      if (count == 0)
        return;
      this._content = value >> (32 - count);
      this._length = count - (useBytes ? 24 : 0);
    }

    public int Bits(int count) {
      var result = 0u;
      while (count > 0) {
        if (this._length == 0) {
          if (useBytes) {
            this._content = this.Byte();
            this._length = 8;
          } else {
            this._content = this.Word();
            this._length = 32;
          }
        }

        var take = Math.Min(count, this._length);
        this._length -= take;
        result = result << take | (this._content >> this._length) & (uint)((1L << take) - 1);
        count -= take;
      }

      return (int)result;
    }
  }

  // ── Compression (Pack-Ice 2.31+, ICE!) ────────────────────────────────────

  private static byte[] CompressCore(ReadOnlySpan<byte> input, int searchDepth) {
    if (input.IsEmpty)
      throw new ArgumentException("Pack-Ice cannot hold an empty file: its decrunchers reject an unpacked size of zero.", nameof(input));

    // The decruncher fills the output from its end, so a forward parse of the reversed input
    // produces the tokens in the order they are read.
    var reversed = input.ToArray();
    Array.Reverse(reversed);
    var writer = new ConsumptionWriter();
    // The finder masks positions with its window size, which must be a power of two.
    var finder = new HashChainMatchFinder(8192, searchDepth);
    var literalStart = 0;
    var position = 0;
    while (position < reversed.Length) {
      var match = finder.FindMatch(reversed, position, IcePackerConstants.MaxEncodedDistance + 2, Math.Min(IcePackerConstants.MaxMatchLength, reversed.Length - position), 2);
      var (distance, length) = Encodable(match);
      var literals = position - literalStart;
      if (length == 0 && literals < IcePackerConstants.MaxLiteralRun) {
        ++position;
        continue;
      }

      if (length == 0)
        throw new NotSupportedException($"Pack-Ice cannot encode {IcePackerConstants.MaxLiteralRun} bytes without a match that fits its distance limits.");

      WriteLiterals(writer, reversed.AsSpan(literalStart, literals));
      WriteMatch(writer, distance, length);
      for (var i = 1; i < length; ++i)
        finder.InsertPosition(reversed, position + i);
      position += length;
      literalStart = position;
    }

    // After the last match the decruncher reads the literal flag once more.
    WriteLiterals(writer, reversed.AsSpan(literalStart, position - literalStart));
    var stream = writer.ToFileOrder();

    var result = new byte[IcePackerConstants.HeaderSize + stream.Length];
    BinaryPrimitives.WriteUInt32BigEndian(result, IcePackerConstants.MagicV2);
    BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(4), (uint)result.Length);
    BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(8), (uint)input.Length);
    stream.CopyTo(result.AsSpan(IcePackerConstants.HeaderSize));
    return result;
  }

  /// <summary>Trims a candidate to what the 2.31 codes can express, or rejects it.</summary>
  private static (int Distance, int Length) Encodable(Match match) {
    var (distance, length) = (match.Distance, match.Length);
    if (length < 2 || distance < 1)
      return (0, 0);
    // A longer match stores distance - length + 1, which must be positive (distance 1 aside).
    if (distance >= 2 && length > distance)
      length = distance;
    if (length == 2)
      return distance <= IcePackerConstants.MaxDistance2 ? (distance, 2) : (0, 0);
    var encoded = distance == 1 ? 0 : distance - length + 1;
    return encoded <= IcePackerConstants.MaxEncodedDistance ? (distance, length) : (0, 0);
  }

  private static void WriteLiterals(ConsumptionWriter writer, ReadOnlySpan<byte> literals) {
    if (literals.IsEmpty) {
      writer.Bits(0, 1);
      return;
    }

    writer.Bits(1, 1);
    var value = literals.Length - 1;
    for (var i = 0; ; ++i) {
      var bits = LiteralNewBits[i];
      var span = (1 << bits) - 1;
      var local = value - (LiteralNewOffsets[i] - i);
      if (i == LiteralNewBits.Length - 1 || local < span) {
        writer.Bits(local, bits);
        break;
      }

      writer.Bits(span, bits);
    }

    foreach (var literal in literals)
      writer.Byte(literal);
  }

  private static void WriteMatch(ConsumptionWriter writer, int distance, int length) {
    var count = length - 2;
    switch (count) {
      case 0:
        writer.Bits(0b0, 1);
        break;
      case 1:
        writer.Bits(0b10, 2);
        break;
      case < 4:
        writer.Bits(0b110, 3);
        writer.Bits(count - 2, 1);
        break;
      case < 8:
        writer.Bits(0b1110, 4);
        writer.Bits(count - 4, 2);
        break;
      default:
        writer.Bits(0b1111, 4);
        writer.Bits(count - 8, 10);
        break;
    }

    if (length == 2) {
      var encoded = distance - 1;
      if (encoded < 0x40) {
        writer.Bits(0, 1);
        writer.Bits(encoded, 6);
      } else {
        writer.Bits(1, 1);
        writer.Bits(encoded - 0x40, 9);
      }

      return;
    }

    var e = distance == 1 ? 0 : distance - length + 1;
    if (e < 32) {
      writer.Bits(0b10, 2);
      writer.Bits(e, 5);
    } else if (e < 288) {
      writer.Bits(0b0, 1);
      writer.Bits(e - 32, 8);
    } else {
      writer.Bits(0b11, 2);
      writer.Bits(e - 288, 12);
    }
  }

  /// <summary>
  /// Lays bytes out in the order the decruncher consumes them: a bit byte takes its slot when its
  /// first bit is written, literal bytes when they are written. The first slot is the anchor,
  /// which holds seven bits above its marker.
  /// </summary>
  private sealed class ConsumptionWriter {
    private readonly List<byte> _slots = [];
    private int _bitSlot = -1;
    private int _bitsFree;

    public void Byte(byte value) => this._slots.Add(value);

    public void Bits(int value, int count) {
      for (var bit = count - 1; bit >= 0; --bit) {
        if (this._bitsFree == 0) {
          this._bitSlot = this._slots.Count;
          this._slots.Add(0);
          this._bitsFree = this._bitSlot == 0 ? 7 : 8;
        }

        --this._bitsFree;
        if ((value >> bit & 1) != 0)
          this._slots[this._bitSlot] |= (byte)(1 << (this._bitSlot == 0 ? this._bitsFree + 1 : this._bitsFree));
      }
    }

    public byte[] ToFileOrder() {
      // The anchor's marker sits just below its last used bit; unused bits stay zero, which
      // also reads as "no picture" after the last token.
      var anchorBits = this._bitSlot == 0 ? 7 - this._bitsFree : 7;
      this._slots[0] |= (byte)(1 << (7 - anchorBits));
      var file = this._slots.ToArray();
      Array.Reverse(file);
      return file;
    }
  }

  private static byte[] ReadAllBytes(Stream stream) {
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    return ms.ToArray();
  }
}
