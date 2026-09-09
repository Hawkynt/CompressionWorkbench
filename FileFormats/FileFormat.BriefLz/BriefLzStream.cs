/*
 * Managed BriefLZ compatibility implementation for CompressionWorkbench.
 *
 * The on-disk payload syntax is based on Jørgen Ibsen's BriefLZ reference
 * implementation (https://github.com/jibsen/brieflz), copyright (c) 2002-2020
 * Joergen Ibsen, distributed under the zlib License:
 *
 * This software is provided 'as-is', without any express or implied warranty.
 * In no event will the authors be held liable for any damages arising from the
 * use of this software.
 *
 * Permission is granted to anyone to use this software for any purpose,
 * including commercial applications, and to alter it and redistribute it
 * freely, subject to the following restrictions:
 *
 * 1. The origin of this software must not be misrepresented; you must not claim
 *    that you wrote the original software.
 * 2. Altered source versions must be plainly marked as such, and must not be
 *    misrepresented as being the original software.
 * 3. This notice may not be removed or altered from any source distribution.
 *
 * This file is an altered, independently structured managed implementation; it
 * is not the original BriefLZ source.
 */

using System.Buffers.Binary;
using Compression.Core.Checksums;

namespace FileFormat.BriefLz;

/// <summary>
/// Reads and writes the <c>blzpack</c> container used by the BriefLZ reference
/// distribution.
/// </summary>
/// <remarks>
/// <para>
/// A file is a sequence of independently compressed blocks. Each block has a
/// 24-byte big-endian header containing magic, version, compressed/original
/// sizes and optional CRC-32 values. The reference tool defaults to 1 MiB
/// blocks, which this writer follows.
/// </para>
/// <para>
/// The BriefLZ payload itself stores the first literal verbatim, then interleaves
/// little-endian 16-bit tag words with literal/offset bytes. Matches use the
/// reference gamma2 code for <c>length - 2</c> and the high offset bits.
/// </para>
/// </remarks>
public static class BriefLzStream {

  private const uint Magic = 0x626C7A1Au; // "blz\x1A"
  private const uint Version = 1;
  private const int HeaderSize = 24;
  private const int HashBits = 17;
  private const int HashSize = 1 << HashBits;
  private const int MinimumMatchLength = 4;

  /// <summary>The reference <c>blzpack</c> default block size.</summary>
  public const int DefaultBlockSize = 1024 * 1024;

  /// <summary>Fastest managed encoder effort.</summary>
  public const int MinimumCompressionLevel = 1;

  /// <summary>Highest managed encoder effort.</summary>
  public const int MaximumCompressionLevel = 10;

  // Level 1 deliberately examines only the latest hash hit, matching the
  // reference packer's fast parser. Higher levels preserve the same decoder
  // syntax while searching more same-hash candidates for a smaller stream.
  private static readonly int[] SearchDepthByLevel = [1, 2, 4, 8, 16, 32, 64, 96, 224, 1024];

  /// <summary>
  /// Compresses with the reference-compatible fast parser (level 1).
  /// </summary>
  public static void Compress(Stream input, Stream output) =>
    Compress(input, output, MinimumCompressionLevel);

  /// <summary>
  /// Compresses using a managed BriefLZ effort level from 1 (fastest) through
  /// 10 (deepest candidate search).
  /// </summary>
  public static void Compress(Stream input, Stream output, int level) {
    ArgumentNullException.ThrowIfNull(input);
    ArgumentNullException.ThrowIfNull(output);
    ValidateLevel(level);

    var blockBuffer = new byte[DefaultBlockSize];
    while (true) {
      var count = ReadBlock(input, blockBuffer);
      if (count == 0)
        break;

      var source = blockBuffer.AsSpan(0, count).ToArray();
      WriteBlock(output, source, level);
    }
  }

  /// <summary>
  /// Tries every managed effort level and writes the smallest complete
  /// <c>blzpack</c> stream.
  /// </summary>
  public static void CompressOptimal(Stream input, Stream output) {
    ArgumentNullException.ThrowIfNull(input);
    ArgumentNullException.ThrowIfNull(output);

    using var raw = new MemoryStream();
    input.CopyTo(raw);
    var source = raw.ToArray();

    byte[]? best = null;
    for (var level = MinimumCompressionLevel; level <= MaximumCompressionLevel; ++level) {
      using var candidateInput = new MemoryStream(source, writable: false);
      using var candidateOutput = new MemoryStream();
      Compress(candidateInput, candidateOutput, level);
      var bytes = candidateOutput.ToArray();
      if (best is null || bytes.Length < best.Length)
        best = bytes;
    }

    output.Write(best ?? []);
  }

  /// <summary>
  /// Decompresses all concatenated <c>blzpack</c> blocks from
  /// <paramref name="input"/> into <paramref name="output"/>.
  /// </summary>
  public static void Decompress(Stream input, Stream output) {
    ArgumentNullException.ThrowIfNull(input);
    ArgumentNullException.ThrowIfNull(output);

    Span<byte> header = stackalloc byte[HeaderSize];
    while (TryReadHeader(input, header)) {
      var magic = BinaryPrimitives.ReadUInt32BigEndian(header);
      if (magic != Magic)
        throw new InvalidDataException($"Invalid BriefLZ magic: 0x{magic:X8}, expected 0x{Magic:X8}.");

      var version = BinaryPrimitives.ReadUInt32BigEndian(header[4..]);
      if (version != Version)
        throw new InvalidDataException($"Unsupported BriefLZ version: {version}.");

      var compressedSize = BinaryPrimitives.ReadUInt32BigEndian(header[8..]);
      var expectedCompressedCrc = BinaryPrimitives.ReadUInt32BigEndian(header[12..]);
      var uncompressedSize = BinaryPrimitives.ReadUInt32BigEndian(header[16..]);
      var expectedOriginalCrc = BinaryPrimitives.ReadUInt32BigEndian(header[20..]);

      if (compressedSize > int.MaxValue || uncompressedSize > int.MaxValue)
        throw new InvalidDataException("BriefLZ block exceeds the managed implementation's 2 GiB block limit.");

      var compressed = new byte[(int)compressedSize];
      try {
        input.ReadExactly(compressed);
      } catch (EndOfStreamException ex) {
        throw new InvalidDataException("Truncated BriefLZ compressed block.", ex);
      }

      if (expectedCompressedCrc != 0) {
        var actualCompressedCrc = Crc32.Compute(compressed);
        if (actualCompressedCrc != expectedCompressedCrc)
          throw new InvalidDataException(
            $"Compressed data CRC mismatch: 0x{actualCompressedCrc:X8} != 0x{expectedCompressedCrc:X8}.");
      }

      var decompressed = DecompressBlock(compressed, (int)uncompressedSize);

      if (expectedOriginalCrc != 0) {
        var actualOriginalCrc = Crc32.Compute(decompressed);
        if (actualOriginalCrc != expectedOriginalCrc)
          throw new InvalidDataException(
            $"Decompressed data CRC mismatch: 0x{actualOriginalCrc:X8} != 0x{expectedOriginalCrc:X8}.");
      }

      output.Write(decompressed);
    }
  }

  private static int ReadBlock(Stream input, byte[] buffer) {
    var count = 0;
    while (count < buffer.Length) {
      var read = input.Read(buffer, count, buffer.Length - count);
      if (read == 0)
        break;
      count += read;
    }

    return count;
  }

  private static bool TryReadHeader(Stream input, Span<byte> header) {
    var first = input.ReadByte();
    if (first < 0)
      return false;

    header[0] = (byte)first;
    try {
      input.ReadExactly(header[1..]);
    } catch (EndOfStreamException ex) {
      throw new InvalidDataException("Truncated BriefLZ block header.", ex);
    }

    return true;
  }

  private static void WriteBlock(Stream output, byte[] source, int level) {
    var payload = CompressBlock(source, level);
    Span<byte> header = stackalloc byte[HeaderSize];

    BinaryPrimitives.WriteUInt32BigEndian(header, Magic);
    BinaryPrimitives.WriteUInt32BigEndian(header[4..], Version);
    BinaryPrimitives.WriteUInt32BigEndian(header[8..], (uint)payload.Length);
    BinaryPrimitives.WriteUInt32BigEndian(header[12..], Crc32.Compute(payload));
    BinaryPrimitives.WriteUInt32BigEndian(header[16..], (uint)source.Length);
    BinaryPrimitives.WriteUInt32BigEndian(header[20..], Crc32.Compute(source));

    output.Write(header);
    output.Write(payload);
  }

  private static byte[] CompressBlock(byte[] source, int level) {
    if (source.Length == 0)
      return [];

    var writer = new PayloadWriter(source.Length + source.Length / 8 + 64);
    writer.WriteRawByte(source[0]);
    if (source.Length == 1)
      return writer.FinishWithoutTag();

    writer.StartTags();

    var hashHead = new int[HashSize];
    Array.Fill(hashHead, -1);
    var chain = new int[source.Length];
    Array.Fill(chain, -1);

    if (source.Length >= MinimumMatchLength)
      InsertHash(source, 0, hashHead, chain);

    var searchDepth = SearchDepthByLevel[level - 1];
    var position = 1;
    while (position < source.Length) {
      var (matchLength, matchDistance) =
        FindMatch(source, position, hashHead, chain, searchDepth);

      if (ShouldEmitMatch(matchLength, matchDistance)) {
        writer.WriteBit(1);
        writer.WriteGamma2((uint)(matchLength - 2));

        var offsetMinusOne = (uint)(matchDistance - 1);
        writer.WriteGamma2((offsetMinusOne >> 8) + 2);
        writer.WriteRawByte((byte)offsetMinusOne);

        InsertCoveredPositions(source, position, matchLength, hashHead, chain);
        position += matchLength;
        continue;
      }

      writer.WriteBit(0);
      writer.WriteRawByte(source[position]);
      if (position + MinimumMatchLength <= source.Length)
        InsertHash(source, position, hashHead, chain);
      ++position;
    }

    return writer.Finish();
  }

  private static (int Length, int Distance) FindMatch(
      byte[] source,
      int position,
      int[] hashHead,
      int[] chain,
      int searchDepth) {
    if (position + MinimumMatchLength > source.Length)
      return (0, 0);

    var candidate = hashHead[Hash4(source, position)];
    var maximumLength = source.Length - position;
    var bestLength = 0;
    var bestDistance = 0;

    while (candidate >= 0 && searchDepth-- > 0) {
      var length = 0;
      while (length < maximumLength && source[candidate + length] == source[position + length])
        ++length;

      var distance = position - candidate;
      if (length > bestLength || length == bestLength && length >= MinimumMatchLength && distance < bestDistance) {
        bestLength = length;
        bestDistance = distance;
        if (bestLength == maximumLength)
          break;
      }

      var previous = chain[candidate];
      if (previous >= candidate)
        break;
      candidate = previous;
    }

    return bestLength >= MinimumMatchLength ? (bestLength, bestDistance) : (0, 0);
  }

  private static bool ShouldEmitMatch(int length, int distance) {
    if (length > MinimumMatchLength)
      return true;
    if (length != MinimumMatchLength || distance <= 0)
      return false;

    // Same level-1 heuristic as the reference implementation: a far four-byte
    // match can cost more than literals and may obscure a better next match.
    return distance - 1 < 0x7E00;
  }

  private static void InsertCoveredPositions(
      byte[] source,
      int position,
      int length,
      int[] hashHead,
      int[] chain) {
    var endExclusive = Math.Min(position + length, source.Length - MinimumMatchLength + 1);
    for (var i = position; i < endExclusive; ++i)
      InsertHash(source, i, hashHead, chain);
  }

  private static void InsertHash(byte[] source, int position, int[] hashHead, int[] chain) {
    var hash = Hash4(source, position);
    chain[position] = hashHead[hash];
    hashHead[hash] = position;
  }

  private static int Hash4(byte[] source, int position) {
    var value = (uint)(
      source[position]
      | source[position + 1] << 8
      | source[position + 2] << 16
      | source[position + 3] << 24);
    return (int)(value * 2654435761u >> (32 - HashBits));
  }

  private static byte[] DecompressBlock(byte[] compressed, int uncompressedSize) {
    if (uncompressedSize == 0)
      return [];
    if (compressed.Length == 0)
      throw new InvalidDataException("BriefLZ block has output bytes but no compressed payload.");

    var reader = new PayloadReader(compressed);
    var destination = new byte[uncompressedSize];
    var position = 0;

    while (position < destination.Length) {
      if (reader.ReadBit() == 0) {
        destination[position++] = reader.ReadRawByte();
        continue;
      }

      var lengthCode = reader.ReadGamma2();
      var offsetCode = reader.ReadGamma2();
      if (lengthCode > int.MaxValue - 2u)
        throw new InvalidDataException("BriefLZ match length exceeds the supported range.");

      var length = (int)lengthCode + 2;
      var offsetHigh = offsetCode - 2u;
      var offsetLow = reader.ReadRawByte();
      var distance = ((ulong)offsetHigh << 8) + offsetLow + 1u;

      if (distance == 0 || distance > (ulong)position)
        throw new InvalidDataException($"BriefLZ match offset {distance} exceeds current position {position}.");
      if (length > destination.Length - position)
        throw new InvalidDataException("BriefLZ match exceeds the declared decompressed block size.");

      var sourcePosition = position - (int)distance;
      for (var i = 0; i < length; ++i)
        destination[position++] = destination[sourcePosition + i];
    }

    return destination;
  }

  private static void ValidateLevel(int level) {
    if (level is < MinimumCompressionLevel or > MaximumCompressionLevel)
      throw new ArgumentOutOfRangeException(
        nameof(level), level, $"BriefLZ level must be between {MinimumCompressionLevel} and {MaximumCompressionLevel}.");
  }

  private sealed class PayloadWriter(int capacity) {
    private readonly List<byte> _buffer = new(capacity);
    private int _tagPosition = -1;
    private uint _tag;
    private int _bitsLeft;

    public void WriteRawByte(byte value) => this._buffer.Add(value);

    public void StartTags() => this.ReserveTag();

    public void WriteBit(int bit) {
      if (this._bitsLeft == 0) {
        this.FlushTag();
        this.ReserveTag();
      }

      this._tag = (this._tag << 1) | (uint)(bit & 1);
      --this._bitsLeft;
    }

    public void WriteGamma2(uint value) {
      if (value < 2)
        throw new ArgumentOutOfRangeException(nameof(value), value, "BriefLZ gamma2 values start at 2.");

      var highestBit = 31 - System.Numerics.BitOperations.LeadingZeroCount(value);
      for (var bit = highestBit - 1; bit >= 0; --bit) {
        this.WriteBit((int)(value >> bit) & 1);
        this.WriteBit(bit == 0 ? 0 : 1);
      }
    }

    public byte[] Finish() {
      this.WriteBit(1); // delimiter for any remaining literal tags
      this._tag <<= this._bitsLeft;
      this.FlushTag();
      return [.. this._buffer];
    }

    public byte[] FinishWithoutTag() => [.. this._buffer];

    private void ReserveTag() {
      this._tagPosition = this._buffer.Count;
      this._buffer.Add(0);
      this._buffer.Add(0);
      this._tag = 0;
      this._bitsLeft = 16;
    }

    private void FlushTag() {
      if (this._tagPosition < 0)
        return;
      this._buffer[this._tagPosition] = (byte)this._tag;
      this._buffer[this._tagPosition + 1] = (byte)(this._tag >> 8);
    }
  }

  private sealed class PayloadReader(byte[] source) {
    private int _position;
    private uint _tag = 0x4000;
    private int _bitsLeft = 1;

    public int ReadBit() {
      if (this._bitsLeft == 0) {
        if (this._position + 2 > source.Length)
          throw new InvalidDataException("Unexpected end of BriefLZ tag stream.");

        this._tag = (uint)(source[this._position] | source[this._position + 1] << 8);
        this._position += 2;
        this._bitsLeft = 16;
      }

      var bit = (this._tag & 0x8000) != 0 ? 1 : 0;
      this._tag = this._tag << 1 & 0xFFFF;
      --this._bitsLeft;
      return bit;
    }

    public byte ReadRawByte() {
      if (this._position >= source.Length)
        throw new InvalidDataException("Unexpected end of BriefLZ payload.");
      return source[this._position++];
    }

    public uint ReadGamma2() {
      uint result = 1;
      do {
        if (result > uint.MaxValue >> 1)
          throw new InvalidDataException("BriefLZ gamma2 value overflows UInt32.");
        result = result << 1 | (uint)this.ReadBit();
      } while (this.ReadBit() != 0);

      return result;
    }
  }
}
