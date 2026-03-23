using Compression.Core.Dictionary.MatchFinders;
using Compression.Core.Transforms;

namespace Compression.Core.Dictionary.Ace;

/// <summary>
/// Encodes data using ACE 1.0 or 2.0 compression (LZ77 + dual Huffman trees).
/// Instance-based: the sliding window persists across calls for solid archive support.
/// ACE 2.0 adds sub-mode switching: LZ77, EXE, DELTA, SOUND, PIC.
/// </summary>
public sealed class AceEncoder {
  private const int MaxCodeLength = 16;
  private const int BlockSize = 32768;

  private const int SubModeLz77 = 0;
  private const int SubModeExe = 1;
  private const int SubModeDelta = 2;
  private const int SubModeSound = 3;
  private const int SubModePic = 4;

  private readonly int _dictBits;
  private readonly int _dictSize;
  private readonly byte[] _window;
  private readonly int _windowMask;
  private int _windowPos;
  private HashChainMatchFinder? _matchFinder;

  /// <summary>
  /// Initializes a new <see cref="AceEncoder"/> with the specified dictionary size.
  /// </summary>
  /// <param name="dictBits">Dictionary bits (10-22).</param>
  public AceEncoder(int dictBits = AceConstants.DefaultDictBits) {
    this._dictBits = dictBits;
    this._dictSize = 1 << dictBits;
    this._window = new byte[this._dictSize];
    this._windowMask = this._dictSize - 1;
  }

  /// <summary>
  /// Compresses data using the ACE 1.0 algorithm, preserving window state for subsequent solid calls.
  /// </summary>
  /// <param name="data">The data to compress.</param>
  /// <returns>The compressed data.</returns>
  public byte[] Encode(ReadOnlySpan<byte> data) {
    if (data.Length == 0)
      return [];

    // For solid mode, prepend recent window data so match finder can reference it
    byte[] workBuffer;
    int dataStart;

    if (this._windowPos > 0 && this._matchFinder != null) {
      // Solid continuation: prepend up to dictSize bytes of history
      var histLen = Math.Min(this._windowPos, this._dictSize);
      workBuffer = new byte[histLen + data.Length];
      // Copy history from window
      var histStart = (this._windowPos - histLen + this._dictSize) & this._windowMask;
      for (var i = 0; i < histLen; ++i)
        workBuffer[i] = this._window[(histStart + i) & this._windowMask];
      data.CopyTo(workBuffer.AsSpan(histLen));
      dataStart = histLen;
    }
    else {
      workBuffer = data.ToArray();
      dataStart = 0;
    }

    var output = new AceBitWriter();
    this._matchFinder = new HashChainMatchFinder(this._dictSize);

    // Insert history positions into match finder
    for (var i = 0; i < dataStart; ++i)
      this._matchFinder.InsertPosition(workBuffer, i);

    var pos = dataStart;

    while (pos < workBuffer.Length) {
      var tokens = new List<(int sym, int length, int distance)>();

      while (pos < workBuffer.Length && tokens.Count < BlockSize) {
        var match = this._matchFinder.FindMatch(workBuffer, pos, this._dictSize, 1032, 2);
        if (match.Length >= 2) {
          var lenSym = GetLengthSymbol(match.Length);
          tokens.Add((lenSym, match.Length, match.Distance));
          for (var i = 1; i < match.Length && pos + i < workBuffer.Length; ++i)
            this._matchFinder.InsertPosition(workBuffer, pos + i);
          pos += match.Length;
        }
        else {
          tokens.Add((workBuffer[pos], 0, 0));
          pos++;
        }
      }

      // Collect frequencies
      var mainFreq = new int[AceConstants.MainSymbols];
      var lenFreq = new int[AceConstants.LenSymbols];
      mainFreq[AceConstants.SymbolEndOfBlock] = 1;

      foreach (var (sym, length, distance) in tokens) {
        ++mainFreq[sym];
      }

      // Build Huffman trees
      var mainLengths = BuildCodeLengths(mainFreq, AceConstants.MainSymbols, MaxCodeLength);
      var lenLengths = BuildCodeLengths(lenFreq, AceConstants.LenSymbols, MaxCodeLength);

      var mainCodes = BuildCanonicalCodes(mainLengths);
      var lenCodes = BuildCanonicalCodes(lenLengths);

      // Write trees
      WriteHuffmanTree(output, mainLengths, AceConstants.MainSymbols);
      WriteHuffmanTree(output, lenLengths, AceConstants.LenSymbols);

      // Write tokens
      foreach (var (sym, length, distance) in tokens) {
        if (sym < 256) {
          output.WriteBits(mainCodes[sym], mainLengths[sym]);
        }
        else {
          output.WriteBits(mainCodes[sym], mainLengths[sym]);
          var lenIdx = sym - AceConstants.SymbolMatchBase;
          var extra = AceConstants.LengthExtra[lenIdx];
          if (extra > 0) {
            var extraVal = length - AceConstants.LengthBase[lenIdx];
            output.WriteBits((uint)extraVal, extra);
          }
          output.WriteBits(0, 2);
          output.WriteBits((uint)(distance - 1), this._dictBits);
        }
      }

      // Write end-of-block
      output.WriteBits(mainCodes[AceConstants.SymbolEndOfBlock],
        mainLengths[AceConstants.SymbolEndOfBlock]);
    }

    // Update window with the new data for solid continuation
    for (var i = dataStart; i < workBuffer.Length; ++i) {
      this._window[this._windowPos] = workBuffer[i];
      this._windowPos = (this._windowPos + 1) & this._windowMask;
    }

    return output.ToArray();
  }

  /// <summary>
  /// Compresses data using ACE 2.0 with a specified sub-mode applied as a preprocessing transform.
  /// </summary>
  /// <param name="data">The data to compress.</param>
  /// <param name="subMode">Sub-mode: 0=LZ77, 1=EXE, 2=DELTA, 3=SOUND, 4=PIC.</param>
  /// <param name="soundChannels">Channel count for SOUND mode (1-4).</param>
  /// <param name="picWidth">Image width for PIC mode.</param>
  /// <param name="picBytesPerPixel">Bytes per pixel for PIC mode (1-4).</param>
  /// <returns>The compressed data.</returns>
  public byte[] Encode20(ReadOnlySpan<byte> data, int subMode = SubModeLz77,
      int soundChannels = 1, int picWidth = 0, int picBytesPerPixel = 3) {
    if (data.Length == 0)
      return [];

    // Apply forward transform
    var transformed = ApplyForwardTransform(data, subMode, soundChannels, picWidth, picBytesPerPixel);

    // For solid mode, prepend recent window data
    byte[] workBuffer;
    int dataStart;
    if (this._windowPos > 0 && this._matchFinder != null) {
      var histLen = Math.Min(this._windowPos, this._dictSize);
      workBuffer = new byte[histLen + transformed.Length];
      var histStart = (this._windowPos - histLen + this._dictSize) & this._windowMask;
      for (var i = 0; i < histLen; ++i)
        workBuffer[i] = this._window[(histStart + i) & this._windowMask];
      transformed.AsSpan().CopyTo(workBuffer.AsSpan(histLen));
      dataStart = histLen;
    } else {
      workBuffer = transformed;
      dataStart = 0;
    }

    var output = new AceBitWriter();
    this._matchFinder = new HashChainMatchFinder(this._dictSize);
    for (var i = 0; i < dataStart; ++i)
      this._matchFinder.InsertPosition(workBuffer, i);

    var pos = dataStart;
    var modeSwitchWritten = false;

    while (pos < workBuffer.Length) {
      var tokens = new List<(int sym, int length, int distance)>();

      // Emit mode-switch symbol at start of first block if non-LZ77
      if (!modeSwitchWritten && subMode != SubModeLz77) {
        tokens.Add((AceConstants.SymbolModeSwitch, subMode, 0));
        modeSwitchWritten = true;
      }

      while (pos < workBuffer.Length && tokens.Count < BlockSize) {
        var match = this._matchFinder.FindMatch(workBuffer, pos, this._dictSize, 1032, 2);
        if (match.Length >= 2) {
          var lenSym = GetLengthSymbol(match.Length);
          tokens.Add((lenSym, match.Length, match.Distance));
          for (var i = 1; i < match.Length && pos + i < workBuffer.Length; ++i)
            this._matchFinder.InsertPosition(workBuffer, pos + i);
          pos += match.Length;
        } else {
          tokens.Add((workBuffer[pos], 0, 0));
          pos++;
        }
      }

      var mainFreq = new int[AceConstants.MainSymbols];
      var lenFreq = new int[AceConstants.LenSymbols];
      mainFreq[AceConstants.SymbolEndOfBlock] = 1;
      foreach (var (sym, _, _) in tokens)
        ++mainFreq[sym];

      var mainLengths = BuildCodeLengths(mainFreq, AceConstants.MainSymbols, MaxCodeLength);
      var lenLengths = BuildCodeLengths(lenFreq, AceConstants.LenSymbols, MaxCodeLength);
      var mainCodes = BuildCanonicalCodes(mainLengths);

      WriteHuffmanTree(output, mainLengths, AceConstants.MainSymbols);
      WriteHuffmanTree(output, lenLengths, AceConstants.LenSymbols);

      foreach (var (sym, length, distance) in tokens) {
        if (sym == AceConstants.SymbolModeSwitch) {
          output.WriteBits(mainCodes[sym], mainLengths[sym]);
          output.WriteBits((uint)length, 3); // 3-bit sub-mode value
        } else if (sym < 256) {
          output.WriteBits(mainCodes[sym], mainLengths[sym]);
        } else {
          output.WriteBits(mainCodes[sym], mainLengths[sym]);
          var lenIdx = sym - AceConstants.SymbolMatchBase;
          var extra = AceConstants.LengthExtra[lenIdx];
          if (extra > 0) {
            var extraVal = length - AceConstants.LengthBase[lenIdx];
            output.WriteBits((uint)extraVal, extra);
          }
          output.WriteBits(0, 2);
          output.WriteBits((uint)(distance - 1), this._dictBits);
        }
      }

      output.WriteBits(mainCodes[AceConstants.SymbolEndOfBlock],
        mainLengths[AceConstants.SymbolEndOfBlock]);
    }

    for (var i = dataStart; i < workBuffer.Length; ++i) {
      this._window[this._windowPos] = workBuffer[i];
      this._windowPos = (this._windowPos + 1) & this._windowMask;
    }

    return output.ToArray();
  }

  /// <summary>
  /// Static convenience method for non-solid encoding (creates a fresh encoder).
  /// </summary>
  public static byte[] EncodeBlock(ReadOnlySpan<byte> data, int dictBits = AceConstants.DefaultDictBits) {
    var encoder = new AceEncoder(dictBits);
    return encoder.Encode(data);
  }

  /// <summary>
  /// Static convenience method for non-solid ACE 2.0 encoding.
  /// </summary>
  public static byte[] EncodeBlock20(ReadOnlySpan<byte> data, int dictBits = AceConstants.DefaultDictBits,
      int subMode = 0, int soundChannels = 1, int picWidth = 0, int picBytesPerPixel = 3) {
    var encoder = new AceEncoder(dictBits);
    return encoder.Encode20(data, subMode, soundChannels, picWidth, picBytesPerPixel);
  }

  private static byte[] ApplyForwardTransform(ReadOnlySpan<byte> data, int subMode,
      int soundChannels, int picWidth, int picBytesPerPixel) {
    return subMode switch {
      SubModeLz77 => data.ToArray(),
      SubModeExe => BcjFilter.EncodeX86(data),
      SubModeDelta => DeltaFilter.Encode(data),
      SubModeSound => AceSoundFilter.Encode(data, soundChannels),
      SubModePic => AcePicFilter.Encode(data, picWidth > 0 ? picWidth * picBytesPerPixel : 0),
      _ => data.ToArray(),
    };
  }

  private static int GetLengthSymbol(int length) {
    for (var i = AceConstants.LengthBase.Length - 1; i >= 0; --i) {
      if (length >= AceConstants.LengthBase[i])
        return AceConstants.SymbolMatchBase + i;
    }
    return AceConstants.SymbolMatchBase;
  }

  private static void WriteHuffmanTree(AceBitWriter output, int[] codeLengths, int numSymbols) {
    var usedCount = 0;
    for (var i = 0; i < numSymbols; ++i)
      if (codeLengths[i] > 0) ++usedCount;

    if (usedCount == 0) {
      output.WriteBits(0, 9);
      output.WriteBits(0, 9);
      return;
    }

    if (usedCount == 1) {
      output.WriteBits(0, 9);
      for (var i = 0; i < numSymbols; ++i) {
        if (codeLengths[i] > 0) {
          output.WriteBits((uint)i, 9);
          break;
        }
      }
      return;
    }

    // RLE encode the code lengths
    var rle = new List<int>();
    var idx = 0;
    while (idx < numSymbols) {
      if (codeLengths[idx] == 0) {
        var run = 1;
        while (idx + run < numSymbols && codeLengths[idx + run] == 0) ++run;
        var totalRun = run;
        while (run > 0) {
          if (run >= 11) {
            rle.Add(18);
            rle.Add(Math.Min(run - 11, 127));
            run -= Math.Min(run, 138);
          }
          else if (run >= 3) {
            rle.Add(17);
            rle.Add(run - 3);
            run = 0;
          }
          else {
            rle.Add(0);
            --run;
          }
        }
        idx += totalRun;
      }
      else {
        rle.Add(codeLengths[idx]);
        var prev = codeLengths[idx];
        ++idx;
        var rep = 0;
        while (idx < numSymbols && codeLengths[idx] == prev && rep < 6) {
          ++rep;
          ++idx;
        }
        while (rep >= 3) {
          rle.Add(16);
          rle.Add(rep - 3);
          rep = 0;
        }
        while (rep > 0) {
          rle.Add(prev);
          --rep;
        }
      }
    }

    // Build pre-tree from RLE symbols (0-18)
    var preFreq = new int[19];
    for (var i = 0; i < rle.Count; ++i) {
      var sym = rle[i];
      if (sym <= 18) ++preFreq[sym];
      if (sym >= 16 && sym <= 18) ++i; // skip extra data
    }

    var preLengths = BuildCodeLengths(preFreq, 19, MaxCodeLength);
    var preCodes = BuildCanonicalCodes(preLengths);

    var preCount = 19;
    while (preCount > 0 && preLengths[preCount - 1] == 0) --preCount;
    output.WriteBits((uint)preCount, 9);
    for (var i = 0; i < preCount; ++i)
      output.WriteBits((uint)preLengths[i], 4);

    for (var i = 0; i < rle.Count; ++i) {
      var sym = rle[i];
      if (sym <= 15) {
        output.WriteBits(preCodes[sym], preLengths[sym]);
      }
      else if (sym == 16) {
        output.WriteBits(preCodes[16], preLengths[16]);
        output.WriteBits((uint)rle[++i], 2);
      }
      else if (sym == 17) {
        output.WriteBits(preCodes[17], preLengths[17]);
        output.WriteBits((uint)rle[++i], 3);
      }
      else if (sym == 18) {
        output.WriteBits(preCodes[18], preLengths[18]);
        output.WriteBits((uint)rle[++i], 7);
      }
    }
  }

  private static int[] BuildCodeLengths(int[] freq, int numSymbols, int maxBits) {
    var lengths = new int[numSymbols];
    var symbols = new List<(int sym, int freq)>();

    for (var i = 0; i < numSymbols; ++i)
      if (freq[i] > 0)
        symbols.Add((i, freq[i]));

    if (symbols.Count == 0) return lengths;
    if (symbols.Count == 1) {
      lengths[symbols[0].sym] = 1;
      return lengths;
    }

    var pq = new PriorityQueue<int, long>();
    var nodes = new List<(long freq, int sym, int left, int right)>();

    for (var i = 0; i < symbols.Count; ++i) {
      nodes.Add((symbols[i].freq, symbols[i].sym, -1, -1));
      pq.Enqueue(i, symbols[i].freq);
    }

    while (pq.Count > 1) {
      pq.TryDequeue(out var a, out var fa);
      pq.TryDequeue(out var b, out var fb);
      var newIdx = nodes.Count;
      nodes.Add((fa + fb, -1, a, b));
      pq.Enqueue(newIdx, fa + fb);
    }

    pq.TryDequeue(out var root, out _);

    void Walk(int nodeIdx, int depth) {
      var node = nodes[nodeIdx];
      if (node.sym >= 0) {
        lengths[node.sym] = Math.Max(depth, 1);
        return;
      }
      Walk(node.left, depth + 1);
      Walk(node.right, depth + 1);
    }
    Walk(root, 0);

    for (var i = 0; i < numSymbols; ++i)
      if (lengths[i] > maxBits)
        lengths[i] = maxBits;

    var kraftMax = 1L << maxBits;
    long kraftSum = 0;
    for (var i = 0; i < numSymbols; ++i)
      if (lengths[i] > 0)
        kraftSum += kraftMax >> lengths[i];

    while (kraftSum > kraftMax) {
      for (var i = numSymbols - 1; i >= 0; --i) {
        if (lengths[i] > 0 && lengths[i] < maxBits) {
          kraftSum -= kraftMax >> lengths[i];
          ++lengths[i];
          kraftSum += kraftMax >> lengths[i];
          if (kraftSum <= kraftMax) break;
        }
      }
    }

    return lengths;
  }

  private static uint[] BuildCanonicalCodes(int[] lengths) {
    var maxLen = 0;
    foreach (var l in lengths)
      if (l > maxLen) maxLen = l;
    if (maxLen == 0) return new uint[lengths.Length];

    var blCount = new int[maxLen + 1];
    foreach (var l in lengths)
      if (l > 0) ++blCount[l];

    var nextCode = new uint[maxLen + 1];
    uint code = 0;
    for (var b = 1; b <= maxLen; ++b) {
      code = (code + (uint)blCount[b - 1]) << 1;
      nextCode[b] = code;
    }

    var codes = new uint[lengths.Length];
    for (var i = 0; i < lengths.Length; ++i)
      if (lengths[i] > 0)
        codes[i] = nextCode[lengths[i]]++;

    return codes;
  }
}

/// <summary>
/// MSB-first bit writer for ACE encoding.
/// </summary>
internal sealed class AceBitWriter {
  private readonly List<byte> _output = [];
  private uint _buffer;
  private int _bitsUsed;

  public void WriteBits(uint value, int count) {
    for (var i = count - 1; i >= 0; --i) {
      this._buffer = (this._buffer << 1) | ((value >> i) & 1);
      if (++this._bitsUsed == 16) {
        this._output.Add((byte)(this._buffer & 0xFF));
        this._output.Add((byte)((this._buffer >> 8) & 0xFF));
        this._buffer = 0;
        this._bitsUsed = 0;
      }
    }
  }

  public byte[] ToArray() {
    if (this._bitsUsed > 0) {
      this._buffer <<= (16 - this._bitsUsed);
      this._output.Add((byte)(this._buffer & 0xFF));
      this._output.Add((byte)((this._buffer >> 8) & 0xFF));
    }
    return [.. this._output];
  }
}
