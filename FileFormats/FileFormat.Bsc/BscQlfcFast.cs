// Managed port of libbsc's QLFC fast coder (Apache-2.0).
// Copyright (c) 2009-2025 Ilya Grebnov <ilya.grebnov@gmail.com>
// See THIRD-PARTY-NOTICE.libbsc.txt in this directory.
using System.Buffers.Binary;
using System.Numerics;

namespace FileFormat.Bsc;

/// <summary>
/// libbsc's fast Quantized Local Frequency Coding variant (LIBBSC_CODER_QLFC_FAST).
/// </summary>
internal static class BscQlfcFast {
  private const int AlphabetSize = 256;

  /// <summary>Encodes one QLFC coder sub-block and wraps it in libbsc's serial coder framing.</summary>
  public static byte[] Compress(ReadOnlySpan<byte> input) {
    if (input.IsEmpty)
      throw new ArgumentException("QLFC cannot encode an empty block", nameof(input));

    var block = EncodeBlock(input);
    var result = new byte[block.Length + 1];
    result[0] = 1;
    block.CopyTo(result, 1);
    return result;
  }

  /// <summary>Decodes libbsc's serial QLFC framing, including foreign multi-sub-block streams.</summary>
  public static byte[] Decompress(ReadOnlySpan<byte> input, int maximumOutputSize) {
    if (input.IsEmpty)
      throw new InvalidDataException("BSC: missing QLFC framing");
    if (maximumOutputSize < 0)
      throw new InvalidDataException("BSC: invalid QLFC output limit");

    var blocks = input[0];
    if (blocks == 0)
      throw new InvalidDataException("BSC: invalid zero QLFC sub-block count");

    if (blocks == 1)
      return DecodeBlock(input[1..], maximumOutputSize);

    var tableSize = checked(1 + 8 * blocks);
    if (input.Length < tableSize)
      throw new InvalidDataException("BSC: truncated QLFC sub-block table");

    var inputOffset = tableSize;
    var output = new List<byte>(Math.Min(maximumOutputSize, 1024 * 1024));
    for (var block = 0; block < blocks; ++block) {
      var header = input.Slice(1 + block * 8, 8);
      var outputSize = BinaryPrimitives.ReadInt32LittleEndian(header);
      var inputSize = BinaryPrimitives.ReadInt32LittleEndian(header[4..]);
      if (outputSize < 0 || inputSize < 0 || inputSize > input.Length - inputOffset)
        throw new InvalidDataException("BSC: invalid QLFC sub-block sizes");
      if (outputSize > maximumOutputSize - output.Count)
        throw new InvalidDataException("BSC: QLFC sub-block expands beyond the declared data size");

      var blockInput = input.Slice(inputOffset, inputSize);
      if (inputSize == outputSize) {
        output.AddRange(blockInput);
      } else {
        var decoded = DecodeBlock(blockInput, outputSize);
        if (decoded.Length != outputSize)
          throw new InvalidDataException("BSC: QLFC sub-block size mismatch");
        output.AddRange(decoded);
      }
      inputOffset += inputSize;
    }

    if (inputOffset != input.Length)
      throw new InvalidDataException("BSC: inconsistent QLFC framing");
    return [.. output];
  }

  private static byte[] EncodeBlock(ReadOnlySpan<byte> input) {
    var model = new FastModel();
    var rankArray = Transform(input, out var mtfTable);
    var coder = new BscRangeEncoder(checked(input.Length + 64 * 1024));
    coder.EncodeWord((uint)input.Length);
    EncodeMtfTable(mtfTable, coder);

    var rankOffset = 0;
    var inputOffset = 0;
    while (rankOffset < rankArray.Length) {
      if (coder.CheckEndOfBuffer)
        throw new InvalidDataException("BSC: QLFC output exceeded its safety margin");

      var currentChar = input[inputOffset];
      var runStart = inputOffset++;
      while (inputOffset < input.Length && input[inputOffset] == currentChar)
        ++inputOffset;
      var currentRun = inputOffset - runStart;
      var currentRank = rankArray[rankOffset++];

      EncodeRank(currentChar, currentRank, model, coder);
      EncodeRun(currentChar, currentRun, model, coder);
    }

    if (inputOffset != input.Length)
      throw new InvalidDataException("BSC: internal QLFC run accounting mismatch");
    return coder.Finish();
  }

  private static byte[] DecodeBlock(ReadOnlySpan<byte> input, int maximumOutputSize) {
    var model = new FastModel();
    var coder = new BscRangeDecoder(input);
    var outputSizeUnsigned = coder.DecodeWord();
    if (outputSizeUnsigned > int.MaxValue || outputSizeUnsigned > (uint)maximumOutputSize)
      throw new InvalidDataException("BSC: QLFC block exceeds the declared data size");
    var outputSize = (int)outputSizeUnsigned;
    if (outputSize == 0)
      return [];

    var mtfTable = DecodeMtfTable(ref coder);
    var output = new byte[outputSize];
    var outputOffset = 0;

    while (outputOffset < output.Length) {
      var currentChar = mtfTable[0];
      DecodeRank(currentChar, mtfTable, model, ref coder);
      var run = DecodeRun(currentChar, model, ref coder);
      if (run <= 0 || run > output.Length - outputOffset)
        throw new InvalidDataException("BSC: invalid QLFC run length");
      output.AsSpan(outputOffset, run).Fill(currentChar);
      outputOffset += run;
    }

    return output;
  }

  private static byte[] Transform(ReadOnlySpan<byte> input, out byte[] mtfTable) {
    mtfTable = new byte[AlphabetSize];
    for (var i = 0; i < AlphabetSize; ++i)
      mtfTable[i] = (byte)i;

    if (input[^1] == 0)
      (mtfTable[0], mtfTable[1]) = (mtfTable[1], mtfTable[0]);

    var used = new bool[AlphabetSize];
    var buffer = new byte[input.Length];
    var index = input.Length;
    var symbolCount = 0;

    for (var i = input.Length - 1; i >= 0;) {
      var current = input[i--];
      while (i >= 0 && input[i] == current)
        --i;

      var previous = mtfTable[0];
      var rank = 1;
      mtfTable[0] = current;
      while (true) {
        if (rank >= AlphabetSize)
          throw new InvalidDataException("BSC: QLFC MTF transform lost its current symbol");
        var temporary = mtfTable[rank];
        mtfTable[rank] = previous;
        if (temporary == current)
          break;
        previous = temporary;
        ++rank;
      }

      if (!used[current]) {
        used[current] = true;
        rank = symbolCount++;
      }
      buffer[--index] = (byte)rank;
    }

    // This sentinel is part of libbsc's QLFC transform; for the final run it
    // forces the rank representation expected by all three QLFC models.
    buffer[^1] = 1;

    for (var rank = 1; rank < AlphabetSize; ++rank) {
      if (used[mtfTable[rank]])
        continue;
      mtfTable[rank] = mtfTable[rank - 1];
      break;
    }

    return buffer[index..];
  }

  private static void EncodeMtfTable(ReadOnlySpan<byte> mtfTable, BscRangeEncoder coder) {
    Span<bool> used = stackalloc bool[AlphabetSize];
    var previous = -1;

    for (var rank = 0; rank < AlphabetSize; ++rank) {
      var current = mtfTable[rank];
      for (var bit = 7; bit >= 0; --bit) {
        var canBeZero = false;
        var canBeOne = false;
        for (var candidate = 0; candidate < AlphabetSize; ++candidate) {
          if (candidate != previous && used[candidate])
            continue;
          if ((current >> (bit + 1)) != (candidate >> (bit + 1)))
            continue;
          if ((candidate & (1 << bit)) == 0)
            canBeZero = true;
          else
            canBeOne = true;
          if (canBeZero && canBeOne)
            break;
        }
        if (canBeZero && canBeOne)
          coder.EncodeBit((uint)((current >> bit) & 1), 1, 1);
      }

      if (current == previous)
        break;
      previous = current;
      used[current] = true;
    }
  }

  private static byte[] DecodeMtfTable(ref BscRangeDecoder coder) {
    var table = new byte[AlphabetSize];
    Span<bool> used = stackalloc bool[AlphabetSize];
    var previous = -1;

    for (var rank = 0; rank < AlphabetSize; ++rank) {
      var current = 0;
      for (var bit = 7; bit >= 0; --bit) {
        var canBeZero = false;
        var canBeOne = false;
        for (var candidate = 0; candidate < AlphabetSize; ++candidate) {
          if (candidate != previous && used[candidate])
            continue;
          if (current != (candidate >> (bit + 1)))
            continue;
          if ((candidate & (1 << bit)) == 0)
            canBeZero = true;
          else
            canBeOne = true;
          if (canBeZero && canBeOne)
            break;
        }

        if (canBeZero && canBeOne)
          current = current + current + coder.DecodeBit(1, 1);
        else if (canBeZero)
          current += current;
        else if (canBeOne)
          current += current + 1;
        else
          throw new InvalidDataException("BSC: invalid QLFC MTF table");
      }

      table[rank] = (byte)current;
      if (current == previous)
        break;
      previous = current;
      used[current] = true;
    }

    return table;
  }

  private static void EncodeRank(byte currentChar, int rank, FastModel model, BscRangeEncoder coder) {
    ref var exponent0 = ref model.RankExponent(currentChar, 0);
    if (rank == 1) {
      var probability = exponent0;
      Update(ref exponent0, 8016, 4);
      coder.EncodeBit0(probability, 13);
      return;
    }

    {
      var probability = exponent0;
      Update(ref exponent0, 83, 4);
      coder.EncodeBit1(probability, 13);
    }

    var bitSize = BitScanReverse(rank);
    for (var bit = 1; bit < bitSize; ++bit) {
      ref var predictor = ref model.RankExponent(currentChar, bit);
      var probability = predictor;
      Update(ref predictor, 122, 4);
      coder.EncodeBit1(probability, 13);
    }
    if (bitSize < 7) {
      ref var predictor = ref model.RankExponent(currentChar, bitSize);
      var probability = predictor;
      Update(ref predictor, 8114, 4);
      coder.EncodeBit0(probability, 13);
    }

    var context = 1;
    for (var bit = bitSize - 1; bit >= 0; --bit) {
      ref var predictor = ref model.RankMantissa(currentChar, bitSize, context);
      var probability = predictor;
      var value = (uint)((rank >> bit) & 1);
      Update(ref predictor, value, 7999, 235, 7);
      coder.EncodeBit(value, probability, 13);
      context += context + (int)value;
    }
  }

  private static void EncodeRun(byte currentChar, int run, FastModel model, BscRangeEncoder coder) {
    ref var exponent0 = ref model.RunExponent(currentChar, 0);
    if (run == 1) {
      var probability = exponent0;
      Update(ref exponent0, 2025, 5);
      coder.EncodeBit0(probability, 11);
      return;
    }

    {
      var probability = exponent0;
      Update(ref exponent0, 42, 5);
      coder.EncodeBit1(probability, 11);
    }

    var bitSize = BitScanReverse(run);
    if (bitSize >= 32)
      throw new InvalidDataException("BSC: QLFC run exponent overflow");
    for (var bit = 1; bit < bitSize; ++bit) {
      ref var predictor = ref model.RunExponent(currentChar, bit);
      var probability = predictor;
      Update(ref predictor, 142, 4);
      coder.EncodeBit1(probability, 11);
    }
    {
      ref var predictor = ref model.RunExponent(currentChar, bitSize);
      var probability = predictor;
      Update(ref predictor, 1962, 4);
      coder.EncodeBit0(probability, 11);
    }

    if (bitSize <= 5) {
      var context = 1;
      for (var bit = bitSize - 1; bit >= 0; --bit) {
        ref var predictor = ref model.RunMantissa(currentChar, bitSize, context);
        var probability = predictor;
        var value = (uint)((run >> bit) & 1);
        Update(ref predictor, value, 1951, 147, 6);
        coder.EncodeBit(value, probability, 11);
        context += context + (int)value;
      }
    } else {
      for (int context = 1, bit = bitSize - 1; bit >= 0; --bit, ++context) {
        ref var predictor = ref model.RunMantissa(currentChar, bitSize, context);
        var probability = predictor;
        var value = (uint)((run >> bit) & 1);
        Update(ref predictor, value, 1987, 46, 5);
        coder.EncodeBit(value, probability, 11);
      }
    }
  }

  private static void DecodeRank(byte currentChar, byte[] mtfTable, FastModel model, ref BscRangeDecoder coder) {
    ref var exponent0 = ref model.RankExponent(currentChar, 0);
    var probability = exponent0;
    if (coder.PeakBit(probability, 13) != 0) {
      Update(ref exponent0, 83, 4);
      coder.DecodeBit1(probability, 13);

      var bitSize = 1;
      while (bitSize < 7) {
        ref var predictor = ref model.RankExponent(currentChar, bitSize);
        probability = predictor;
        if (coder.PeakBit(probability, 13) != 0) {
          Update(ref predictor, 122, 4);
          ++bitSize;
          coder.DecodeBit1(probability, 13);
        } else {
          Update(ref predictor, 8114, 4);
          coder.DecodeBit0(probability, 13);
          break;
        }
      }

      var rank = 1;
      for (var bit = bitSize - 1; bit >= 0; --bit) {
        ref var predictor = ref model.RankMantissa(currentChar, bitSize, rank);
        var value = (uint)coder.DecodeBit(predictor, 13);
        Update(ref predictor, value, 7999, 235, 7);
        rank += rank + (int)value;
      }
      if (rank >= AlphabetSize)
        throw new InvalidDataException("BSC: QLFC rank exceeds the alphabet");

      for (var i = 0; i < rank; ++i)
        mtfTable[i] = mtfTable[i + 1];
      mtfTable[rank] = currentChar;
    } else {
      mtfTable[0] = mtfTable[1];
      mtfTable[1] = currentChar;
      Update(ref exponent0, 8016, 4);
      coder.DecodeBit0(probability, 13);
    }
  }

  private static int DecodeRun(byte currentChar, FastModel model, ref BscRangeDecoder coder) {
    ref var exponent0 = ref model.RunExponent(currentChar, 0);
    var probability = exponent0;
    if (coder.PeakBit(probability, 11) == 0) {
      Update(ref exponent0, 2025, 5);
      coder.DecodeBit0(probability, 11);
      return 1;
    }

    Update(ref exponent0, 42, 5);
    coder.DecodeBit1(probability, 11);

    var bitSize = 1;
    while (true) {
      if (bitSize >= 31)
        throw new InvalidDataException("BSC: QLFC run exponent exceeds Int32");
      ref var predictor = ref model.RunExponent(currentChar, bitSize);
      probability = predictor;
      if (coder.PeakBit(probability, 11) != 0) {
        Update(ref predictor, 142, 4);
        ++bitSize;
        coder.DecodeBit1(probability, 11);
      } else {
        Update(ref predictor, 1962, 4);
        coder.DecodeBit0(probability, 11);
        break;
      }
    }

    var run = 1;
    if (bitSize <= 5) {
      for (var bit = bitSize - 1; bit >= 0; --bit) {
        ref var predictor = ref model.RunMantissa(currentChar, bitSize, run);
        var value = (uint)coder.DecodeBit(predictor, 11);
        Update(ref predictor, value, 1951, 147, 6);
        run = checked(run + run + (int)value);
      }
    } else {
      for (var context = 1; context <= bitSize; ++context) {
        ref var predictor = ref model.RunMantissa(currentChar, bitSize, context);
        var value = (uint)coder.DecodeBit(predictor, 11);
        Update(ref predictor, value, 1987, 46, 5);
        run = checked(run + run + (int)value);
      }
    }
    return run;
  }

  private static int BitScanReverse(int value) {
    if (value <= 0)
      throw new InvalidDataException("BSC: QLFC bit scan requires a positive value");
    return 31 - BitOperations.LeadingZeroCount((uint)value);
  }

  private static void Update(ref short probability, int target, int rateShift) {
    var value = probability;
    probability = unchecked((short)(value - ((value - target) >> rateShift)));
  }

  private static void Update(ref short probability, uint bit, int target0, int target1, int rateShift) {
    var value = probability;
    var target = bit == 0 ? target0 : target1;
    probability = unchecked((short)(value - ((value - target) >> rateShift)));
  }

  private sealed class FastModel {
    private readonly short[] _rankExponent = new short[AlphabetSize * 8];
    private readonly short[] _rankMantissa = new short[AlphabetSize * 8 * AlphabetSize];
    private readonly short[] _runExponent = new short[AlphabetSize * 32];
    private readonly short[] _runMantissa = new short[AlphabetSize * 32 * 32];

    public FastModel() {
      Array.Fill(_rankExponent, (short)4096);
      Array.Fill(_rankMantissa, (short)4096);
      Array.Fill(_runExponent, (short)1024);
      Array.Fill(_runMantissa, (short)1024);
    }

    public ref short RankExponent(int character, int bit)
      => ref _rankExponent[character * 8 + bit];

    public ref short RankMantissa(int character, int exponent, int context)
      => ref _rankMantissa[(character * 8 + exponent) * AlphabetSize + context];

    public ref short RunExponent(int character, int bit)
      => ref _runExponent[character * 32 + bit];

    public ref short RunMantissa(int character, int exponent, int context)
      => ref _runMantissa[(character * 32 + exponent) * 32 + context];
  }
}
