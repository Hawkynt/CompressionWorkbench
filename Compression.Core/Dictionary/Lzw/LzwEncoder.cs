using Compression.Core.BitIO;

namespace Compression.Core.Dictionary.Lzw;

/// <summary>
/// Encodes data using the LZW (Lempel-Ziv-Welch) algorithm with variable-width codes.
/// </summary>
public sealed class LzwEncoder {
  private readonly Stream _output;
  private readonly int _minBits;
  private readonly int _maxBits;
  private readonly bool _useClearCode;
  private readonly bool _useStopCode;
  private readonly BitOrder _bitOrder;
  private readonly LzwCompressionLevel _level;

  /// <summary>
  /// Initializes a new <see cref="LzwEncoder"/>.
  /// </summary>
  /// <param name="output">The stream to write compressed data to.</param>
  /// <param name="minBits">Minimum (initial) code width in bits. Defaults to 9.</param>
  /// <param name="maxBits">Maximum code width in bits. Defaults to 12.</param>
  /// <param name="useClearCode">Whether to emit a clear code for dictionary resets.</param>
  /// <param name="useStopCode">Whether to emit a stop code at end of stream.</param>
  /// <param name="bitOrder">The bit ordering to use for output.</param>
  /// <param name="level">The compression level to use.</param>
  public LzwEncoder(
    Stream output,
    int minBits = 9,
    int maxBits = 12,
    bool useClearCode = true,
    bool useStopCode = true,
    BitOrder bitOrder = BitOrder.LsbFirst,
    LzwCompressionLevel level = LzwCompressionLevel.FirstMatch) {
    this._output = output ?? throw new ArgumentNullException(nameof(output));
    this._minBits = minBits;
    this._maxBits = maxBits;
    this._useClearCode = useClearCode;
    this._useStopCode = useStopCode;
    this._bitOrder = bitOrder;
    this._level = level;
  }

  /// <summary>
  /// Gets the clear code value (2^(minBits-1)).
  /// </summary>
  public int ClearCode => 1 << (this._minBits - 1);

  /// <summary>
  /// Gets the stop code value, or -1 if stop codes are disabled.
  /// </summary>
  public int StopCode => this._useStopCode ? this.ClearCode + (this._useClearCode ? 1 : 0) : -1;

  private int FirstUsableCode => this.ClearCode + (this._useClearCode ? 1 : 0) + (this._useStopCode ? 1 : 0);

  /// <summary>
  /// Encodes the input data and writes compressed LZW codes to the output stream.
  /// </summary>
  /// <param name="data">The data to compress.</param>
  public void Encode(ReadOnlySpan<byte> data) {
    switch (this._level) {
      case LzwCompressionLevel.Uncompressed:
        this.EncodeUncompressed(data);
        break;
      case LzwCompressionLevel.FirstMatch:
        this.EncodeFirstMatch(data);
        break;
      case LzwCompressionLevel.Optimal:
        this.EncodeOptimal(data);
        break;
      default:
        throw new ArgumentOutOfRangeException(nameof(this._level), this._level, "Unknown compression level.");
    }
  }

  private void EncodeUncompressed(ReadOnlySpan<byte> data) {
    var writer = new BitWriter(this._output, this._bitOrder);

    var clearCode = this.ClearCode;
    var stopCode = this.StopCode;
    var currentBits = this._minBits;
    var maxCode = 1 << this._maxBits;

    // Track decoder state so we emit codes at the right bit width.
    var nextCode = this.FirstUsableCode;
    var hasPrevious = false;

    if (this._useClearCode)
      writer.WriteBits((uint)clearCode, currentBits);

    foreach (var value in data) {
      writer.WriteBits(value, currentBits);

      // The decoder adds a new dictionary entry after every code
      // (except the first one after a reset, since previousEntry is null).
      if (hasPrevious && nextCode < maxCode) {
        ++nextCode;
        if (nextCode >= (1 << currentBits) && currentBits < this._maxBits)
          ++currentBits;
      }
      hasPrevious = true;

      // If dictionary is full, emit clear code to reset.
      if (nextCode < maxCode || !this._useClearCode)
        continue;

      writer.WriteBits((uint)clearCode, currentBits);
      currentBits = this._minBits;
      nextCode = this.FirstUsableCode;
      hasPrevious = false;
    }

    if (this._useStopCode)
      writer.WriteBits((uint)stopCode, currentBits);

    writer.FlushBits();
  }

  private void EncodeFirstMatch(ReadOnlySpan<byte> data) {
    var writer = new BitWriter(this._output, this._bitOrder);

    var clearCode = this.ClearCode;
    var stopCode = this.StopCode;
    var currentBits = this._minBits;
    var maxCode = 1 << this._maxBits;

    // Two counters: trieNextCode for assigning trie entries (can be 1 ahead),
    // decoderNextCode for tracking the decoder's nextCode (controls bit width).
    // The encoder eagerly adds trie entries for lookup, but the decoder only
    // adds entries when previousEntry is set (i.e., not on the first code after reset).
    var trieNextCode = this.FirstUsableCode;
    var decoderNextCode = this.FirstUsableCode;
    var hasPrevious = false;

    var trie = new Dictionary<(int ParentCode, byte Child), int>();

    if (this._useClearCode)
      writer.WriteBits((uint)clearCode, currentBits);

    if (data.IsEmpty) {
      if (this._useStopCode)
        writer.WriteBits((uint)stopCode, currentBits);

      writer.FlushBits();
      return;
    }

    int currentCode = data[0];
    var i = 1;

    while (i < data.Length) {
      var nextByte = data[i];
      var key = (currentCode, nextByte);

      if (trie.TryGetValue(key, out var existingCode)) {
        currentCode = existingCode;
        ++i;
      }
      else {
        writer.WriteBits((uint)currentCode, currentBits);

        // Always add trie entry for future lookups (if room).
        if (trieNextCode < maxCode) {
          trie[key] = trieNextCode;
          ++trieNextCode;
        }

        // Mirror decoder's nextCode: only increment when decoder has previousEntry.
        if (hasPrevious) {
          if (decoderNextCode < maxCode) {
            ++decoderNextCode;
            if (decoderNextCode >= (1 << currentBits) && currentBits < this._maxBits)
              ++currentBits;
          }
          else if (this._useClearCode) {
            writer.WriteBits((uint)clearCode, currentBits);
            trie.Clear();
            currentBits = this._minBits;
            trieNextCode = this.FirstUsableCode;
            decoderNextCode = this.FirstUsableCode;
            hasPrevious = false;
            currentCode = nextByte;
            ++i;
            continue;
          }
        }

        hasPrevious = true;
        currentCode = nextByte;
        ++i;
      }
    }

    writer.WriteBits((uint)currentCode, currentBits);

    // The decoder adds one more entry after the final data code.
    if (hasPrevious && decoderNextCode < maxCode) {
      ++decoderNextCode;
      if (decoderNextCode >= (1 << currentBits) && currentBits < this._maxBits)
        ++currentBits;
    }

    if (this._useStopCode)
      writer.WriteBits((uint)stopCode, currentBits);

    writer.FlushBits();
  }

  private void EncodeOptimal(ReadOnlySpan<byte> data) {
    if (data.IsEmpty || data.Length <= 2) {
      this.EncodeFirstMatch(data);
      return;
    }

    // Encode with greedy for comparison.
    byte[] greedyOutput;
    using (var greedyMs = new MemoryStream()) {
      new LzwEncoder(greedyMs, this._minBits, this._maxBits, this._useClearCode, this._useStopCode, this._bitOrder,
        LzwCompressionLevel.FirstMatch).Encode(data);
      
      greedyOutput = greedyMs.ToArray();
    }

    // Try DP encoding.
    var dpOutput = this.DpEncode(data);

    // Use whichever is smaller.
    if (dpOutput != null && dpOutput.Length <= greedyOutput.Length)
      this._output.Write(dpOutput, 0, dpOutput.Length);
    else
      this._output.Write(greedyOutput, 0, greedyOutput.Length);
  }

  private byte[]? DpEncode(ReadOnlySpan<byte> data) {
    var n = data.Length;
    var maxCode = 1 << this._maxBits;
    var firstUsable = this.FirstUsableCode;

    // --- Pass 1: Build greedy trie ---
    var greedyTrie = new Dictionary<(int ParentCode, byte Child), int>();
    {
      var gNext = firstUsable;
      int cur = data[0];
      for (var i = 1; i < n; ++i) {
        var nb = data[i];
        var key = (cur, nb);
        if (greedyTrie.TryGetValue(key, out var ex))
          cur = ex;
        else {
          if (gNext < maxCode)
            greedyTrie[key] = gNext++;

          cur = nb;
        }
      }
    }

    // --- Precompute bit widths for each code count ---
    // After k codes emitted, decoder's nextCode = firstUsable + max(0, k-1).
    var maxEntries = maxCode - firstUsable + 2;
    var bitWidthCount = Math.Min(n + 1, maxEntries);
    var bitWidthTable = new int[bitWidthCount];
    {
      var cb = this._minBits;
      var nc = firstUsable;
      for (var k = 0; k < bitWidthCount; ++k) {
        bitWidthTable[k] = cb;
        // After this code, decoder increments (except for the first code).
        if (k < 1 || nc >= maxCode)
          continue;

        ++nc;
        if (nc > (1 << cb) && cb < this._maxBits)
          ++cb;
      }
    }

    // --- Pass 2: Forward DP ---
    var cost = new double[n + 1];
    var predMatchLen = new int[n + 1];
    var codesOnPath = new int[n + 1];
    for (var i = 1; i <= n; ++i)
      cost[i] = double.MaxValue;

    for (var i = 0; i < n; ++i) {
      if (cost[i] == double.MaxValue)
        continue;

      var k = codesOnPath[i];
      var bits = bitWidthTable[Math.Min(k, bitWidthCount - 1)];
      var edgeCost = cost[i] + bits;

      // Length 1 (single byte, always available).
      if (edgeCost < cost[i + 1]) {
        cost[i + 1] = edgeCost;
        predMatchLen[i + 1] = 1;
        codesOnPath[i + 1] = k + 1;
      }

      // Longer matches via greedy trie.
      int code = data[i];
      var p = i + 1;
      while (p < n) {
        if (!greedyTrie.TryGetValue((code, data[p]), out var nx))
          break;

        code = nx;
        ++p;
        if (!(edgeCost < cost[p]))
          continue;

        cost[p] = edgeCost;
        predMatchLen[p] = p - i;
        codesOnPath[p] = k + 1;
      }
    }

    if (cost[n] == double.MaxValue)
      return null;

    // --- Traceback: get optimal match lengths ---
    var matchLengths = new List<int>();
    {
      var pos = n;
      while (pos > 0) {
        matchLengths.Add(predMatchLen[pos]);
        pos -= predMatchLen[pos];
      }
      matchLengths.Reverse();
    }

    // --- Pass 3: Encode with actual trie, guided by DP match lengths ---
    using var ms = new MemoryStream();
    var writer = new BitWriter(ms, this._bitOrder);
    var clearCode = this.ClearCode;
    var stopCode = this.StopCode;
    var currentBits = this._minBits;
    var trieNextCode = firstUsable;
    var decoderNextCode = firstUsable;
    var hasPrevious = false;
    var trie = new Dictionary<(int ParentCode, byte Child), int>();

    if (this._useClearCode)
      writer.WriteBits((uint)clearCode, currentBits);

    var dataPos = 0;
    var dpIdx = 0;

    while (dataPos < n) {
      // Get desired match length from DP (or greedy if exhausted).
      var desiredLen = (dpIdx < matchLengths.Count) ? matchLengths[dpIdx] : int.MaxValue;
      ++dpIdx;

      // Walk actual trie to find all available match lengths.
      int bestCode = data[dataPos];
      var bestLen = 1;
      var cur = bestCode;
      var desiredCode = (desiredLen == 1) ? bestCode : -1;

      for (var j = 1; dataPos + j < n; ++j) {
        if (!trie.TryGetValue((cur, data[dataPos + j]), out var nx))
          break;

        cur = nx;
        bestLen = j + 1;
        bestCode = cur;
        if (bestLen == desiredLen)
          desiredCode = cur;
      }

      // Use desired length if achievable; otherwise use longest available.
      int useLen, useCode;
      if (desiredCode >= 0 && desiredLen <= bestLen) {
        useLen = desiredLen;
        useCode = desiredCode;
      }
      else {
        useLen = bestLen;
        useCode = bestCode;
      }

      // Emit code.
      writer.WriteBits((uint)useCode, currentBits);

      // Add trie entry: (emitted code, next unmatched byte).
      if (trieNextCode < maxCode && dataPos + useLen < n) {
        var entryKey = (useCode, data[dataPos + useLen]);
        if (trie.TryAdd(entryKey, trieNextCode))
          ++trieNextCode;
      }

      // Mirror decoder's nextCode.
      if (hasPrevious) {
        if (decoderNextCode < maxCode) {
          ++decoderNextCode;
          if (decoderNextCode >= (1 << currentBits) && currentBits < this._maxBits)
            ++currentBits;

        } else if (this._useClearCode) {
          writer.WriteBits((uint)clearCode, currentBits);
          trie.Clear();
          currentBits = this._minBits;
          trieNextCode = firstUsable;
          decoderNextCode = firstUsable;
          hasPrevious = false;
          dataPos += useLen;
          continue;
        }
      }

      hasPrevious = true;
      dataPos += useLen;
    }

    // Account for decoder adding one more entry after the final code.
    if (hasPrevious && decoderNextCode < maxCode) {
      ++decoderNextCode;
      if (decoderNextCode >= (1 << currentBits) && currentBits < this._maxBits)
        ++currentBits;

    }

    if (this._useStopCode)
      writer.WriteBits((uint)stopCode, currentBits);

    writer.FlushBits();
    return ms.ToArray();
  }
}
