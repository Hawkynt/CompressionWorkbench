using Compression.Core.BitIO;

namespace Compression.Core.Dictionary.Rar;

/// <summary>
/// Decompressor for RAR v2.x archives (UnPack 2.0 algorithm).
/// Uses 3 adaptive Huffman tables (main, distance, length) and a sliding window.
/// </summary>
public sealed class Rar2Decoder {
  private const int MainTableSize = 298; // 256 literals + 16 lengths + 16 distances + 10 specials
  private const int DistTableSize = 48;
  private const int LenTableSize = 28;
  private const int MaxCodeLength = 15;
  private const int WindowSize = 0x100000; // 1 MB max
  private const int WindowMask = WindowSize - 1;

  private static readonly int[] LenBits = [0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 2, 2, 2, 2, 3, 3, 3, 3, 4, 4, 4, 4, 5, 5, 5, 5];
  private static readonly int[] LenBase = [0, 1, 2, 3, 4, 5, 6, 7, 8, 10, 12, 14, 16, 20, 24, 28, 32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224];
  private static readonly int[] DistBits = [0, 0, 0, 0, 1, 1, 2, 2, 3, 3, 4, 4, 5, 5, 6, 6, 7, 7, 8, 8, 9, 9, 10, 10, 11, 11, 12, 12, 13, 13, 14, 14, 15, 15, 16, 16, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0];
  private static readonly int[] DistBase = [0, 1, 2, 3, 4, 6, 8, 12, 16, 24, 32, 48, 64, 96, 128, 192, 256, 384, 512, 768, 1024, 1536, 2048, 3072, 4096, 6144, 8192, 12288, 16384, 24576, 32768, 49152, 65536, 98304, 131072, 196608, 262144, 327680, 393216, 458752, 524288, 589824, 655360, 720896, 786432, 851968, 917504, 983040];

  private readonly byte[] _window = new byte[WindowSize];
  private int _windowPos;
  private int _lastDist;
  private int _lastLength;
  private readonly int[] _oldDist = new int[4];
  private int _oldDistPtr;

  /// <summary>
  /// Tracks adaptive prediction state for one audio channel.
  /// </summary>
  private sealed class AudioState {
    public int K1, K2, K3, K4, K5;
    public int D1, D2, D3, D4;
    public int LastDelta;
    public int LastByte;
  }

  /// <summary>
  /// Decompresses RAR v2.x data.
  /// </summary>
  /// <param name="compressed">The compressed data.</param>
  /// <param name="unpackedSize">The expected uncompressed size.</param>
  /// <returns>The decompressed data.</returns>
  public byte[] Decompress(ReadOnlySpan<byte> compressed, int unpackedSize) {
    var output = new byte[unpackedSize];
    var outPos = 0;

    using var ms = new MemoryStream(compressed.ToArray());
    var bitReader = new BitBuffer<LsbBitOrder>(ms);

    // Read the audio mode flag
    var audioMode = bitReader.ReadBits(1) != 0;

    // Audio mode state
    var numChannels = 0;
    var audioDecoders = Array.Empty<(int[] Symbols, int[] Lengths, int MaxBits)>();
    var audioStates = Array.Empty<AudioState>();
    var currentChannel = 0;

    // Normal mode state
    var mainDecoder = default((int[] Symbols, int[] Lengths, int MaxBits));
    var distDecoder = default((int[] Symbols, int[] Lengths, int MaxBits));
    var lenDecoder = default((int[] Symbols, int[] Lengths, int MaxBits));

    if (audioMode) {
      ReadAudioTables(bitReader, out numChannels, out audioDecoders, out audioStates);
      currentChannel = 0;
    } else {
      ReadNormalTables(bitReader, out mainDecoder, out distDecoder, out lenDecoder);
    }

    while (outPos < unpackedSize) {
      if (audioMode) {
        // Audio mode: decode one sample per channel, round-robin
        var sym = DecodeSymbol(bitReader, audioDecoders[currentChannel]);
        var delta = sym > 127 ? sym - 256 : sym;
        var state = audioStates[currentChannel];
        var predicted = AudioPredict(state);
        var outputByte = (byte)((predicted - delta) & 0xFF);

        var signedOutput = (int)(sbyte)outputByte;
        var signedPredicted = (int)(sbyte)(byte)predicted;
        var curDelta = signedOutput - signedPredicted;

        // Shift deltas
        state.D4 = state.D3;
        state.D3 = state.D2;
        state.D2 = state.D1;
        state.D1 = state.LastDelta;
        state.LastDelta = curDelta;
        state.LastByte = outputByte;

        // Adapt weights
        AdaptWeight(ref state.K1, state.D1, curDelta);
        AdaptWeight(ref state.K2, state.D2, curDelta);
        AdaptWeight(ref state.K3, state.D3, curDelta);
        AdaptWeight(ref state.K4, state.D4, curDelta);
        AdaptWeight(ref state.K5, state.LastByte, curDelta);

        this._window[this._windowPos++ & WindowMask] = outputByte;
        output[outPos++] = outputByte;
        currentChannel = (currentChannel + 1) % numChannels;
      } else {
        // Normal LZ mode
        var sym = DecodeSymbol(bitReader, mainDecoder);

        if (sym < 256) {
          // Literal byte
          this._window[this._windowPos++ & WindowMask] = (byte)sym;
          output[outPos++] = (byte)sym;
        } else if (sym == 256) {
          // Re-read tables
          audioMode = bitReader.ReadBits(1) != 0;
          if (audioMode) {
            ReadAudioTables(bitReader, out numChannels, out audioDecoders, out audioStates);
            currentChannel = 0;
          } else {
            ReadNormalTables(bitReader, out mainDecoder, out distDecoder, out lenDecoder);
          }
        } else if (sym <= 270) {
          // Length/distance encoded together
          var lengthCode = sym - 257;
          int length;
          int distSym;

          if (lengthCode < 8) {
            // Short match: length = 2, distance from old distances
            length = 2;
            distSym = lengthCode;
            var dist = this._oldDist[distSym & 3];
            this._lastDist = dist;
            this._lastLength = length;
            CopyMatch(output, ref outPos, dist, length, unpackedSize);
          } else {
            // Longer match
            lengthCode -= 8;
            length = Rar2Decoder.LenBase[lengthCode] + 3;
            if (Rar2Decoder.LenBits[lengthCode] > 0)
              length += (int)bitReader.ReadBits(Rar2Decoder.LenBits[lengthCode]);

            distSym = DecodeSymbol(bitReader, distDecoder);
            var dist = Rar2Decoder.DistBase[distSym] + 1;
            if (Rar2Decoder.DistBits[distSym] > 0)
              dist += (int)bitReader.ReadBits(Rar2Decoder.DistBits[distSym]);

            this._lastDist = dist;
            this._lastLength = length;
            this._oldDist[this._oldDistPtr++ & 3] = dist;
            CopyMatch(output, ref outPos, dist, length, unpackedSize);
          }
        } else {
          // sym 271-297: length from length table + distance from distance table
          var lenSym = DecodeSymbol(bitReader, lenDecoder);
          var length = Rar2Decoder.LenBase[lenSym] + 3;
          if (Rar2Decoder.LenBits[lenSym] > 0)
            length += (int)bitReader.ReadBits(Rar2Decoder.LenBits[lenSym]);

          var distSym = DecodeSymbol(bitReader, distDecoder);
          var dist = Rar2Decoder.DistBase[distSym] + 1;
          if (Rar2Decoder.DistBits[distSym] > 0)
            dist += (int)bitReader.ReadBits(Rar2Decoder.DistBits[distSym]);

          this._lastDist = dist;
          this._lastLength = length;
          this._oldDist[this._oldDistPtr++ & 3] = dist;
          CopyMatch(output, ref outPos, dist, length, unpackedSize);
        }
      }
    }

    return output;
  }

  private void CopyMatch(byte[] output, ref int outPos, int distance, int length, int limit) {
    for (var i = 0; i < length && outPos < limit; ++i) {
      var srcPos = (this._windowPos - distance) & WindowMask;
      var b = this._window[srcPos];
      this._window[this._windowPos & WindowMask] = b;
      ++this._windowPos;
      output[outPos++] = b;
    }
  }

  private static int[] ReadCodeLengths(BitBuffer<LsbBitOrder> bitReader, int count) {
    var lengths = new int[count];
    var i = 0;
    while (i < count) {
      var val = (int)bitReader.ReadBits(4);
      if (val == 15) {
        var zeroCount = (int)bitReader.ReadBits(4);
        if (zeroCount == 0) {
          lengths[i++] = 15;
        } else {
          zeroCount += 2;
          while (zeroCount-- > 0 && i < count)
            lengths[i++] = 0;
        }
      } else
        lengths[i++] = val;
    }
    return lengths;
  }

  private static (int[] Symbols, int[] Lengths, int MaxBits) BuildDecoder(int[] codeLengths, int numSymbols) {
    var maxBits = 0;
    for (var i = 0; i < numSymbols; ++i)
      maxBits = Math.Max(maxBits, codeLengths[i]);

    if (maxBits == 0) maxBits = 1;

    var tableSize = 1 << maxBits;
    var symbols = new int[tableSize];
    var lengths = new int[tableSize];

    // Build canonical codes
    var blCount = new int[Rar2Decoder.MaxCodeLength + 1];
    for (var i = 0; i < numSymbols; ++i)
      if (codeLengths[i] > 0)
        ++blCount[codeLengths[i]];

    var nextCode = new int[Rar2Decoder.MaxCodeLength + 1];
    var code = 0;
    for (var bits = 1; bits <= maxBits; ++bits) {
      code = (code + blCount[bits - 1]) << 1;
      nextCode[bits] = code;
    }

    // Fill lookup table
    Array.Fill(symbols, -1);
    for (var sym = 0; sym < numSymbols; ++sym) {
      var len = codeLengths[sym];
      if (len <= 0) continue;
      var c = nextCode[len]++;
      // Fill all entries with prefix matching this code
      var prefix = c << (maxBits - len);
      var count = 1 << (maxBits - len);
      for (var j = 0; j < count; ++j) {
        symbols[prefix + j] = sym;
        lengths[prefix + j] = len;
      }
    }

    return (symbols, lengths, maxBits);
  }

  private static int DecodeSymbol(BitBuffer<LsbBitOrder> bitReader, (int[] Symbols, int[] Lengths, int MaxBits) decoder) {
    var peekBits = (int)bitReader.PeekBits(decoder.MaxBits);
    var sym = decoder.Symbols[peekBits];
    if (sym < 0)
      throw new InvalidDataException("Invalid Huffman code in RAR v2 data.");
    bitReader.DropBits(decoder.Lengths[peekBits]);
    return sym;
  }

  private static void ReadNormalTables(
      BitBuffer<LsbBitOrder> bitReader,
      out (int[] Symbols, int[] Lengths, int MaxBits) mainDecoder,
      out (int[] Symbols, int[] Lengths, int MaxBits) distDecoder,
      out (int[] Symbols, int[] Lengths, int MaxBits) lenDecoder) {
    var mainLens = ReadCodeLengths(bitReader, Rar2Decoder.MainTableSize);
    var distLens = ReadCodeLengths(bitReader, Rar2Decoder.DistTableSize);
    var lenLens = ReadCodeLengths(bitReader, Rar2Decoder.LenTableSize);
    mainDecoder = BuildDecoder(mainLens, Rar2Decoder.MainTableSize);
    distDecoder = BuildDecoder(distLens, Rar2Decoder.DistTableSize);
    lenDecoder = BuildDecoder(lenLens, Rar2Decoder.LenTableSize);
  }

  private const int AudioTableSize = 256;

  private static void ReadAudioTables(
      BitBuffer<LsbBitOrder> bitReader,
      out int numChannels,
      out (int[] Symbols, int[] Lengths, int MaxBits)[] audioDecoders,
      out AudioState[] audioStates) {
    numChannels = (int)bitReader.ReadBits(2) + 1;
    audioDecoders = new (int[], int[], int)[numChannels];
    audioStates = new AudioState[numChannels];
    for (var ch = 0; ch < numChannels; ++ch) {
      var lens = ReadCodeLengths(bitReader, Rar2Decoder.AudioTableSize);
      audioDecoders[ch] = BuildDecoder(lens, Rar2Decoder.AudioTableSize);
      audioStates[ch] = new AudioState();
    }
  }

  /// <summary>
  /// Computes the predicted sample value for an audio channel.
  /// </summary>
  private static int AudioPredict(AudioState s) =>
    ((s.K1 * s.D1 + s.K2 * s.D2 + s.K3 * s.D3 + s.K4 * s.D4 + s.K5 * s.LastByte) >> 3) & 0xFF;

  /// <summary>
  /// Adapts a prediction weight based on the sign correlation between a historical delta and the current delta.
  /// </summary>
  private static void AdaptWeight(ref int weight, int d, int curDelta) {
    if (d != 0 && curDelta != 0)
      weight += (d ^ curDelta) >= 0 ? 1 : -1;
  }
}
