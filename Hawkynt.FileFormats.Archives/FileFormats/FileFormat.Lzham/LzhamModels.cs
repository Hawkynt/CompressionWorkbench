// Converted from LZHAM (Rich Geldreich), public domain: lzham_symbol_codec.cpp,
// lzham_huffman_codes.cpp, lzham_prefix_coding.cpp and lzham_lzdecompbase.cpp.
// See THIRD-PARTY-NOTICE.lzham.txt in this directory.
namespace FileFormat.Lzham;

/// <summary>LZHAM's fixed tables and limits (<c>CLZDecompBase</c>).</summary>
internal static class LzhamTables {
  public const int MinMatchLen = 2;
  public const int MaxMatchLen = 257;
  public const int MaxHugeMatchLen = 65536;
  public const int MaxLen2MatchDist = 2047;
  public const int MinDictSizeLog2 = 15;
  public const int MaxDictSizeLog2 = 29;

  public const int NumSecondaryLengths = 249;
  public const int NumHugeMatchCodes = 1;
  public const int NumSpecialLengths = 2;
  public const int LowestUsableMatchSlot = 1;

  public const int SpecialCodeEndOfBlock = 0;
  public const int SpecialCodePartialStateReset = 1;

  public const int BlockHeaderBits = 2;
  public const int BlockFlushTypeBits = 2;
  public const int SyncBlock = 0;
  public const int CompBlock = 1;
  public const int RawBlock = 2;
  public const int EofBlock = 3;

  public const int NumStates = 12;
  public const int NumLitStates = 7;

  public const int DefaultTableUpdateRate = 8;
  public const int FastestTableUpdateRate = 20;

  /// <summary><c>g_table_update_settings</c>: (max update interval, slow rate) per update rate 1..20.</summary>
  public static readonly (ushort MaxUpdateInterval, ushort SlowRate)[] TableUpdateSettings = [
    (4, 32), (5, 33), (6, 34), (7, 35), (8, 36),
    (16, 48), (32, 72), (64, 64), (98, 80), (128, 96),
    (192, 112), (256, 128), (512, 128 + 16 * 2), (1024, 128 + 16 * 4), (2048, 128 + 16 * 6),
    (2048, 128 + 16 * 8), (2048, 128 + 16 * 10), (2048, 128 + 16 * 12), (2048, 128 + 16 * 14), (2048, 128 + 16 * 16),
  ];

  public static readonly uint[] PositionBase = [
    0x0, 0x1, 0x2, 0x3, 0x4, 0x6, 0x8, 0xC, 0x10, 0x18, 0x20, 0x30, 0x40, 0x60, 0x80, 0xC0,
    0x100, 0x180, 0x200, 0x300, 0x400, 0x600, 0x800, 0xC00, 0x1000, 0x1800, 0x2000, 0x3000, 0x4000, 0x6000, 0x8000, 0xC000,
    0x10000, 0x18000, 0x20000, 0x30000, 0x40000, 0x60000, 0x80000, 0xC0000, 0x100000, 0x180000, 0x200000, 0x300000, 0x400000, 0x600000, 0x800000, 0xC00000,
    0x1000000, 0x1800000, 0x2000000, 0x3000000, 0x4000000, 0x6000000, 0x8000000, 0xA000000, 0xC000000, 0xE000000, 0x10000000, 0x12000000, 0x14000000, 0x16000000, 0x18000000, 0x1A000000,
    0x1C000000, 0x1E000000, 0x20000000, 0x22000000, 0x24000000, 0x26000000, 0x28000000, 0x2A000000, 0x2C000000, 0x2E000000, 0x30000000, 0x32000000, 0x34000000, 0x36000000, 0x38000000, 0x3A000000,
    0x3C000000, 0x3E000000, 0x40000000, 0x42000000, 0x44000000, 0x46000000, 0x48000000, 0x4A000000, 0x4C000000, 0x4E000000, 0x50000000, 0x52000000, 0x54000000, 0x56000000, 0x58000000, 0x5A000000,
    0x5C000000, 0x5E000000, 0x60000000, 0x62000000, 0x64000000, 0x66000000, 0x68000000, 0x6A000000, 0x6C000000, 0x6E000000, 0x70000000, 0x72000000, 0x74000000, 0x76000000, 0x78000000, 0x7A000000,
    0x7C000000, 0x7E000000, 0x80000000, 0x82000000, 0x84000000, 0x86000000, 0x88000000, 0x8A000000, 0x8C000000, 0x8E000000, 0x90000000, 0x92000000, 0x94000000, 0x96000000, 0x98000000, 0x9A000000,
  ];

  public static readonly byte[] PositionExtraBits = [
    0x0, 0x0, 0x0, 0x0, 0x1, 0x1, 0x2, 0x2, 0x3, 0x3, 0x4, 0x4, 0x5, 0x5, 0x6, 0x6,
    0x7, 0x7, 0x8, 0x8, 0x9, 0x9, 0xA, 0xA, 0xB, 0xB, 0xC, 0xC, 0xD, 0xD, 0xE, 0xE,
    0xF, 0xF, 0x10, 0x10, 0x11, 0x11, 0x12, 0x12, 0x13, 0x13, 0x14, 0x14, 0x15, 0x15, 0x16, 0x16,
    0x17, 0x17, 0x18, 0x18, 0x19, 0x19, 0x19, 0x19, 0x19, 0x19, 0x19, 0x19, 0x19, 0x19, 0x19, 0x19,
    0x19, 0x19, 0x19, 0x19, 0x19, 0x19, 0x19, 0x19, 0x19, 0x19, 0x19, 0x19, 0x19, 0x19, 0x19, 0x19,
    0x19, 0x19, 0x19, 0x19, 0x19, 0x19, 0x19, 0x19, 0x19, 0x19, 0x19, 0x19, 0x19, 0x19, 0x19, 0x19,
    0x19, 0x19, 0x19, 0x19, 0x19, 0x19, 0x19, 0x19, 0x19, 0x19, 0x19, 0x19, 0x19, 0x19, 0x19, 0x19,
    0x19, 0x19, 0x19, 0x19, 0x19, 0x19, 0x19, 0x19, 0x19, 0x19, 0x19, 0x19, 0x19, 0x19, 0x19, 0x19,
  ];

  private static readonly byte[] NumPositionSlots = [30, 32, 34, 36, 38, 40, 42, 44, 46, 48, 50, 52, 54, 58, 66];

  public static int PositionSlots(int dictSizeLog2) => NumPositionSlots[dictSizeLog2 - MinDictSizeLog2];

  public static int MainTableSymbols(int dictSizeLog2) => NumSpecialLengths + (PositionSlots(dictSizeLog2) - LowestUsableMatchSlot) * 8;

  public static (ushort MaxUpdateInterval, ushort SlowRate) UpdateSettings(int tableUpdateRate) {
    if (tableUpdateRate == 0)
      tableUpdateRate = DefaultTableUpdateRate;
    return TableUpdateSettings[Math.Clamp(tableUpdateRate, 1, FastestTableUpdateRate) - 1];
  }
}

/// <summary>
/// LZHAM's quasi-adaptive Huffman model (<c>raw_quasi_adaptive_huffman_data_model</c>): symbol
/// frequencies are counted as symbols are coded, and the canonical code is rebuilt after an
/// interval that grows with each rebuild up to a maximum.
/// </summary>
internal sealed class LzhamHuffmanModel {
  private const int DefaultMaxUpdateInterval = 64;
  private const int DefaultAdaptRate = 64;
  private const int MaxExpectedCodeSize = 16;

  private readonly bool _encoding;
  private readonly ushort _maxUpdateInterval;
  private readonly ushort _adaptRate;
  private readonly uint _maxCycle;

  public readonly int TotalSyms;
  public readonly ushort[] SymFreq;
  public readonly byte[] CodeSizes;
  public readonly ushort[] Codes;

  private uint _updateCycle;
  public uint SymbolsUntilUpdate;
  private uint _totalCount;

  // Decoder tables (canonical decoding without the acceleration table).
  private readonly uint[] _maxCodes = new uint[MaxExpectedCodeSize + 1];
  private readonly int[] _valPtrs = new int[MaxExpectedCodeSize + 1];
  private ushort[] _sortedSymbolOrder = [];
  private int _totalUsedSyms;
  private int _minCodeSize;

  public LzhamHuffmanModel(bool encoding, int totalSyms, int maxUpdateInterval, int adaptRate) {
    _encoding = encoding;
    TotalSyms = totalSyms;
    _maxUpdateInterval = (ushort)maxUpdateInterval;
    _adaptRate = (ushort)adaptRate;
    SymFreq = new ushort[totalSyms];
    CodeSizes = new byte[totalSyms];
    Codes = encoding ? new ushort[totalSyms] : [];
    var maxCycle = (uint)((Math.Max(24, totalSyms) + 6) * (_maxUpdateInterval != 0 ? _maxUpdateInterval : DefaultMaxUpdateInterval));
    _maxCycle = Math.Min(maxCycle, 32767u);
    Reset();
  }

  private LzhamHuffmanModel(LzhamHuffmanModel other) {
    _encoding = other._encoding;
    _maxUpdateInterval = other._maxUpdateInterval;
    _adaptRate = other._adaptRate;
    _maxCycle = other._maxCycle;
    TotalSyms = other.TotalSyms;
    SymFreq = (ushort[])other.SymFreq.Clone();
    CodeSizes = (byte[])other.CodeSizes.Clone();
    Codes = (ushort[])other.Codes.Clone();
    _updateCycle = other._updateCycle;
    SymbolsUntilUpdate = other.SymbolsUntilUpdate;
    _totalCount = other._totalCount;
    Array.Copy(other._maxCodes, _maxCodes, _maxCodes.Length);
    Array.Copy(other._valPtrs, _valPtrs, _valPtrs.Length);
    _sortedSymbolOrder = (ushort[])other._sortedSymbolOrder.Clone();
    _totalUsedSyms = other._totalUsedSyms;
    _minCodeSize = other._minCodeSize;
  }

  /// <summary>A deep copy (<c>assign</c>).</summary>
  public LzhamHuffmanModel Clone() => new(this);

  public void Reset() {
    Array.Fill(SymFreq, (ushort)1);
    _updateCycle = (uint)TotalSyms;
    _totalCount = 0;
    SymbolsUntilUpdate = 0;
    UpdateTables((int)Math.Min(_maxCycle, 16u), symFreqAllOnes: true);
  }

  private void Rescale() {
    uint total = 0;
    for (var i = 0; i < TotalSyms; ++i) {
      var freq = (uint)(SymFreq[i] + 1) >> 1;
      total += freq;
      SymFreq[i] = (ushort)freq;
    }

    _totalCount = total;
  }

  public void ResetUpdateRate() {
    _totalCount += _updateCycle - SymbolsUntilUpdate;
    if (_totalCount > TotalSyms)
      Rescale();
    SymbolsUntilUpdate = _updateCycle = Math.Min(8u, _updateCycle);
  }

  /// <summary>Counts a coded symbol and rebuilds the code when the interval is up.</summary>
  public void Update(int symbol) {
    ++SymFreq[symbol];
    if (--SymbolsUntilUpdate == 0)
      UpdateTables(-1, symFreqAllOnes: false);
  }

  private void UpdateTables(int forceUpdateCycle, bool symFreqAllOnes) {
    _totalCount += _updateCycle;
    while (_totalCount >= 32768)
      Rescale();

    var maxCodeSize = 0;
    if (symFreqAllOnes && TotalSyms >= 2) {
      var baseCodeSize = System.Numerics.BitOperations.Log2((uint)TotalSyms);
      var numLeft = (TotalSyms - (1 << baseCodeSize)) * 2;
      if (numLeft > TotalSyms)
        numLeft = TotalSyms;
      CodeSizes.AsSpan(0, numLeft).Fill((byte)(baseCodeSize + 1));
      CodeSizes.AsSpan(numLeft).Fill((byte)baseCodeSize);
      maxCodeSize = baseCodeSize + (numLeft != 0 ? 1 : 0);
    }

    if (maxCodeSize == 0) {
      var totalFreq = LzhamHuffman.GenerateCodeSizes(TotalSyms, SymFreq, CodeSizes, ref maxCodeSize);
      if (totalFreq != _totalCount)
        throw new InvalidDataException("LZHAM: Huffman model frequencies out of step");
      if (maxCodeSize > MaxExpectedCodeSize && !LzhamHuffman.LimitMaxCodeSize(TotalSyms, CodeSizes, MaxExpectedCodeSize))
        throw new InvalidDataException("LZHAM: cannot limit Huffman code sizes");
    }

    if (forceUpdateCycle >= 0)
      SymbolsUntilUpdate = _updateCycle = (uint)forceUpdateCycle;
    else {
      _updateCycle = (31u + _updateCycle * Math.Max(32u, _adaptRate != 0 ? _adaptRate : (uint)DefaultAdaptRate)) >> 5;
      if (_updateCycle > _maxCycle)
        _updateCycle = _maxCycle;
      SymbolsUntilUpdate = _updateCycle;
    }

    if (_encoding)
      GenerateCodes();
    else
      GenerateDecoderTables();
  }

  private void GenerateCodes() {
    Span<int> numCodes = stackalloc int[MaxExpectedCodeSize + 1];
    numCodes.Clear();
    foreach (var size in CodeSizes)
      ++numCodes[size];

    Span<uint> nextCode = stackalloc uint[MaxExpectedCodeSize + 1];
    uint code = 0;
    nextCode[0] = 0;
    for (var i = 1; i <= MaxExpectedCodeSize; ++i) {
      nextCode[i] = code;
      code = (code + (uint)numCodes[i]) << 1;
    }

    for (var i = 0; i < TotalSyms; ++i)
      Codes[i] = (ushort)nextCode[CodeSizes[i]]++;
  }

  // generate_decoder_tables, keeping what canonical decoding needs: per code size the largest
  // code (left-justified to 16 bits, plus one) and where its symbols start in sorted order.
  private void GenerateDecoderTables() {
    Span<int> numCodes = stackalloc int[MaxExpectedCodeSize + 1];
    numCodes.Clear();
    foreach (var size in CodeSizes)
      ++numCodes[size];

    Span<int> sortedPositions = stackalloc int[MaxExpectedCodeSize + 1];
    Span<uint> minCodes = stackalloc uint[MaxExpectedCodeSize];
    minCodes.Clear();
    uint nextCode = 0;
    var totalUsed = 0;
    var minCodeSize = int.MaxValue;
    for (var i = 1; i <= MaxExpectedCodeSize; ++i) {
      var n = numCodes[i];
      if (n == 0) {
        _maxCodes[i - 1] = 0;
        _valPtrs[i - 1] = 0;
      } else {
        minCodeSize = Math.Min(minCodeSize, i);
        minCodes[i - 1] = nextCode;
        var maxCode = nextCode + (uint)n - 1;
        _maxCodes[i - 1] = 1 + ((maxCode << (16 - i)) | ((1u << (16 - i)) - 1));
        _valPtrs[i - 1] = totalUsed;
        sortedPositions[i] = totalUsed;
        nextCode += (uint)n;
        totalUsed += n;
      }

      nextCode <<= 1;
    }

    _totalUsedSyms = totalUsed;
    if (_sortedSymbolOrder.Length < totalUsed)
      _sortedSymbolOrder = new ushort[totalUsed];
    for (var i = 0; i < TotalSyms; ++i) {
      var size = CodeSizes[i];
      if (size != 0)
        _sortedSymbolOrder[sortedPositions[size]++] = (ushort)i;
    }

    for (var i = 0; i < MaxExpectedCodeSize; ++i)
      _valPtrs[i] -= (int)minCodes[i];

    _minCodeSize = minCodeSize;
    _maxCodes[MaxExpectedCodeSize] = uint.MaxValue;
    _valPtrs[MaxExpectedCodeSize] = 0xFFFFF;
  }

  /// <summary>Decodes one symbol from the 64-bit, MSB-aligned bit buffer; returns it and its code length.</summary>
  public int DecodeSymbol(ulong bitBuffer, out int length) {
    var k = (uint)(bitBuffer >> 48) + 1;
    var len = _minCodeSize;
    while (k > _maxCodes[len - 1])
      ++len;

    var valPtr = _valPtrs[len - 1] + (int)(bitBuffer >> (64 - len));
    if ((uint)valPtr >= (uint)TotalSyms)
      valPtr = 0;
    if (valPtr >= _totalUsedSyms)
      throw new InvalidDataException("LZHAM: invalid Huffman code");

    length = len;
    return _sortedSymbolOrder[valPtr];
  }
}

/// <summary>Huffman code-size construction (<c>generate_huffman_codes</c>, <c>limit_max_code_size</c>).</summary>
internal static class LzhamHuffman {

  /// <summary>Code sizes for the non-zero frequencies; returns the frequency total.</summary>
  public static uint GenerateCodeSizes(int numSyms, ushort[] freq, byte[] codeSizes, ref int maxCodeSize) {
    var used = new List<(uint Freq, int Symbol)>(numSyms);
    uint total = 0;
    for (var i = 0; i < numSyms; ++i) {
      var f = freq[i];
      if (f == 0)
        codeSizes[i] = 0;
      else {
        total += f;
        used.Add((f, i));
      }
    }

    if (used.Count == 1) {
      codeSizes[used[0].Symbol] = 1;
      return total;
    }

    // radix_sort_syms is a stable sort by ascending frequency.
    var sorted = used.Select((entry, index) => (entry.Freq, entry.Symbol, index))
      .OrderBy(e => e.Freq).ThenBy(e => e.index).ToArray();
    var x = new int[sorted.Length];
    for (var i = 0; i < sorted.Length; ++i)
      x[i] = (int)sorted[i].Freq;

    CalculateMinimumRedundancy(x, x.Length);

    var maxLen = 0;
    for (var i = 0; i < sorted.Length; ++i) {
      maxLen = Math.Max(maxLen, x[i]);
      codeSizes[sorted[i].Symbol] = (byte)x[i];
    }

    maxCodeSize = maxLen;
    return total;
  }

  // calculate_minimum_redundancy (Moffat & Katajainen, 1996): in-place code lengths for
  // frequencies sorted ascending.
  private static void CalculateMinimumRedundancy(int[] a, int n) {
    if (n == 0)
      return;
    if (n == 1) {
      a[0] = 0;
      return;
    }

    a[0] += a[1];
    int root = 0, leaf = 2, next;
    for (next = 1; next < n - 1; ++next) {
      if (leaf >= n || a[root] < a[leaf]) {
        a[next] = a[root];
        a[root++] = next;
      } else
        a[next] = a[leaf++];

      if (leaf >= n || (root < next && a[root] < a[leaf])) {
        a[next] += a[root];
        a[root++] = next;
      } else
        a[next] += a[leaf++];
    }

    a[n - 2] = 0;
    for (next = n - 3; next >= 0; --next)
      a[next] = a[a[next]] + 1;

    int avbl = 1, used = 0, dpth = 0;
    root = n - 2;
    next = n - 1;
    while (avbl > 0) {
      while (root >= 0 && a[root] == dpth) {
        ++used;
        --root;
      }

      while (avbl > used) {
        a[next--] = dpth;
        --avbl;
      }

      avbl = 2 * used;
      ++dpth;
      used = 0;
    }
  }

  /// <summary>The LHArc technique limit_max_code_size uses to cap code lengths.</summary>
  public static bool LimitMaxCodeSize(int numSyms, byte[] codeSizes, int maxCodeSize) {
    const int maxEverCodeSize = 34;
    Span<int> numCodes = stackalloc int[maxEverCodeSize + 1];
    numCodes.Clear();
    var shouldLimit = false;
    for (var i = 0; i < numSyms; ++i) {
      var c = codeSizes[i];
      ++numCodes[c];
      if (c > maxCodeSize)
        shouldLimit = true;
    }

    if (!shouldLimit)
      return true;

    Span<int> nextSortedOfs = stackalloc int[maxEverCodeSize + 1];
    var ofs = 0;
    for (var i = 1; i <= maxEverCodeSize; ++i) {
      nextSortedOfs[i] = ofs;
      ofs += numCodes[i];
    }

    if (ofs < 2 || ofs > 1024)
      return true;
    if (ofs > 1 << maxCodeSize)
      return false;

    for (var i = maxCodeSize + 1; i <= maxEverCodeSize; ++i)
      numCodes[maxCodeSize] += numCodes[i];

    uint total = 0;
    for (var i = maxCodeSize; i != 0; --i)
      total += (uint)numCodes[i] << (maxCodeSize - i);

    if (total == 1u << maxCodeSize)
      return true;

    do {
      --numCodes[maxCodeSize];
      int i;
      for (i = maxCodeSize - 1; i != 0; --i) {
        if (numCodes[i] == 0)
          continue;
        --numCodes[i];
        numCodes[i + 1] += 2;
        break;
      }

      if (i == 0)
        return false;
      --total;
    } while (total != 1u << maxCodeSize);

    var newCodeSizes = new byte[ofs];
    var p = 0;
    for (var i = 1; i <= maxCodeSize; ++i)
      for (var n = numCodes[i]; n > 0; --n)
        newCodeSizes[p++] = (byte)i;

    for (var i = 0; i < numSyms; ++i) {
      var c = codeSizes[i];
      if (c != 0)
        codeSizes[i] = newCodeSizes[nextSortedOfs[c]++];
    }

    return true;
  }
}
