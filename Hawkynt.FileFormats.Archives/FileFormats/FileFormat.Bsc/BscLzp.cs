// Managed port of libbsc's LZP stream semantics (Apache-2.0).
// Copyright (c) 2009-2025 Ilya Grebnov <ilya.grebnov@gmail.com>
// See THIRD-PARTY-NOTICE.libbsc.txt in this directory.
using System.Buffers.Binary;

namespace FileFormat.Bsc;

/// <summary>
/// libbsc Lempel-Ziv Prediction preprocessing and its serial sub-block framing.
/// </summary>
internal static class BscLzp {
  private const byte MatchFlag = 0xf2;

  public static bool TryCompress(ReadOnlySpan<byte> input, int hashSize, int minLength, out byte[] framed) {
    ValidateParameters(hashSize, minLength);
    framed = [];
    if (input.Length < minLength + 32 || input.Length < 4)
      return false;

    var lookup = new Lookup(hashSize);
    var encoded = new List<byte>(input.Length);
    encoded.AddRange(input[..4]);

    var position = 4;
    while (position < input.Length) {
      var index = Hash(input, position, lookup.Mask);
      var reference = lookup.GetAndSet(index, position);

      if (reference > 0) {
        var matchLength = 0;
        var maximum = input.Length - position;
        while (matchLength < maximum && input[position + matchLength] == input[reference + matchLength])
          ++matchLength;

        if (matchLength >= minLength) {
          encoded.Add(MatchFlag);
          var excess = matchLength - minLength;
          while (excess >= 254) {
            encoded.Add(254);
            excess -= 254;
          }
          encoded.Add((byte)excess);
          position += matchLength;
          continue;
        }
      }

      var literal = input[position++];
      encoded.Add(literal);
      if (literal == MatchFlag && reference > 0)
        encoded.Add(255);

      // libbsc stops LZP when the preprocessing cannot save space. Keeping the
      // same decision here avoids feeding an expanded stream to BWT/QLFC.
      if (encoded.Count + 1 >= input.Length)
        return false;
    }

    if (encoded.Count + 1 >= input.Length)
      return false;

    framed = new byte[encoded.Count + 1];
    framed[0] = 1;
    encoded.CopyTo(framed, 1);
    return true;
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
