// Managed port of libbsc's QLFC static/adaptive coders (Apache-2.0).
// Copyright (c) 2009-2025 Ilya Grebnov <ilya.grebnov@gmail.com>
// See THIRD-PARTY-NOTICE.libbsc.txt in this directory.
using System.Buffers.Binary;
using System.Numerics;

namespace FileFormat.Bsc;

/// <summary>
/// libbsc's model-1 QLFC coders. Static uses the fixed weighted probability
/// blend; Adaptive uses the upstream logistic probability mixers.
/// </summary>
internal static class BscQlfcModel {
  private const int AlphabetSize = 256;
  private const short InitialProbability = 2048;

  private static readonly BitModelConfig RankTop = new(
    StaticWeights: new(17, 14, 1),
    StaticState: new(-116, 33, -78, 34), StaticChar: new(-2, 282, 12, 274), StaticGlobal: new(4, 697, 55, 1185),
    AdaptiveLearning: new(20, 47, 27),
    AdaptiveState: new(1, 57, -111, 31), AdaptiveChar: new(291, 250, 154, 528), AdaptiveGlobal: new(375, 163, 313, 639),
    AdaptiveMixer: new(-41, 96, 53, 49), StaticUsesGenericUpdate: false);

  private static readonly BitModelConfig RankExponent = new(
    StaticWeights: new(22, 6, 4),
    StaticState: new(-177, 23, -370, 11), StaticChar: new(-14, 271, 3, 308), StaticGlobal: new(-3, 788, 135, 1364),
    AdaptiveLearning: new(49, 41, 40),
    AdaptiveState: new(-137, 17, 482, 40), AdaptiveChar: new(61, 192, 200, 133), AdaptiveGlobal: new(54, 1342, 578, 1067),
    AdaptiveMixer: new(-11, 318, 144, 848), StaticUsesGenericUpdate: false);

  private static readonly BitModelConfig RankMantissa = new(
    StaticWeights: new(15, 10, 7),
    StaticState: new(-254, 16, -177, 20), StaticChar: new(-55, 73, -54, 74), StaticGlobal: new(-6, 575, 1670, 1173),
    AdaptiveLearning: new(263, 175, 17),
    AdaptiveState: new(-145, 18, 114, 24), AdaptiveChar: new(-43, 69, -36, 78), AdaptiveGlobal: new(-2, 1119, 11, 1181),
    AdaptiveMixer: new(-203, 20, -271, 15), StaticUsesGenericUpdate: true);

  private static readonly BitModelConfig RankEscape = new(
    StaticWeights: new(16, 11, 5),
    StaticState: new(-126, 32, -126, 32), StaticChar: new(-33, 120, -25, 157), StaticGlobal: new(-6, 585, 150, 275),
    AdaptiveLearning: new(480, 202, 17),
    AdaptiveState: new(-99, 32, 318, 42), AdaptiveChar: new(17, 101, 1116, 246), AdaptiveGlobal: new(22, 964, -2, 1110),
    AdaptiveMixer: new(-194, 21, -129, 20), StaticUsesGenericUpdate: true);

  private static readonly BitModelConfig RunTop = new(
    StaticWeights: new(14, 18, 0),
    StaticState: new(-68, 38, -112, 36), StaticChar: new(-4, 221, -13, 231), StaticGlobal: new(0, 0, 0, 0),
    AdaptiveLearning: new(15, 50, 78),
    AdaptiveState: new(-93, 34, -4, 51), AdaptiveChar: new(139, 423, 244, 162), AdaptiveGlobal: new(275, 450, -6, 579),
    AdaptiveMixer: new(-68, 25, 1, 64), StaticUsesGenericUpdate: false);

  private static readonly BitModelConfig RunExponent = new(
    StaticWeights: new(14, 15, 3),
    StaticState: new(-90, 45, -92, 44), StaticChar: new(-3, 325, -11, 341), StaticGlobal: new(24, 887, -4, 765),
    AdaptiveLearning: new(35, 37, 42),
    AdaptiveState: new(-116, 31, 43, 45), AdaptiveChar: new(165, 222, 30, 324), AdaptiveGlobal: new(315, 857, 109, 867),
    AdaptiveMixer: new(-14, 215, 61, 73), StaticUsesGenericUpdate: false);

  private static readonly BitModelConfig RunMantissa = new(
    StaticWeights: new(7, 15, 10),
    StaticState: new(-275, 14, -185, 22), StaticChar: new(-18, 191, -15, 241), StaticGlobal: new(-73, 54, -214, 19),
    AdaptiveLearning: new(51, 44, 80),
    AdaptiveState: new(-176, 14, -141, 21), AdaptiveChar: new(84, 172, 37, 263), AdaptiveGlobal: new(2, 15, -197, 20),
    AdaptiveMixer: new(-27, 142, -146, 27), StaticUsesGenericUpdate: true);

  internal static byte[] Compress(ReadOnlySpan<byte> input, bool adaptive) {
    if (input.IsEmpty)
      throw new ArgumentException("QLFC cannot encode an empty block", nameof(input));

    var block = EncodeBlock(input, adaptive);
    var result = new byte[block.Length + 1];
    result[0] = 1;
    block.CopyTo(result, 1);
    return result;
  }

  internal static byte[] Decompress(ReadOnlySpan<byte> input, int maximumOutputSize, bool adaptive) {
    if (input.IsEmpty)
      throw new InvalidDataException("BSC: missing QLFC framing");
    if (maximumOutputSize < 0)
      throw new InvalidDataException("BSC: invalid QLFC output limit");

    var blocks = input[0];
    if (blocks == 0)
      throw new InvalidDataException("BSC: invalid zero QLFC sub-block count");
    if (blocks == 1)
      return DecodeBlock(input[1..], maximumOutputSize, adaptive);

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
      if (inputSize == outputSize)
        output.AddRange(blockInput);
      else {
        var decoded = DecodeBlock(blockInput, outputSize, adaptive);
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

  private static byte[] EncodeBlock(ReadOnlySpan<byte> input, bool adaptive) {
    var model = new Model(adaptive);
    var rankArray = Transform(input, out var mtfTable);
    var coder = new BscRangeEncoder(checked(input.Length + 64 * 1024));
    coder.EncodeWord((uint)input.Length);
    var maxRank = EncodeMtfTable(mtfTable, coder);

    var rankHistory = new byte[AlphabetSize];
    var runHistory = new byte[AlphabetSize];
    var contextRank0 = 0;
    var contextRank4 = 0;
    var contextRun = 0;
    var averageRank = 0;
    var rankOffset = 0;
    var inputOffset = 0;

    while (rankOffset < rankArray.Length) {
      if (coder.CheckEndOfBuffer)
        throw new InvalidDataException("BSC: QLFC output exceeded its safety margin");

      var currentChar = input[inputOffset];
      var runStart = inputOffset++;
      while (inputOffset < input.Length && input[inputOffset] == currentChar)
        ++inputOffset;
      var runSize = inputOffset - runStart;
      var rank = rankArray[rankOffset++];
      var history = rankHistory[currentChar];
      var state = BscQlfcTables.RankStateFor(contextRank4, contextRun, history);

      if (averageRank < 32) {
        ref var stateProbability = ref model.RankState[state];
        ref var charProbability = ref model.RankChar[currentChar];
        if (rank == 1) {
          rankHistory[currentChar] = 0;
          EncodeModelBit(coder, 0, ref charProbability, ref stateProbability, ref model.RankStatic,
            RankTop, model, model.RankMixerId(currentChar));
        } else {
          EncodeModelBit(coder, 1, ref charProbability, ref stateProbability, ref model.RankStatic,
            RankTop, model, model.RankMixerId(currentChar));

          var bitRankSize = BitOperations.Log2((uint)rank);
          rankHistory[currentChar] = (byte)bitRankSize;
          for (var bit = 1; bit < bitRankSize; ++bit) {
            ref var stateExponent = ref model.RankExponentState(state, bit - 1);
            ref var charExponent = ref model.RankExponentChar(currentChar, bit - 1);
            ref var staticExponent = ref model.RankExponentStatic[bit - 1];
            EncodeModelBit(coder, 1, ref charExponent, ref stateExponent, ref staticExponent,
              RankExponent, model, model.RankExponentMixerId(Math.Max(history, (byte)bit), bit));
          }
          if (bitRankSize < maxRank) {
            ref var stateExponent = ref model.RankExponentState(state, bitRankSize - 1);
            ref var charExponent = ref model.RankExponentChar(currentChar, bitRankSize - 1);
            ref var staticExponent = ref model.RankExponentStatic[bitRankSize - 1];
            EncodeModelBit(coder, 0, ref charExponent, ref stateExponent, ref staticExponent,
              RankExponent, model, model.RankExponentMixerId(Math.Max(history, (byte)bitRankSize), bitRankSize));
          }

          var context = 1;
          for (var bit = bitRankSize - 1; bit >= 0; --bit) {
            var value = (rank >> bit) & 1;
            ref var stateMantissa = ref model.RankMantissaState(bitRankSize, state, context);
            ref var charMantissa = ref model.RankMantissaChar(bitRankSize, currentChar, context);
            ref var staticMantissa = ref model.RankMantissaStatic(bitRankSize, context);
            EncodeModelBit(coder, value, ref charMantissa, ref stateMantissa, ref staticMantissa,
              RankMantissa, model, model.RankMantissaMixerId(bitRankSize));
            context += context + value;
          }
        }
      } else {
        rankHistory[currentChar] = (byte)BitOperations.Log2((uint)rank);
        var context = 1;
        for (var bit = maxRank; bit >= 0; --bit) {
          var value = (rank >> bit) & 1;
          ref var stateEscape = ref model.RankEscapeState(state, context);
          ref var charEscape = ref model.RankEscapeChar(currentChar, context);
          ref var staticEscape = ref model.RankEscapeStatic[context];
          EncodeModelBit(coder, value, ref charEscape, ref stateEscape, ref staticEscape,
            RankEscape, model, model.RankEscapeMixerId(context));
          context += context + value;
        }
      }

      averageRank = (averageRank * 124 + rank * 4) >> 7;
      --rank;
      history = runHistory[currentChar];
      state = BscQlfcTables.RunStateFor(contextRank0, contextRun, rank, history);
      ref var runStateProbability = ref model.RunState[state];
      ref var runCharProbability = ref model.RunChar[currentChar];

      if (runSize == 1) {
        runHistory[currentChar] = (byte)((runHistory[currentChar] + 2) >> 2);
        EncodeModelBit(coder, 0, ref runCharProbability, ref runStateProbability, ref model.RunStatic,
          RunTop, model, model.RunMixerId(currentChar));
      } else {
        EncodeModelBit(coder, 1, ref runCharProbability, ref runStateProbability, ref model.RunStatic,
          RunTop, model, model.RunMixerId(currentChar));

        var bitRunSize = BitOperations.Log2((uint)runSize);
        runHistory[currentChar] = (byte)((runHistory[currentChar] + 3 * bitRunSize + 3) >> 2);
        for (var bit = 1; bit < bitRunSize; ++bit) {
          ref var stateExponent = ref model.RunExponentState(state, bit - 1);
          ref var charExponent = ref model.RunExponentChar(currentChar, bit - 1);
          ref var staticExponent = ref model.RunExponentStatic[bit - 1];
          EncodeModelBit(coder, 1, ref charExponent, ref stateExponent, ref staticExponent,
            RunExponent, model, model.RunExponentMixerId(Math.Max(history, (byte)bit), bit));
        }
        {
          ref var stateExponent = ref model.RunExponentState(state, bitRunSize - 1);
          ref var charExponent = ref model.RunExponentChar(currentChar, bitRunSize - 1);
          ref var staticExponent = ref model.RunExponentStatic[bitRunSize - 1];
          EncodeModelBit(coder, 0, ref charExponent, ref stateExponent, ref staticExponent,
            RunExponent, model, model.RunExponentMixerId(Math.Max(history, (byte)bitRunSize), bitRunSize));
        }

        var context = 1;
        for (var bit = bitRunSize - 1; bit >= 0; --bit) {
          var value = (runSize >> bit) & 1;
          ref var stateMantissa = ref model.RunMantissaState(bitRunSize, state, context);
          ref var charMantissa = ref model.RunMantissaChar(bitRunSize, currentChar, context);
          ref var staticMantissa = ref model.RunMantissaStatic(bitRunSize, context);
          EncodeModelBit(coder, value, ref charMantissa, ref stateMantissa, ref staticMantissa,
            RunMantissa, model, model.RunMantissaMixerId(bitRunSize));
          context = bitRunSize <= 5 ? context + context + value : context + 1;
        }
      }

      contextRank0 = ((contextRank0 << 1) | (rank == 0 ? 1 : 0)) & 0x7;
      contextRank4 = ((contextRank4 << 2) | (rank < 3 ? rank : 3)) & 0xff;
      contextRun = ((contextRun << 1) | (runSize < 3 ? 1 : 0)) & 0xf;
    }

    if (inputOffset != input.Length)
      throw new InvalidDataException("BSC: internal QLFC run accounting mismatch");
    return coder.Finish();
  }

  private static byte[] DecodeBlock(ReadOnlySpan<byte> input, int maximumOutputSize, bool adaptive) {
    var model = new Model(adaptive);
    var coder = new BscRangeDecoder(input);
    var outputSizeUnsigned = coder.DecodeWord();
    if (outputSizeUnsigned > int.MaxValue || outputSizeUnsigned > (uint)maximumOutputSize)
      throw new InvalidDataException("BSC: QLFC block exceeds the declared data size");
    var outputSize = (int)outputSizeUnsigned;
    if (outputSize == 0)
      return [];

    var mtfTable = DecodeMtfTable(ref coder, out var maxRank);
    var rankHistory = new byte[AlphabetSize];
    var runHistory = new byte[AlphabetSize];
    var output = new byte[outputSize];
    var outputOffset = 0;
    var contextRank0 = 0;
    var contextRank4 = 0;
    var contextRun = 0;
    var averageRank = 0;

    while (outputOffset < output.Length) {
      var currentChar = mtfTable[0];
      var history = rankHistory[currentChar];
      var state = BscQlfcTables.RankStateFor(contextRank4, contextRun, history);
      var rank = 1;

      if (averageRank < 32) {
        ref var stateProbability = ref model.RankState[state];
        ref var charProbability = ref model.RankChar[currentChar];
        if (DecodeModelBit(ref coder, ref charProbability, ref stateProbability, ref model.RankStatic,
              RankTop, model, model.RankMixerId(currentChar)) != 0) {
          var bitRankSize = 1;
          while (bitRankSize < maxRank) {
            ref var stateExponent = ref model.RankExponentState(state, bitRankSize - 1);
            ref var charExponent = ref model.RankExponentChar(currentChar, bitRankSize - 1);
            ref var staticExponent = ref model.RankExponentStatic[bitRankSize - 1];
            var mixerHistory = Math.Max(history, (byte)bitRankSize);
            if (DecodeModelBit(ref coder, ref charExponent, ref stateExponent, ref staticExponent,
                  RankExponent, model, model.RankExponentMixerId(mixerHistory, bitRankSize)) == 0)
              break;
            ++bitRankSize;
          }
          if (bitRankSize == maxRank && maxRank > 0) {
            // When the exponent reaches maxRank there is no terminating zero bit.
          }

          rankHistory[currentChar] = (byte)bitRankSize;
          rank = 1;
          for (var bit = bitRankSize - 1; bit >= 0; --bit) {
            ref var stateMantissa = ref model.RankMantissaState(bitRankSize, state, rank);
            ref var charMantissa = ref model.RankMantissaChar(bitRankSize, currentChar, rank);
            ref var staticMantissa = ref model.RankMantissaStatic(bitRankSize, rank);
            var value = DecodeModelBit(ref coder, ref charMantissa, ref stateMantissa, ref staticMantissa,
              RankMantissa, model, model.RankMantissaMixerId(bitRankSize));
            rank += rank + value;
          }
        } else {
          rankHistory[currentChar] = 0;
        }
      } else {
        rank = 0;
        var context = 1;
        for (var bit = maxRank; bit >= 0; --bit) {
          ref var stateEscape = ref model.RankEscapeState(state, context);
          ref var charEscape = ref model.RankEscapeChar(currentChar, context);
          ref var staticEscape = ref model.RankEscapeStatic[context];
          var value = DecodeModelBit(ref coder, ref charEscape, ref stateEscape, ref staticEscape,
            RankEscape, model, model.RankEscapeMixerId(context));
          rank += rank + value;
          context += context + value;
        }
        if (rank <= 0)
          throw new InvalidDataException("BSC: invalid zero QLFC rank");
        rankHistory[currentChar] = (byte)BitOperations.Log2((uint)rank);
      }

      if ((uint)rank >= AlphabetSize)
        throw new InvalidDataException("BSC: QLFC rank exceeds the alphabet");
      for (var r = 0; r < rank; ++r)
        mtfTable[r] = mtfTable[r + 1];
      mtfTable[rank] = currentChar;

      averageRank = (averageRank * 124 + rank * 4) >> 7;
      --rank;
      history = runHistory[currentChar];
      state = BscQlfcTables.RunStateFor(contextRank0, contextRun, rank, history);
      ref var runStateProbability = ref model.RunState[state];
      ref var runCharProbability = ref model.RunChar[currentChar];
      var runSize = 1;

      if (DecodeModelBit(ref coder, ref runCharProbability, ref runStateProbability, ref model.RunStatic,
            RunTop, model, model.RunMixerId(currentChar)) != 0) {
        var bitRunSize = 1;
        while (true) {
          if (bitRunSize >= 32)
            throw new InvalidDataException("BSC: QLFC run exponent overflow");
          ref var stateExponent = ref model.RunExponentState(state, bitRunSize - 1);
          ref var charExponent = ref model.RunExponentChar(currentChar, bitRunSize - 1);
          ref var staticExponent = ref model.RunExponentStatic[bitRunSize - 1];
          var mixerHistory = Math.Max(history, (byte)bitRunSize);
          if (DecodeModelBit(ref coder, ref charExponent, ref stateExponent, ref staticExponent,
                RunExponent, model, model.RunExponentMixerId(mixerHistory, bitRunSize)) == 0)
            break;
          ++bitRunSize;
        }

        runHistory[currentChar] = (byte)((runHistory[currentChar] + 3 * bitRunSize + 3) >> 2);
        runSize = 1;
        var context = 1;
        for (var bit = bitRunSize - 1; bit >= 0; --bit) {
          ref var stateMantissa = ref model.RunMantissaState(bitRunSize, state, context);
          ref var charMantissa = ref model.RunMantissaChar(bitRunSize, currentChar, context);
          ref var staticMantissa = ref model.RunMantissaStatic(bitRunSize, context);
          var value = DecodeModelBit(ref coder, ref charMantissa, ref stateMantissa, ref staticMantissa,
            RunMantissa, model, model.RunMantissaMixerId(bitRunSize));
          runSize = checked(runSize + runSize + value);
          context = bitRunSize <= 5 ? context + context + value : context + 1;
        }
      } else {
        runHistory[currentChar] = (byte)((runHistory[currentChar] + 2) >> 2);
      }

      if (runSize > output.Length - outputOffset)
        throw new InvalidDataException("BSC: QLFC run expands beyond the declared data size");
      contextRank0 = ((contextRank0 << 1) | (rank == 0 ? 1 : 0)) & 0x7;
      contextRank4 = ((contextRank4 << 2) | (rank < 3 ? rank : 3)) & 0xff;
      contextRun = ((contextRun << 1) | (runSize < 3 ? 1 : 0)) & 0xf;
      output.AsSpan(outputOffset, runSize).Fill(currentChar);
      outputOffset += runSize;
    }

    return output;
  }

  private static void EncodeModelBit(
      BscRangeEncoder coder,
      int bit,
      ref short charProbability,
      ref short stateProbability,
      ref short staticProbability,
      BitModelConfig config,
      Model model,
      int mixerId) {
    MixState mixState = default;
    var probability = model.Adaptive
      ? model.Mix(mixerId, charProbability, stateProbability, staticProbability, config, out mixState)
      : StaticProbability(charProbability, stateProbability, staticProbability, config.StaticWeights);

    if (model.Adaptive) {
      UpdateExplicit(ref stateProbability, bit, config.AdaptiveState);
      UpdateExplicit(ref charProbability, bit, config.AdaptiveChar);
      UpdateExplicit(ref staticProbability, bit, config.AdaptiveGlobal);
      model.UpdateMixer(mixerId, mixState, bit, config);
    } else {
      UpdateStatic(ref stateProbability, bit, config.StaticState, config.StaticUsesGenericUpdate);
      UpdateStatic(ref charProbability, bit, config.StaticChar, config.StaticUsesGenericUpdate);
      UpdateStatic(ref staticProbability, bit, config.StaticGlobal, config.StaticUsesGenericUpdate);
    }

    coder.EncodeBit((uint)bit, probability);
  }

  private static int DecodeModelBit(
      ref BscRangeDecoder coder,
      ref short charProbability,
      ref short stateProbability,
      ref short staticProbability,
      BitModelConfig config,
      Model model,
      int mixerId) {
    MixState mixState = default;
    var probability = model.Adaptive
      ? model.Mix(mixerId, charProbability, stateProbability, staticProbability, config, out mixState)
      : StaticProbability(charProbability, stateProbability, staticProbability, config.StaticWeights);
    var bit = coder.DecodeBit(probability);

    if (model.Adaptive) {
      UpdateExplicit(ref stateProbability, bit, config.AdaptiveState);
      UpdateExplicit(ref charProbability, bit, config.AdaptiveChar);
      UpdateExplicit(ref staticProbability, bit, config.AdaptiveGlobal);
      model.UpdateMixer(mixerId, mixState, bit, config);
    } else {
      UpdateStatic(ref stateProbability, bit, config.StaticState, config.StaticUsesGenericUpdate);
      UpdateStatic(ref charProbability, bit, config.StaticChar, config.StaticUsesGenericUpdate);
      UpdateStatic(ref staticProbability, bit, config.StaticGlobal, config.StaticUsesGenericUpdate);
    }
    return bit;
  }

  private static int StaticProbability(int charProbability, int stateProbability, int staticProbability, Weights weights)
    => unchecked((charProbability * weights.Char + stateProbability * weights.State + staticProbability * weights.Global) >> 5);

  private static void UpdateStatic(ref short probability, int bit, Counter counter, bool generic) {
    if (generic) {
      var delta0 = unchecked(probability * counter.Rate0 - ((4096 - counter.Threshold0) * counter.Rate0 - 4095));
      var delta1 = unchecked(probability * counter.Rate1 - counter.Threshold1 * counter.Rate1);
      probability = unchecked((short)(probability - ((bit != 0 ? delta1 : delta0) >> 12)));
    } else
      UpdateExplicit(ref probability, bit, counter);
  }

  private static void UpdateExplicit(ref short probability, int bit, Counter counter) {
    if (bit == 0)
      probability = unchecked((short)(probability + (((4096 - counter.Threshold0 - probability) * counter.Rate0) >> 12)));
    else
      probability = unchecked((short)(probability - (((probability - counter.Threshold1) * counter.Rate1) >> 12)));
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

    buffer[^1] = 1;
    for (var rank = 1; rank < AlphabetSize; ++rank) {
      if (used[mtfTable[rank]])
        continue;
      mtfTable[rank] = mtfTable[rank - 1];
      break;
    }
    return buffer[index..];
  }

  private static int EncodeMtfTable(ReadOnlySpan<byte> mtfTable, BscRangeEncoder coder) {
    Span<bool> used = stackalloc bool[AlphabetSize];
    var previous = -1;
    var maxRank = 7;
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
          coder.EncodeBit((uint)((current >> bit) & 1));
      }

      if (current == previous) {
        maxRank = rank <= 1 ? 0 : BitOperations.Log2((uint)(rank - 1));
        break;
      }
      previous = current;
      used[current] = true;
    }
    return maxRank;
  }

  private static byte[] DecodeMtfTable(ref BscRangeDecoder coder, out int maxRank) {
    var table = new byte[AlphabetSize];
    Span<bool> used = stackalloc bool[AlphabetSize];
    var previous = -1;
    maxRank = 7;
    for (var rank = 0; rank < AlphabetSize; ++rank) {
      var current = 0;
      for (var bit = 7; bit >= 0; --bit) {
        var canBeZero = false;
        var canBeOne = false;
        for (var candidate = 0; candidate < AlphabetSize; ++candidate) {
          if (candidate != previous && used[candidate])
            continue;
          if (current != candidate >> (bit + 1))
            continue;
          if ((candidate & (1 << bit)) == 0)
            canBeZero = true;
          else
            canBeOne = true;
          if (canBeZero && canBeOne)
            break;
        }

        if (canBeZero && canBeOne)
          current = current + current + coder.DecodeBit();
        else if (canBeZero)
          current += current;
        else if (canBeOne)
          current += current + 1;
        else
          throw new InvalidDataException("BSC: invalid QLFC MTF table");
      }

      table[rank] = (byte)current;
      if (current == previous) {
        maxRank = rank <= 1 ? 0 : BitOperations.Log2((uint)(rank - 1));
        break;
      }
      previous = current;
      used[current] = true;
    }
    return table;
  }

  private readonly record struct Counter(int Threshold0, int Rate0, int Threshold1, int Rate1);
  private readonly record struct Weights(int Char, int State, int Global);
  private readonly record struct BitModelConfig(
    Weights StaticWeights,
    Counter StaticState,
    Counter StaticChar,
    Counter StaticGlobal,
    Weights AdaptiveLearning,
    Counter AdaptiveState,
    Counter AdaptiveChar,
    Counter AdaptiveGlobal,
    Counter AdaptiveMixer,
    bool StaticUsesGenericUpdate);

  private readonly record struct MixState(int StretchedChar, int StretchedState, int StretchedGlobal, int MixedProbability, int MapIndex);

  private sealed class Model {
    private const int RankExponentBits = 8;
    private const int RunExponentBits = 32;
    private const int RankMantissaContexts = 256;
    private const int RunMantissaContexts = 32;

    private readonly MixerBank? _rankMixers;
    private readonly MixerBank? _rankExponentMixers;
    private readonly MixerBank? _rankMantissaMixers;
    private readonly MixerBank? _rankEscapeMixers;
    private readonly MixerBank? _runMixers;
    private readonly MixerBank? _runExponentMixers;
    private readonly MixerBank? _runMantissaMixers;

    internal Model(bool adaptive) {
      Adaptive = adaptive;
      if (!adaptive)
        return;
      // The kind is the high byte of every mixer id and has to keep matching
      // ResolveBank, so it belongs to the bank itself, not to a running counter.
      _rankMixers = new(1, AlphabetSize);
      _rankExponentMixers = new(2, RankExponentBits * RankExponentBits);
      _rankMantissaMixers = new(3, RankExponentBits);
      _rankEscapeMixers = new(4, AlphabetSize);
      _runMixers = new(5, AlphabetSize);
      _runExponentMixers = new(6, RunExponentBits * RunExponentBits);
      _runMantissaMixers = new(7, RunExponentBits);
    }

    internal bool Adaptive { get; }
    internal short RankStatic = InitialProbability;
    internal short[] RankState { get; } = Filled(AlphabetSize);
    internal short[] RankChar { get; } = Filled(AlphabetSize);
    internal short[] RankExponentStatic { get; } = Filled(RankExponentBits);
    private short[] RankExponentStates { get; } = Filled(AlphabetSize * RankExponentBits);
    private short[] RankExponentChars { get; } = Filled(AlphabetSize * RankExponentBits);
    private short[] RankMantissaStatics { get; } = Filled(RankExponentBits * RankMantissaContexts);
    private short[] RankMantissaStates { get; } = Filled(RankExponentBits * AlphabetSize * RankMantissaContexts);
    private short[] RankMantissaChars { get; } = Filled(RankExponentBits * AlphabetSize * RankMantissaContexts);
    internal short[] RankEscapeStatic { get; } = Filled(RankMantissaContexts);
    private short[] RankEscapeStates { get; } = Filled(AlphabetSize * RankMantissaContexts);
    private short[] RankEscapeChars { get; } = Filled(AlphabetSize * RankMantissaContexts);

    internal short RunStatic = InitialProbability;
    internal short[] RunState { get; } = Filled(AlphabetSize);
    internal short[] RunChar { get; } = Filled(AlphabetSize);
    internal short[] RunExponentStatic { get; } = Filled(RunExponentBits);
    private short[] RunExponentStates { get; } = Filled(AlphabetSize * RunExponentBits);
    private short[] RunExponentChars { get; } = Filled(AlphabetSize * RunExponentBits);
    private short[] RunMantissaStatics { get; } = Filled(RunExponentBits * RunMantissaContexts);
    private short[] RunMantissaStates { get; } = Filled(RunExponentBits * AlphabetSize * RunMantissaContexts);
    private short[] RunMantissaChars { get; } = Filled(RunExponentBits * AlphabetSize * RunMantissaContexts);

    internal ref short RankExponentState(int state, int bit) => ref RankExponentStates[state * RankExponentBits + bit];
    internal ref short RankExponentChar(int character, int bit) => ref RankExponentChars[character * RankExponentBits + bit];
    internal ref short RankMantissaStatic(int bitSize, int context) => ref RankMantissaStatics[bitSize * RankMantissaContexts + context];
    internal ref short RankMantissaState(int bitSize, int state, int context)
      => ref RankMantissaStates[(bitSize * AlphabetSize + state) * RankMantissaContexts + context];
    internal ref short RankMantissaChar(int bitSize, int character, int context)
      => ref RankMantissaChars[(bitSize * AlphabetSize + character) * RankMantissaContexts + context];
    internal ref short RankEscapeState(int state, int context) => ref RankEscapeStates[state * RankMantissaContexts + context];
    internal ref short RankEscapeChar(int character, int context) => ref RankEscapeChars[character * RankMantissaContexts + context];

    internal ref short RunExponentState(int state, int bit) => ref RunExponentStates[state * RunExponentBits + bit];
    internal ref short RunExponentChar(int character, int bit) => ref RunExponentChars[character * RunExponentBits + bit];
    internal ref short RunMantissaStatic(int bitSize, int context) => ref RunMantissaStatics[bitSize * RunMantissaContexts + context];
    internal ref short RunMantissaState(int bitSize, int state, int context)
      => ref RunMantissaStates[(bitSize * AlphabetSize + state) * RunMantissaContexts + context];
    internal ref short RunMantissaChar(int bitSize, int character, int context)
      => ref RunMantissaChars[(bitSize * AlphabetSize + character) * RunMantissaContexts + context];

    internal int RankMixerId(int character) => MixerId(_rankMixers, character);
    internal int RankExponentMixerId(int history, int bit) => MixerId(_rankExponentMixers, history * RankExponentBits + bit);
    internal int RankMantissaMixerId(int bitSize) => MixerId(_rankMantissaMixers, bitSize);
    internal int RankEscapeMixerId(int context) => MixerId(_rankEscapeMixers, context);
    internal int RunMixerId(int character) => MixerId(_runMixers, character);
    internal int RunExponentMixerId(int history, int bit) => MixerId(_runExponentMixers, history * RunExponentBits + bit);
    internal int RunMantissaMixerId(int bitSize) => MixerId(_runMantissaMixers, bitSize);

    internal int Mix(int mixerId, int charProbability, int stateProbability, int staticProbability, BitModelConfig config, out MixState state) {
      var bank = ResolveBank(mixerId, out var localId);
      return bank.Mix(localId, charProbability, stateProbability, staticProbability, out state);
    }

    internal void UpdateMixer(int mixerId, MixState state, int bit, BitModelConfig config) {
      var bank = ResolveBank(mixerId, out var localId);
      bank.Update(localId, state, bit, config.AdaptiveLearning, config.AdaptiveMixer);
    }

    private MixerBank ResolveBank(int encodedId, out int localId) {
      var kind = encodedId >>> 24;
      localId = encodedId & 0x00ffffff;
      return kind switch {
        1 => _rankMixers!,
        2 => _rankExponentMixers!,
        3 => _rankMantissaMixers!,
        4 => _rankEscapeMixers!,
        5 => _runMixers!,
        6 => _runExponentMixers!,
        7 => _runMantissaMixers!,
        _ => throw new InvalidOperationException("BSC: invalid QLFC mixer id"),
      };
    }

    private static int MixerId(MixerBank? bank, int localId) {
      if (bank is null)
        return 0;
      var kind = bank.Kind;
      return kind << 24 | localId;
    }

    private static short[] Filled(int length) {
      var result = new short[length];
      Array.Fill(result, InitialProbability);
      return result;
    }

    private sealed class MixerBank {
      private readonly int[] _weightChar;
      private readonly int[] _weightState;
      private readonly int[] _weightGlobal;
      private readonly short[] _probabilityMap;

      internal MixerBank(int kind, int count) {
        Kind = kind;
        _weightChar = new int[count];
        _weightState = new int[count];
        _weightGlobal = new int[count];
        _probabilityMap = new short[count * 17];
        for (var mixer = 0; mixer < count; ++mixer) {
          _weightChar[mixer] = _weightState[mixer] = 2048 << 5;
          var offset = mixer * 17;
          for (var probability = 0; probability < 17; ++probability)
            _probabilityMap[offset + probability] = (short)BscQlfcTables.SquashProbability((probability - 8) * 256);
        }
      }

      internal int Kind { get; }

      internal int Mix(int mixer, int charProbability, int stateProbability, int staticProbability, out MixState state) {
        var stretchedChar = BscQlfcTables.StretchProbability(charProbability);
        var stretchedState = BscQlfcTables.StretchProbability(stateProbability);
        var stretchedGlobal = BscQlfcTables.StretchProbability(staticProbability);
        var stretched = unchecked((stretchedChar * _weightChar[mixer]
          + stretchedState * _weightState[mixer]
          + stretchedGlobal * _weightGlobal[mixer]) >> 17);
        stretched = Math.Clamp(stretched, -2047, 2047);

        var index = (stretched + 2048) >> 8;
        var weight = stretched & 255;
        var probability = BscQlfcTables.SquashProbability(stretched);
        var mapOffset = mixer * 17 + index;
        var mappedProbability = _probabilityMap[mapOffset]
          + (((_probabilityMap[mapOffset + 1] - _probabilityMap[mapOffset]) * weight) >> 8);
        var mixedProbability = (3 * probability + mappedProbability) >> 2;
        state = new(stretchedChar, stretchedState, stretchedGlobal, mixedProbability, index);
        return mixedProbability;
      }

      internal void Update(int mixer, MixState state, int bit, Weights learning, Counter mapCounter) {
        var mapOffset = mixer * 17 + state.MapIndex;
        UpdateExplicit(ref _probabilityMap[mapOffset], bit, mapCounter);
        UpdateExplicit(ref _probabilityMap[mapOffset + 1], bit, mapCounter);
        var error = state.MixedProbability - (bit == 0 ? 4095 : 1);
        _weightChar[mixer] -= unchecked(learning.Char * error * state.StretchedChar) >> 16;
        _weightState[mixer] -= unchecked(learning.State * error * state.StretchedState) >> 16;
        _weightGlobal[mixer] -= unchecked(learning.Global * error * state.StretchedGlobal) >> 16;
      }
    }
  }
}
