// Converted from libbsc's lzp.cpp (Apache-2.0).
// Copyright (c) 2009-2025 Ilya Grebnov <ilya.grebnov@gmail.com>
// See THIRD-PARTY-NOTICE.libbsc.txt in this directory.
using System.Buffers.Binary;

namespace FileFormat.Bsc;

/// <summary>
/// libbsc Lempel-Ziv Prediction preprocessing and its serial sub-block framing.
/// </summary>
internal static class BscLzp {
  private const byte MatchFlag = 0xf2;
  internal const int DefaultHashSize = 15;
  internal const int DefaultMinimumLength = 72;

  /// <summary>
  /// <c>bsc_lzp_compress</c> as a single-threaded libbsc build runs it (<c>bsc_lzp_compress_serial</c>):
  /// inputs of 256 KiB and more are cut into <c>bsc_lzp_num_blocks</c> sub-blocks, each with its own
  /// hash table; a sub-block LZP cannot shrink is stored. Returns false where libbsc returns
  /// <c>LIBBSC_NOT_COMPRESSIBLE</c>.
  /// </summary>
  /// <remarks>
  /// For libbsc's default parameters (hash 15, minimum length 72) the block encoder is
  /// <c>bsc_lzp_encode_large_fast_path</c>, the one x86-64 and AArch64 builds run; other
  /// parameters use <c>bsc_lzp_encode_generic</c>. x86-64 builds run further specialised encoders
  /// for other parameters with a hash of 17 bits or less, whose matches may differ in length;
  /// every variant is read by the same decoder.
  /// </remarks>
  public static bool TryCompress(ReadOnlySpan<byte> input, int hashSize, int minLength, out byte[] framed) {
    ValidateParameters(hashSize, minLength);
    framed = [];
    var n = input.Length;
    var source = input.ToArray();
    var output = new byte[n];
    var blocks = NumBlocks(n);
    int length;

    if (blocks == 1) {
      var result = EncodeBlock(source, 0, n, output, 1, n - 1, hashSize, minLength);
      if (result < 0)
        return false;
      output[0] = 1;
      length = result + 1;
    } else {
      var chunkSize = n / blocks;
      var outputPosition = 1 + 8 * blocks;
      output[0] = (byte)blocks;
      for (var block = 0; block < blocks; ++block) {
        var inputStart = block * chunkSize;
        var inputSize = block != blocks - 1 ? chunkSize : n - inputStart;
        var outputSize = Math.Min(inputSize, n - outputPosition);

        var result = EncodeBlock(source, inputStart, inputStart + inputSize, output, outputPosition, outputPosition + outputSize, hashSize, minLength);
        if (result < 0) {
          if (outputPosition + inputSize >= n)
            return false;
          result = inputSize;
          source.AsSpan(inputStart, inputSize).CopyTo(output.AsSpan(outputPosition));
        }

        BinaryPrimitives.WriteInt32LittleEndian(output.AsSpan(1 + 8 * block), inputSize);
        BinaryPrimitives.WriteInt32LittleEndian(output.AsSpan(1 + 8 * block + 4), result);
        outputPosition += result;
      }

      length = outputPosition;
    }

    framed = output.AsSpan(0, length).ToArray();
    return true;
  }

  // bsc_lzp_num_blocks: the sub-block count grows with the square of the size.
  private static int NumBlocks(int n) {
    ReadOnlySpan<int> counts = [128, 96, 64, 48, 32, 24, 16, 12, 8, 6, 4, 2];
    foreach (var count in counts)
      if (n >= (long)count * count * 65536)
        return count;
    return 1;
  }

  // bsc_lzp_encode_block: the encoded length, or -1 for LIBBSC_NOT_COMPRESSIBLE.
  private static int EncodeBlock(byte[] input, int inputStart, int inputEnd, byte[] output, int outputStart, int outputEnd, int hashSize, int minLength) {
    if (inputEnd - inputStart - minLength < 32)
      return -1;

    var lookup = new Lookup(hashSize);
    return hashSize == DefaultHashSize && minLength == DefaultMinimumLength
      ? EncodeFastPath(input, inputStart, inputEnd, output, outputStart, outputEnd, lookup)
      : EncodeGeneric(input, inputStart, inputEnd, output, outputStart, outputEnd, lookup, minLength);
  }

  private static ulong Load64(byte[] data, int position) => BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(position));

  private static uint Load32(byte[] data, int position) => BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(position));

  private static uint Context(byte[] data, int position)
    => (uint)(data[position - 1] | data[position - 2] << 8 | data[position - 3] << 16 | data[position - 4] << 24);

  private static int HashIndex(uint context, uint mask) => (int)(((context >> 15) ^ context ^ (context >> 3)) & mask);

  // The literal tail after the last position a match may start: every byte is copied, and a
  // match flag the decoder would read as a match is escaped.
  private static int EncodeTail(byte[] input, int inputStart, int position, int inputEnd, byte[] output, int written, int outputEob, Lookup lookup) {
    var context = Context(input, position);
    while (position < inputEnd && written < outputEob) {
      var value = lookup.GetAndSet(HashIndex(context, lookup.Mask), position - inputStart);
      var next = output[written++] = input[position++];
      context = context << 8 | next;
      if (next == MatchFlag && value > 0)
        output[written++] = 255;
    }

    return written;
  }

  private static void WriteMatchLength(byte[] output, ref int written, int outputEob, long excess) {
    output[written++] = MatchFlag;
    while (excess >= 254) {
      excess -= 254;
      output[written++] = 254;
      if (written >= outputEob)
        break;
    }

    output[written++] = (byte)excess;
  }

  // bsc_lzp_encode_large_fast_path (hash 15, minimum length 72): four positions per step,
  // 8-byte candidate checks and byte-exact match lengths.
  private static int EncodeFastPath(byte[] input, int inputStart, int inputEnd, byte[] output, int outputStart, int outputEnd, Lookup lookup) {
    const int minLength = DefaultMinimumLength;
    var mask = lookup.Mask;
    var outputEob = outputEnd - 8;
    var inputMinLengthEnd = inputEnd - minLength - 32;

    var heuristic = inputStart;
    var heuristicValue = inputStart < inputMinLengthEnd ? Load64(input, heuristic) : 0;

    var position = inputStart;
    var written = outputStart;
    for (var i = 0; i < 4; ++i)
      output[written++] = input[position++];

    while (position < inputMinLengthEnd && written < outputEob) {
      // The next four input bytes are copied ahead; a match overwrites from where it starts.
      input.AsSpan(position, 4).CopyTo(output.AsSpan(written));
      var next8 = System.Buffers.Binary.BinaryPrimitives.ReverseEndianness(Load64(input, position - 4));
      var mix = (((next8 >> 12) ^ next8) >> 3) ^ next8;

      var found = -1;
      var bad = -1;
      var value = 0;
      for (var k = 0; k < 4; ++k) {
        var index = (int)((mix >> (8 * (4 - k))) & mask);
        value = lookup.GetAndSet(index, position - inputStart + k);
        if (value > 0
            && Load64(input, position + minLength - 8 + k) == Load64(input, inputStart + value + minLength - 8)
            && Load64(input, position + k) == Load64(input, inputStart + value)) {
          found = k;
          break;
        }

        if (value > 0 && (byte)(next8 >> (8 * (3 - k))) == MatchFlag) {
          bad = k;
          break;
        }
      }

      if (found < 0 && bad < 0) {
        position += 4;
        written += 4;
        continue;
      }

      if (bad >= 0) {
        // A literal match flag the decoder would take for a match: copy it and escape it.
        position += bad + 1;
        written += bad + 1;
        output[written++] = 255;
        continue;
      }

      position += found;
      written += found;
      var reference = inputStart + value;
      if (heuristic - position > 0 && heuristicValue != Load64(input, reference + (heuristic - position))) {
        position += 1;
        if (output[written++] == MatchFlag)
          output[written++] = 255;
        continue;
      }

      long length = 8;
      for (; position + length < inputMinLengthEnd; length += 8) {
        var difference = Load64(input, position + (int)length) ^ Load64(input, reference + (int)length);
        if (difference != 0) {
          length += System.Numerics.BitOperations.TrailingZeroCount(difference) / 8;
          break;
        }
      }

      if (length < minLength) {
        heuristic = position + (int)length;
        heuristicValue = Load64(input, heuristic);
        position += 1;
        if (output[written++] == MatchFlag)
          output[written++] = 255;
        continue;
      }

      position += (int)length;
      WriteMatchLength(output, ref written, outputEob, length - minLength);
    }

    written = EncodeTail(input, inputStart, position, inputEnd, output, written, outputEob, lookup);
    return written >= outputEob ? -1 : written - outputStart;
  }

  // bsc_lzp_encode_generic: one position per step, 4-byte candidate checks and match lengths
  // counted in 4-byte steps, then 2 and 1.
  private static int EncodeGeneric(byte[] input, int inputStart, int inputEnd, byte[] output, int outputStart, int outputEnd, Lookup lookup, int minLength) {
    var mask = lookup.Mask;
    var outputEob = outputEnd - 8;
    var heuristic = inputStart;
    var inputMinLengthEnd = inputEnd - minLength - 32;

    var position = inputStart;
    var written = outputStart;
    for (var i = 0; i < 4; ++i)
      output[written++] = input[position++];

    var context = Context(input, position);
    while (position < inputMinLengthEnd && written < outputEob) {
      var value = lookup.GetAndSet(HashIndex(context, mask), position - inputStart);
      if (value > 0) {
        var reference = inputStart + value;
        var matched = false;
        if (Load32(input, position + minLength - 4) == Load32(input, reference + minLength - 4)
            && Load32(input, position) == Load32(input, reference)
            && !(heuristic > position && Load32(input, heuristic) != Load32(input, reference + (heuristic - position)))) {
          var length = 4;
          for (; position + length < inputMinLengthEnd; length += 4)
            if (Load32(input, position + length) != Load32(input, reference + length))
              break;

          if (length < minLength)
            heuristic = position + length;
          else {
            if (input[position + length] == input[reference + length] && input[position + length + 1] == input[reference + length + 1])
              length += 2;
            if (input[position + length] == input[reference + length])
              length += 1;

            position += length;
            context = Context(input, position);
            WriteMatchLength(output, ref written, outputEob, length - minLength);
            matched = true;
          }
        }

        if (!matched) {
          var next = output[written++] = input[position++];
          context = context << 8 | next;
          if (next == MatchFlag)
            output[written++] = 255;
        }
      } else
        context = context << 8 | (output[written++] = input[position++]);
    }

    written = EncodeTail(input, inputStart, position, inputEnd, output, written, outputEob, lookup);
    return written >= outputEob ? -1 : written - outputStart;
  }

  public static byte[] Decompress(ReadOnlySpan<byte> input, int hashSize, int minLength, int expectedSize) {
    ValidateParameters(hashSize, minLength);
    if (expectedSize < 0)
      throw new InvalidDataException("BSC: invalid LZP output size");
    if (input.IsEmpty)
      throw new InvalidDataException("BSC: missing LZP framing");

    var blocks = input[0];
    if (blocks == 0)
      throw new InvalidDataException("BSC: invalid zero LZP sub-block count");

    if (blocks == 1)
      return DecodeBlock(input[1..], hashSize, minLength, expectedSize);

    var tableBytes = checked(1 + 8 * blocks);
    if (input.Length < tableBytes)
      throw new InvalidDataException("BSC: truncated LZP sub-block table");

    var inputOffset = tableBytes;
    var result = new byte[expectedSize];
    var outputOffset = 0;

    for (var block = 0; block < blocks; ++block) {
      var header = input.Slice(1 + block * 8, 8);
      var outputSize = BinaryPrimitives.ReadInt32LittleEndian(header);
      var inputSize = BinaryPrimitives.ReadInt32LittleEndian(header[4..]);
      if (outputSize < 0 || inputSize < 0 || inputSize > input.Length - inputOffset)
        throw new InvalidDataException("BSC: invalid LZP sub-block sizes");
      if (outputSize > result.Length - outputOffset)
        throw new InvalidDataException("BSC: LZP sub-block expands beyond declared data size");

      var blockInput = input.Slice(inputOffset, inputSize);
      if (inputSize == outputSize) {
        blockInput.CopyTo(result.AsSpan(outputOffset, outputSize));
      } else {
        var decoded = DecodeBlock(blockInput, hashSize, minLength, outputSize);
        decoded.CopyTo(result, outputOffset);
      }

      inputOffset += inputSize;
      outputOffset += outputSize;
    }

    if (inputOffset != input.Length || outputOffset != expectedSize)
      throw new InvalidDataException("BSC: inconsistent LZP framing");
    return result;
  }

  private static byte[] DecodeBlock(ReadOnlySpan<byte> input, int hashSize, int minLength, int expectedSize) {
    if (input.Length < 4 || expectedSize < 4)
      throw new InvalidDataException("BSC: truncated LZP block");

    var lookup = new Lookup(hashSize);
    var result = new List<byte>(expectedSize);
    result.AddRange(input[..4]);
    var inputOffset = 4;

    while (inputOffset < input.Length) {
      if (result.Count >= expectedSize)
        throw new InvalidDataException("BSC: LZP block expands beyond its declared size");

      var index = Hash(result, lookup.Mask);
      var reference = lookup.GetAndSet(index, result.Count);
      var next = input[inputOffset++];

      if (next != MatchFlag || reference <= 0) {
        result.Add(next);
        continue;
      }

      if (inputOffset >= input.Length)
        throw new InvalidDataException("BSC: truncated LZP match");

      var lengthByte = input[inputOffset++];
      if (lengthByte == 255) {
        result.Add(MatchFlag);
        continue;
      }

      long matchLength = minLength;
      while (lengthByte == 254) {
        matchLength += 254;
        if (matchLength > expectedSize - result.Count)
          throw new InvalidDataException("BSC: LZP match exceeds the declared data size");
        if (inputOffset >= input.Length)
          throw new InvalidDataException("BSC: truncated LZP match length");
        lengthByte = input[inputOffset++];
      }
      matchLength += lengthByte;

      if (matchLength > expectedSize - result.Count)
        throw new InvalidDataException("BSC: LZP match exceeds the declared data size");
      if (reference < 0 || reference >= result.Count)
        throw new InvalidDataException("BSC: invalid LZP match reference");

      for (var i = 0; i < matchLength; ++i)
        result.Add(result[reference++]);
    }

    if (result.Count != expectedSize)
      throw new InvalidDataException($"BSC: LZP decoded {result.Count} bytes, expected {expectedSize}");
    return [.. result];
  }

  private static int Hash(ReadOnlySpan<byte> input, int position, uint mask) {
    var context = (uint)(input[position - 1]
      | input[position - 2] << 8
      | input[position - 3] << 16
      | input[position - 4] << 24);
    return (int)(((context >> 15) ^ context ^ (context >> 3)) & mask);
  }

  private static int Hash(List<byte> output, uint mask) {
    var count = output.Count;
    var context = (uint)(output[count - 1]
      | output[count - 2] << 8
      | output[count - 3] << 16
      | output[count - 4] << 24);
    return (int)(((context >> 15) ^ context ^ (context >> 3)) & mask);
  }

  private static void ValidateParameters(int hashSize, int minLength) {
    if (hashSize is < 10 or > 28)
      throw new InvalidDataException($"BSC: invalid LZP hash size {hashSize}");
    if (minLength is < 4 or > 255)
      throw new InvalidDataException($"BSC: invalid LZP minimum match length {minLength}");
  }

  private sealed class Lookup {
    private readonly int[]? _dense;
    private readonly Dictionary<int, int>? _sparse;

    public Lookup(int hashSize) {
      Mask = (1u << hashSize) - 1;
      if (hashSize <= 20)
        _dense = new int[1 << hashSize];
      else
        _sparse = [];
    }

    public uint Mask { get; }

    public int GetAndSet(int index, int value) {
      if (_dense is not null) {
        var previous = _dense[index];
        _dense[index] = value;
        return previous;
      }

      var sparse = _sparse!;
      var previousSparse = sparse.GetValueOrDefault(index);
      sparse[index] = value;
      return previousSparse;
    }
  }
}
