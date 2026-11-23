using Compression.Core.Dictionary.Lzma;

namespace Compression.Core.Entropy.WinZipJpeg;

/// <summary>
/// Decoder for ZIP compression method 96, WinZip's lossless JPEG recompression: LZMA-packed
/// JPEG metadata and arithmetic-coded DCT coefficients, re-entropy-coded into the original
/// JPEG file bit for bit.
/// </summary>
/// <remarks>
/// <para>
/// Converted from horsicq/XArchive <c>Algos/xwinzipjpegdecoder.cpp</c> (MIT; see
/// <c>THIRD-PARTY-NOTICE.XArchive.txt</c>). That decoder follows WinZip's "JPEG Compression —
/// Method 96" specification (2008) and the log-domain binary arithmetic coder of U.S. patent
/// 4,791,403 it incorporates. It carries the corrections to the printed formulas that real
/// WinZip streams require (marked "Erratum" below).
/// </para>
/// <para>
/// The stream is a properties header, then bundles. Each bundle is LZMA-packed JPEG metadata
/// up to a scan, followed by that scan's coefficients in slices; one arithmetic-coder segment
/// covers each component of a slice.
/// </para>
/// </remarks>
public sealed class WinZipJpegDecoder {
  private const int MaxMetadataSize = 16 * 1024 * 1024;
  private const long MaxSliceBlocks = 512L * 1024 * 1024 / (64 * 2);

  // Probability state table (specification section 6; index 48 is the fixed-statistics entry).
  private static ReadOnlySpan<ushort> LogP => [
    1024, 895, 795, 706, 628, 559, 493, 437, 379, 331, 287, 247, 212, 186, 158, 143, 127, 110, 98, 84, 72, 65, 59, 53, 48,
    45, 42, 40, 37, 35, 33, 30, 28, 26, 23, 21, 19, 17, 15, 13, 11, 9, 7, 5, 4, 3, 2, 1, 1024,
  ];
  private static ReadOnlySpan<ushort> LogQP => [
    0, 272, 502, 726, 941, 1150, 1371, 1578, 1819, 2044, 2278, 2521, 2765, 2971, 3227, 3382, 3566, 3788, 3965, 4200, 4435, 4590, 4737, 4899, 5050,
    5147, 5250, 5325, 5441, 5527, 5617, 5758, 5863, 5976, 6157, 6295, 6447, 6616, 6806, 7024, 7278, 7585, 7972, 8495, 8884, 9309, 10065, 11689, 0,
  ];
  private static ReadOnlySpan<ushort> NMaxLP => [
    16384, 16110, 15105, 14826, 14444, 13975, 13804, 13547, 13265, 13240, 12915, 12844, 12720, 12648, 12482, 12441, 12319,
    12320, 12250, 12180, 12168, 12155, 12154, 12084, 12096, 12105, 12096, 12080, 12062, 12075, 12078, 12060, 12068, 12090,
    12075, 12075, 12103, 12121, 12150, 12181, 12221, 12294, 12411, 12615, 13120, 13113, 14574, 21860, 0,
  ];
  private static ReadOnlySpan<byte> HalfI => [
    8, 8, 7, 7, 7, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 8, 9, 10, 10, 10, 10, 10, 10, 9, 9, 8, 8, 7, 7, 6, 6, 6, 5, 5, 4, 4, 3, 3, 3, 3, 2, 2, 1, 0, 0,
  ];
  private static ReadOnlySpan<byte> DblI => [
    0, 1, 2, 3, 4, 5, 6, 7, 8, 7, 7, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 7, 8, 8, 9, 9, 10, 10, 10, 10, 10, 10, 9, 8, 7, 6, 6, 5, 4, 3, 3, 3, 2, 1, 0,
  ];

  // Zigzag geometry (ITU T.81).
  private static ReadOnlySpan<byte> ZigZag => [
    0, 1, 5, 6, 14, 15, 27, 28, 2, 4, 7, 13, 16, 26, 29, 42, 3, 8, 12, 17, 25, 30, 41, 43, 9, 11, 18, 24, 31, 40, 44, 53,
    10, 19, 23, 32, 39, 45, 52, 54, 20, 22, 33, 38, 46, 51, 55, 60, 21, 34, 37, 47, 50, 56, 59, 61, 35, 36, 48, 49, 57, 58, 62, 63,
  ];
  private static ReadOnlySpan<byte> RowOf => [
    0, 0, 1, 2, 1, 0, 0, 1, 2, 3, 4, 3, 2, 1, 0, 0, 1, 2, 3, 4, 5, 6, 5, 4, 3, 2, 1, 0, 0, 1, 2, 3,
    4, 5, 6, 7, 7, 6, 5, 4, 3, 2, 1, 2, 3, 4, 5, 6, 7, 7, 6, 5, 4, 3, 4, 5, 6, 7, 7, 6, 5, 6, 7, 7,
  ];
  private static ReadOnlySpan<byte> ColumnOf => [
    0, 1, 0, 0, 1, 2, 3, 2, 1, 0, 0, 1, 2, 3, 4, 5, 4, 3, 2, 1, 0, 0, 1, 2, 3, 4, 5, 6, 7, 6, 5, 4,
    3, 2, 1, 0, 1, 2, 3, 4, 5, 6, 7, 7, 6, 5, 4, 3, 2, 3, 4, 5, 6, 7, 7, 6, 5, 4, 5, 6, 7, 7, 6, 7,
  ];

  private static readonly short[] ZeroBlock = new short[64];

  // ── input ────────────────────────────────────────────────────────────

  private readonly byte[] _input;
  private int _inputPosition;

  private WinZipJpegDecoder(byte[] input) => this._input = input;

  private int ReadByte() => this._inputPosition < this._input.Length ? this._input[this._inputPosition++] : -1;

  private byte[] ReadBytes(int count) {
    if (count < 0 || this._inputPosition + count > this._input.Length) throw new InvalidDataException("WinZip JPEG: truncated stream.");
    var result = this._input.AsSpan(this._inputPosition, count).ToArray();
    this._inputPosition += count;
    return result;
  }

  // ── arithmetic decoder ───────────────────────────────────────────────

  /// <summary>One adaptive probability bin.</summary>
  private struct Bin {
    public int Index;
    public int Dlrm;
    public byte Mps;
    public byte K;

    public static Bin Fresh => new() { Dlrm = NMaxLP[0] };
    public static Bin Fixed => new() { Index = 48, Dlrm = NMaxLP[0] };
  }

  private bool _endOfData;
  private byte _currentByte, _lastByte;
  private uint _x;
  private int _lr, _lrm, _lx;

  private byte ByteIn() {
    this._lastByte = this._currentByte;
    var b = this.ReadByte();
    if (b < 0) {
      this._endOfData = true;
      this._currentByte = 0;
    } else
      this._currentByte = (byte)b;
    return this._currentByte;
  }

  private static int LogX(uint x) {
    var high = x >> 12;
    if (high == 0) return 0x2000;
    var whole = 0;
    if (high < 512) {
      var log = 0;
      for (var v = high; v > 1; v >>= 1) ++log;
      whole = 8 - log;
    }
    var shift = 8 - whole;
    int negFraction = shift >= 0 ? WinZipJpegTables.Log[(int)((x >> shift) & 0xFFF)] : WinZipJpegTables.Log[(int)((x << -shift) & 0xFFF)];
    return (whole << 10) - negFraction;
  }

  private static uint AntilogX(int lr) {
    var whole = lr >> 10;
    var fraction = lr & 0x3FF;
    var shift = 7 - whole;
    return shift >= 0 ? (uint)WinZipJpegTables.Antilog[fraction] << shift : (uint)WinZipJpegTables.Antilog[fraction] >> -shift;
  }

  private void Renormalize() {
    while (this._lr > 0x1FFF) {
      if (this._currentByte == 0xFF && this._lastByte == 0xFF) this._x += this.ByteIn(); // stuffed carry byte
      this._x = (this._x << 8) | this.ByteIn();
      this._lr -= 0x2000;
      this._lrm -= 0x2000;
    }
    this._lx = LogX(this._x);
  }

  private void StartSegment() {
    this._endOfData = false;
    this._currentByte = this._lastByte = 0;
    var b1 = this.ByteIn();
    var b2 = this.ByteIn();
    this._x = ((uint)b1 << 8) | b2;
    this._lr = 0x1001;
    this._lrm = this._lr;
    this._lx = LogX(this._x);
    if (this._x == 0xFFFF) this.ByteIn(); // stuffed byte
  }

  private void EndSegment() {
    this.Renormalize();
    if (this._currentByte == 0xFF && this._lastByte == 0xFF) this.ByteIn();
  }

  /// <summary>QSMALLER, saturating at index 47 (WinZip's modification).</summary>
  private static void QSmaller(ref Bin bin) {
    if (bin.Index >= 47) return;
    ++bin.Index;
    if (bin.K <= 1) {
      bin.Index += HalfI[bin.Index];
      if (bin.K <= 0) bin.Index += HalfI[bin.Index];
    }
  }

  private void UpdateMps(ref Bin bin) {
    if (bin.K <= 5) QSmaller(ref bin);
    bin.K = 0;
    this._lrm = this._lr + NMaxLP[bin.Index];
    if (this._lrm > 0x7FF) this.Renormalize();
  }

  /// <summary>QBIGGER; the fixed-statistics entry never adapts (WinZip's modification).</summary>
  private void QBigger(ref Bin bin) {
    if (bin.Index >= 48) return;
    var dlrm = this._lrm - this._lr;
    var incrementSaved = 0;
    int max = NMaxLP[bin.Index];
    if (dlrm >= max / 2) {
      dlrm = max - dlrm;
      if (dlrm <= max / 4) DoubleIndex(ref bin.Index, ref incrementSaved);
      DoubleIndex(ref bin.Index, ref incrementSaved);
    } else {
      if (dlrm >= max / 4) IncrementIndex(ref bin.Index, ref incrementSaved);
      IncrementIndex(ref bin.Index, ref incrementSaved);
    }
    if (bin.Index <= 0) {
      bin.Index = incrementSaved;
      bin.Mps ^= 1;
    }
    this._lrm = this._lr + dlrm;

    static void IncrementIndex(ref int index, ref int saved) {
      if (index > 0) --index;
      else ++saved;
    }

    static void DoubleIndex(ref int index, ref int saved) {
      if (index > 0) index -= DblI[index];
      else saved += DblI[index];
    }
  }

  private void UpdateLps(ref Bin bin) {
    this._lr += LogQP[bin.Index];
    this._lrm += LogQP[bin.Index];
    if (bin.K >= 11) {
      this.QBigger(ref bin);
      bin.K = 0;
      this._lrm = this._lr + NMaxLP[bin.Index];
    } else if (this._lrm < this._lr)
      this._lrm = this._lr;
  }

  private int DecodeBit(ref Bin bin) {
    this._lrm = this._lr + bin.Dlrm;
    if (this._lrm > 0x7FF) this.Renormalize();
    this._lr += LogP[bin.Index];
    int bit = bin.Mps;
    var lrt = Math.Min(this._lrm, this._lx);
    if (this._lr >= lrt) {
      if (this._lr < this._lx)
        this.UpdateMps(ref bin);
      else {
        this.Renormalize();
        if (this._lr < this._lx) {
          if (this._lr >= this._lrm) this.UpdateMps(ref bin);
        } else {
          bit ^= 1;
          ++bin.K;
          this._x -= AntilogX(this._lr);
          this._lx = LogX(this._x);
          this.UpdateLps(ref bin);
        }
      }
    }
    bin.Dlrm = this._lrm - this._lr;
    return bit;
  }

  // ── JPEG metadata ────────────────────────────────────────────────────

  private readonly ushort[][] _quant = [new ushort[64], new ushort[64], new ushort[64], new ushort[64]];
  private readonly (ushort Code, byte Length)[][] _huffman = [.. Enumerable.Range(0, 8).Select(_ => new (ushort, byte)[256])]; // [class * 4 + index]
  private int _width, _height, _componentCount;
  private readonly (int Id, int H, int V, int Quant)[] _components = new (int, int, int, int)[4];
  private int _hMcus, _vMcus;
  private int _scanCount;
  private readonly (int Component, int Dc, int Ac)[] _scan = new (int, int, int)[4];
  private int _restartInterval;

  private enum ParseResult { Scan, End }

  private static int U16(ReadOnlySpan<byte> d, int at) => (d[at] << 8) | d[at + 1];

  private static int SegmentSize(ReadOnlySpan<byte> d, int at) {
    if (at + 2 > d.Length) throw new InvalidDataException("WinZip JPEG: truncated marker segment.");
    var size = U16(d, at);
    if (size < 2 || at + size > d.Length) throw new InvalidDataException("WinZip JPEG: bad marker segment size.");
    return size;
  }

  /// <summary>Reads the markers of one bundle's metadata up to SOS or EOI (specification 5.2).</summary>
  private ParseResult ParseMetadata(ReadOnlySpan<byte> d) {
    var at = 0;
    for (;;) {
      if (at >= d.Length || d[at] != 0xFF) throw new InvalidDataException("WinZip JPEG: marker expected.");
      while (d[at] == 0xFF)
        if (++at >= d.Length) throw new InvalidDataException("WinZip JPEG: truncated marker.");
      var marker = d[at++];
      switch (marker) {
        case 0xD8:
          continue;
        case 0xD9:
          return ParseResult.End;
        case 0xC4: {
          var next = at + SegmentSize(d, at);
          var p = at + 2;
          while (p + 17 <= next) {
            var cls = d[p] >> 4;
            var index = d[p] & 0x0F;
            ++p;
            if (cls > 1 || index >= 4) throw new InvalidDataException("WinZip JPEG: bad Huffman table.");
            var total = 0;
            for (var i = 0; i < 16; ++i) total += d[p + i];
            if (p + 16 + total > next) throw new InvalidDataException("WinZip JPEG: bad Huffman table.");
            var table = this._huffman[cls * 4 + index];
            Array.Clear(table);
            var valueAt = p + 16;
            uint code = 0;
            for (var i = 0; i < 16; ++i) {
              for (var j = 0; j < d[p + i]; ++j) table[d[valueAt++]] = ((ushort)code++, (byte)(i + 1));
              code <<= 1;
            }
            p = valueAt;
          }
          at = next;
          break;
        }
        case 0xDB: {
          var next = at + SegmentSize(d, at);
          var p = at + 2;
          while (p + 1 <= next) {
            var precision = d[p] >> 4;
            var index = d[p] & 0x0F;
            ++p;
            if (index >= 4) throw new InvalidDataException("WinZip JPEG: bad quantisation table.");
            var q = this._quant[index];
            if (precision == 0) {
              if (p + 64 > next) throw new InvalidDataException("WinZip JPEG: bad quantisation table.");
              for (var i = 0; i < 64; ++i) q[i] = d[p + i];
              p += 64;
            } else if (precision == 1) {
              if (p + 128 > next) throw new InvalidDataException("WinZip JPEG: bad quantisation table.");
              for (var i = 0; i < 64; ++i) q[i] = (ushort)U16(d, p + 2 * i);
              p += 128;
            } else
              throw new InvalidDataException("WinZip JPEG: bad quantisation table.");
          }
          at = next;
          break;
        }
        case 0xDD: {
          var size = SegmentSize(d, at);
          if (size < 4) throw new InvalidDataException("WinZip JPEG: bad DRI.");
          this._restartInterval = U16(d, at + 2);
          at += size;
          break;
        }
        case 0xC0:
        case 0xC1: {
          var size = SegmentSize(d, at);
          if (size < 8) throw new InvalidDataException("WinZip JPEG: bad SOF.");
          this._height = U16(d, at + 3);
          this._width = U16(d, at + 5);
          this._componentCount = d[at + 7];
          if (this._componentCount is < 1 or > 4 || size < 8 + this._componentCount * 3 || this._width == 0 || this._height == 0)
            throw new InvalidDataException("WinZip JPEG: bad SOF.");
          int maxH = 1, maxV = 1;
          for (var i = 0; i < this._componentCount; ++i) {
            var c = at + 8 + i * 3;
            var h = d[c + 1] >> 4;
            var v = d[c + 1] & 0x0F;
            if (d[c + 2] >= 4 || h is < 1 or > 4 || v is < 1 or > 4) throw new InvalidDataException("WinZip JPEG: bad SOF component.");
            this._components[i] = (d[c], h, v, d[c + 2]);
            maxH = Math.Max(maxH, h);
            maxV = Math.Max(maxV, v);
          }
          // A single-component frame is coded as 1x1 whatever its declared sampling.
          if (this._componentCount == 1) {
            this._components[0] = (this._components[0].Id, 1, 1, this._components[0].Quant);
            maxH = maxV = 1;
          }
          this._hMcus = (this._width + maxH * 8 - 1) / (maxH * 8);
          this._vMcus = (this._height + maxV * 8 - 1) / (maxV * 8);
          at += size;
          break;
        }
        case 0xDA: {
          var size = SegmentSize(d, at);
          if (size < 6) throw new InvalidDataException("WinZip JPEG: bad SOS.");
          this._scanCount = d[at + 2];
          if (this._scanCount is < 1 or > 4 || size < 6 + this._scanCount * 2) throw new InvalidDataException("WinZip JPEG: bad SOS.");
          for (var i = 0; i < this._scanCount; ++i) {
            var id = d[at + 3 + i * 2];
            var component = -1;
            for (var j = 0; j < this._componentCount; ++j)
              if (this._components[j].Id == id) {
                component = j;
                break;
              }
            var tables = d[at + 4 + i * 2];
            if (component < 0 || tables >> 4 >= 4 || (tables & 0x0F) >= 4) throw new InvalidDataException("WinZip JPEG: bad SOS component.");
            this._scan[i] = (component, tables >> 4, tables & 0x0F);
          }
          // Method 96 only carries full-spectrum sequential scans.
          var tail = at + 3 + this._scanCount * 2;
          if (d[tail] != 0 || d[tail + 1] != 63 || d[tail + 2] != 0) throw new NotSupportedException("WinZip JPEG: only sequential full-spectrum scans exist in method 96.");
          return ParseResult.Scan;
        }
        default:
          at += SegmentSize(d, at);
          break;
      }
    }
  }

  // ── coefficient model ────────────────────────────────────────────────

  private readonly Bin[] _eobBins = new Bin[4 * 13 * 63];
  private readonly Bin[] _zeroBins = new Bin[4 * 62 * 3 * 6];
  private readonly Bin[] _pivotBins = new Bin[4 * 63 * 5 * 7];
  private readonly Bin[] _acMagnitudeBins = new Bin[4 * 3 * 9 * 9 * 9];
  private readonly Bin[] _acRemainderBins = new Bin[4 * 3 * 7 * 13];
  private readonly Bin[] _acSignBins = new Bin[4 * 27 * 3 * 2];
  private readonly Bin[] _dcMagnitudeBins = new Bin[4 * 13 * 10];
  private readonly Bin[] _dcRemainderBins = new Bin[4 * 13 * 14];
  private readonly Bin[] _dcSignBins = new Bin[4 * 2 * 2 * 2];
  private Bin _fixedBin = Bin.Fixed;
  private readonly int[] _signContext = SignContexts();

  /// <summary>The 27 positions in the first two rows or columns get an adaptive sign context, numbered in zigzag order.</summary>
  private static int[] SignContexts() {
    var table = new int[64];
    var next = 0;
    for (var k = 1; k < 64; ++k)
      if (RowOf[k] <= 1 || ColumnOf[k] <= 1) table[k] = next++;
    return table;
  }

  private void ResetBins() {
    foreach (var bins in (Bin[][])[this._eobBins, this._zeroBins, this._pivotBins, this._acMagnitudeBins, this._acRemainderBins,
               this._acSignBins, this._dcMagnitudeBins, this._dcRemainderBins, this._dcSignBins])
      Array.Fill(bins, Bin.Fresh);
  }

  /// <summary>CAT (5.6.3): the bit length of the value.</summary>
  private static int Category(uint value) => 32 - System.Numerics.BitOperations.LeadingZeroCount(value);

  private static int At(int row, int column) => ZigZag[row * 8 + column];

  private static bool FirstRow(int k) => RowOf[k] == 0;
  private static bool FirstColumn(int k) => ColumnOf[k] == 0;

  /// <summary>SUM (5.6.2.1): magnitudes below and to the right of k.</summary>
  private static int Sum(int k, ReadOnlySpan<short> block) {
    var sum = 0;
    int row = RowOf[k], column = ColumnOf[k];
    for (var i = 0; i < 64; ++i)
      if (i != k && RowOf[i] >= row && ColumnOf[i] >= column) sum += Math.Abs((int)block[i]);
    return sum;
  }

  /// <summary>AVG (5.6.2.2). Erratum: the summed neighbour term is Bw[x], and DC never takes part.</summary>
  private static int Both(ReadOnlySpan<short> n, ReadOnlySpan<short> w, int i) => Math.Abs((int)n[i]) + Math.Abs((int)w[i]);

  private static int Average(int k, ReadOnlySpan<short> n, ReadOnlySpan<short> w, ReadOnlySpan<ushort> q) {
    if (k is 0 or 1 or 2) return (Both(n, w, k) + 1) / 2;
    if (FirstRow(k)) {
      var left = At(RowOf[k], ColumnOf[k] - 1);
      return (Both(n, w, left) * q[left] / q[k] + Both(n, w, k) + 2) / 4;
    }
    if (FirstColumn(k)) {
      var up = At(RowOf[k] - 1, ColumnOf[k]);
      return (Both(n, w, up) * q[up] / q[k] + Both(n, w, k) + 2) / 4;
    }
    var u = At(RowOf[k] - 1, ColumnOf[k]);
    var l = At(RowOf[k], ColumnOf[k] - 1);
    if (k == 4) return (Both(n, w, u) * q[u] / q[k] + Both(n, w, l) * q[l] / q[k] + Both(n, w, k) + 3) / 6;
    var ul = At(RowOf[k] - 1, ColumnOf[k] - 1);
    return (Both(n, w, u) * q[u] / q[k] + Both(n, w, l) * q[l] / q[k] + Both(n, w, ul) * q[ul] / q[k] + Both(n, w, k) + 4) / 8;
  }

  /// <summary>BDR (5.6.2.3). Erratum: the bracketed term is a sum.</summary>
  private static int Bdr(int k, ReadOnlySpan<short> c, ReadOnlySpan<short> n, ReadOnlySpan<short> w, ReadOnlySpan<ushort> q) {
    if (FirstRow(k)) {
      var down = At(RowOf[k] + 1, ColumnOf[k]);
      return n[k] - (n[down] + c[down]) * q[down] / q[k];
    }
    if (FirstColumn(k)) {
      var right = At(RowOf[k], ColumnOf[k] + 1);
      return w[k] - (w[right] + c[right]) * q[right] / q[k];
    }
    return 0;
  }

  /// <summary>
  /// Generalised Elias-gamma binarisation (5.6.4). Erratum: the unary bins are indexed by the count
  /// of ones so far, capped at <paramref name="cap"/> − 1, and the remainder is coded most significant
  /// bit first with one bin per bit position.
  /// </summary>
  private int DecodeBinarization(Bin[] magnitudeBins, int magnitudeBase, Bin[] remainderBins, int remainderBase, int maxBits, int cap) {
    var ones = 0;
    while (ones < maxBits) {
      if (this.DecodeBit(ref magnitudeBins[magnitudeBase + Math.Min(ones, cap - 1)]) == 1) ++ones;
      else break;
    }
    if (ones <= 1) return ones;
    var bits = ones - 1;
    var value = 1 << bits;
    for (var i = bits - 1; i >= 0; --i) value |= this.DecodeBit(ref remainderBins[remainderBase + i]) << i;
    return value;
  }

  private int DecodeAcSign(int comp, int k, int abs, ReadOnlySpan<short> c, ReadOnlySpan<short> n, ReadOnlySpan<short> w, ReadOnlySpan<ushort> q) {
    int predicted;
    if (FirstRow(k) || FirstColumn(k)) {
      var bdr = Bdr(k, c, n, w, q);
      if (bdr == 0) return this.DecodeBit(ref this._fixedBin);
      predicted = bdr < 0 ? 1 : 0;
    } else if (k == 4) {
      var s = Math.Sign(n[k]) + Math.Sign(w[k]);
      if (s == 0) return this.DecodeBit(ref this._fixedBin);
      predicted = s < 0 ? 1 : 0;
    } else if (RowOf[k] == 1) {
      if (n[k] == 0) return this.DecodeBit(ref this._fixedBin);
      predicted = n[k] < 0 ? 1 : 0;
    } else if (ColumnOf[k] == 1) {
      if (w[k] == 0) return this.DecodeBit(ref this._fixedBin);
      predicted = w[k] < 0 ? 1 : 0;
    } else
      return this.DecodeBit(ref this._fixedBin);

    var context = Math.Min(Category((uint)abs) / 2, 2);
    return this.DecodeBit(ref this._acSignBins[((comp * 27 + this._signContext[k]) * 3 + context) * 2 + predicted]);
  }

  private int DecodeAc(int comp, int k, bool canBeZero, ReadOnlySpan<short> c, ReadOnlySpan<short> n, ReadOnlySpan<short> w, ReadOnlySpan<ushort> q) {
    var val1 = FirstRow(k) || FirstColumn(k) ? Math.Abs(Bdr(k, c, n, w, q)) : Average(k, n, w, q);
    var val2 = Sum(k, c);
    var cat1 = Category((uint)val1);
    var cat2 = Category((uint)val2);

    if (canBeZero) {
      var zero = ((comp * 62 + (k - 1)) * 3 + Math.Min(cat1, 2)) * 6 + Math.Min(cat2, 5);
      if (this.DecodeBit(ref this._zeroBins[zero]) == 0) return 0;
    }

    int abs;
    var pivot = ((comp * 63 + (k - 1)) * 5 + Math.Min(cat1, 4)) * 7 + Math.Min(cat2, 6);
    if (this.DecodeBit(ref this._pivotBins[pivot]) == 0)
      abs = 1;
    else {
      int val3, kind;
      if (FirstRow(k)) {
        val3 = ColumnOf[k] - 1;
        kind = 0;
      } else if (FirstColumn(k)) {
        val3 = RowOf[k] - 1;
        kind = 1;
      } else {
        val3 = Category((uint)(k - 4));
        kind = 2;
      }
      var magnitude = (((comp * 3 + kind) * 9 + Math.Min(cat1, 8)) * 9 + Math.Min(cat2, 8)) * 9;
      var remainder = ((comp * 3 + kind) * 7 + val3) * 13;
      abs = this.DecodeBinarization(this._acMagnitudeBins, magnitude, this._acRemainderBins, remainder, 14, 9) + 2;
    }
    return this.DecodeAcSign(comp, k, abs, c, n, w, q) != 0 ? -abs : abs;
  }

  private static int RoundedPrediction(long t) => (int)((t < 0 ? t - 5000 : t + 5000) / 10000);

  private int DecodeDc(int comp, ReadOnlySpan<short> c, ReadOnlySpan<short> n, ReadOnlySpan<short> w, bool hasNorth, bool hasWest, ReadOnlySpan<ushort> q) {
    // DC prediction (5.6.7.1). Erratum: the neighbour AC terms are added to the current block's.
    int predicted;
    var north = (long)n[0] * 10000 - 11038L * q[2] * (n[2] + c[2]) / q[0];
    var west = (long)w[0] * 10000 - 11038L * q[1] * (w[1] + c[1]) / q[0];
    if (!hasNorth && !hasWest)
      predicted = 0;
    else if (!hasNorth)
      predicted = RoundedPrediction(west);
    else if (!hasWest)
      predicted = RoundedPrediction(north);
    else {
      var p0 = RoundedPrediction(north);
      var p1 = RoundedPrediction(west);
      // Refinement (5.6.7.2). Erratum: plain differences, without inner absolute values.
      int d0 = 0, d1 = 0;
      for (var i = 1; i < 8; ++i) {
        d0 += Math.Abs(n[At(i, 0)] - c[At(i, 0)]);
        d1 += Math.Abs(w[At(0, i)] - c[At(0, i)]);
      }
      if (d0 > d1) {
        var weight = 1L << Math.Min(d0 - d1, 31);
        predicted = (int)((weight * p1 + p0) / (1 + weight));
      } else {
        var weight = 1L << Math.Min(d1 - d0, 31);
        predicted = (int)((weight * p0 + p1) / (1 + weight));
      }
    }

    var context = Math.Min(Category((uint)Sum(0, c)), 12);
    var abs = this.DecodeBinarization(this._dcMagnitudeBins, (comp * 13 + context) * 10, this._dcRemainderBins, (comp * 13 + context) * 14, 15, 10);
    if (abs == 0) return predicted;

    // Sign (5.6.7.3.2). Erratum: neighbour DCs are compared with the prediction, not with zero.
    var northSign = n[0] < predicted ? 1 : 0;
    var westSign = w[0] < predicted ? 1 : 0;
    var predictedSign = predicted < 0 ? 1 : 0;
    var sign = this.DecodeBit(ref this._dcSignBins[((comp * 2 + northSign) * 2 + westSign) * 2 + predictedSign]);
    return sign != 0 ? predicted - abs : predicted + abs;
  }

  private void DecodeBlock(int comp, Span<short> c, ReadOnlySpan<short> n, ReadOnlySpan<short> w, bool hasNorth, bool hasWest, ReadOnlySpan<ushort> q, ref byte eob) {
    // EOB context (5.6.5.2).
    var average = !hasNorth && !hasWest ? 0
      : !hasNorth ? Sum(0, w)
      : !hasWest ? Sum(0, n)
      : (Sum(0, n) + Sum(0, w) + 1) / 2;
    var context = Math.Min(Category((uint)average), 12);

    // Binary-tree EOB (5.6.5.1): six bits, bins indexed by the partial bit string.
    var bits = 1;
    for (var i = 0; i < 6; ++i) bits = (bits << 1) | this.DecodeBit(ref this._eobBins[(comp * 13 + context) * 63 + bits - 1]);
    var last = bits & 0x3F;
    eob = (byte)last;
    c[(last + 1)..].Clear();
    for (var k = last; k >= 1; --k) c[k] = (short)this.DecodeAc(comp, k, k != last, c, n, w, q);
    c[0] = (short)this.DecodeDc(comp, c, n, w, hasNorth, hasWest, q);
  }

  // ── Huffman re-encoding ──────────────────────────────────────────────

  private readonly MemoryStream _output = new();
  private ulong _bitBuffer;
  private int _bitCount;
  private readonly int[] _dcPredictor = new int[4];
  private int _mcuCounter, _restartIndex;

  private void EmitScanByte(byte b) {
    this._output.WriteByte(b);
    if (b == 0xFF) this._output.WriteByte(0x00);
  }

  private void PushBits(uint bits, int length) {
    if (length <= 0) return;
    this._bitBuffer |= (ulong)bits << (64 - this._bitCount - length);
    this._bitCount += length;
    while (this._bitCount >= 8) {
      this.EmitScanByte((byte)(this._bitBuffer >> 56));
      this._bitBuffer <<= 8;
      this._bitCount -= 8;
    }
  }

  private void PushCode((ushort Code, byte Length)[] table, int symbol) {
    var (code, length) = table[symbol];
    if (length == 0) throw new InvalidDataException("WinZip JPEG: a coefficient has no Huffman code in its table.");
    this.PushBits(code, length);
  }

  private void PushValue((ushort Code, byte Length)[] table, int value, int run) {
    int category;
    uint bits;
    if (value >= 0) {
      category = Category((uint)value);
      bits = (uint)value & ((1u << category) - 1);
    } else {
      category = Category((uint)-value);
      bits = ((uint)value & ((1u << category) - 1)) - 1;
    }
    if (category > 15) throw new InvalidDataException("WinZip JPEG: coefficient out of JPEG range.");
    this.PushCode(table, category | (run << 4));
    this.PushBits(bits, category);
  }

  private void EncodeSlice(short[][] blocks, byte[][] eobs, int rows, bool last) {
    for (var mcuRow = 0; mcuRow < rows; ++mcuRow) {
      for (var mcuColumn = 0; mcuColumn < this._hMcus; ++mcuColumn) {
        if (this._restartInterval != 0 && this._mcuCounter == this._restartInterval) {
          if (this._bitCount != 0) {
            var pad = 8 - this._bitCount;
            this.PushBits((1u << pad) - 1, pad);
          }
          this._output.WriteByte(0xFF);
          this._output.WriteByte((byte)(0xD0 + this._restartIndex));
          this._restartIndex = (this._restartIndex + 1) & 7;
          this._mcuCounter = 0;
          Array.Clear(this._dcPredictor);
        }

        for (var comp = 0; comp < this._scanCount; ++comp) {
          var component = this._components[this._scan[comp].Component];
          var dcTable = this._huffman[this._scan[comp].Dc];
          var acTable = this._huffman[4 + this._scan[comp].Ac];
          var perRow = this._hMcus * component.H;
          for (var y = 0; y < component.V; ++y)
            for (var x = 0; x < component.H; ++x) {
              var index = mcuColumn * component.H + x + (mcuRow * component.V + y) * perRow;
              var block = blocks[comp].AsSpan(index * 64, 64);
              var eob = eobs[comp][index];

              this.PushValue(dcTable, block[0] - this._dcPredictor[comp], 0);
              this._dcPredictor[comp] = block[0];

              var coefficient = 1;
              while (coefficient <= eob) {
                var first = coefficient;
                var runEnd = coefficient + 15;
                while (coefficient < 63 && coefficient < runEnd && block[coefficient] == 0) ++coefficient;
                this.PushValue(acTable, block[coefficient], coefficient - first);
                ++coefficient;
              }
              if (eob != 63) this.PushCode(acTable, 0x00);
            }
        }
        ++this._mcuCounter;
      }
    }

    if (last) {
      // End of scan: pad the final byte with one bits (T.81 F.1.2.3).
      var pad = -this._bitCount & 7;
      if (pad != 0) this.PushBits((1u << pad) - 1, pad);
    }
  }

  // ── stream ───────────────────────────────────────────────────────────

  /// <summary>Decodes a method-96 stream back to the original JPEG file.</summary>
  /// <exception cref="InvalidDataException">The stream is corrupt.</exception>
  /// <exception cref="NotSupportedException">A properties header version or feature method 96 does not define.</exception>
  public static byte[] Decode(ReadOnlySpan<byte> data) {
    try {
      return new WinZipJpegDecoder(data.ToArray()).Run();
    } catch (DivideByZeroException e) {
      throw new InvalidDataException("WinZip JPEG: a quantisation table holds a zero.", e);
    } catch (IndexOutOfRangeException e) {
      throw new InvalidDataException("WinZip JPEG: corrupt stream.", e);
    }
  }

  private byte[] Run() {
    // Properties header (3.2): size >= 4, version 1.0, method 1, slice value in the options' low bits.
    var header = this.ReadBytes(4);
    if (header[0] < 4 || header[1] != 0x10 || header[2] != 0x01 || (header[3] & 0xE0) != 0)
      throw new NotSupportedException("WinZip JPEG: unknown properties header.");
    if (header[0] > 4) this.ReadBytes(header[0] - 4);
    var sliceValue = header[3] & 0x1F;

    var firstBundle = true;
    for (;;) {
      // Bundle header (4.1.1): 16-bit sizes, or 0xFFFF 0xFFFF followed by 32-bit ones.
      var bundle = this.ReadBytes(4);
      long plainSize = bundle[0] | (bundle[1] << 8);
      long packedSize = bundle[2] | (bundle[3] << 8);
      if (plainSize == 0xFFFF && packedSize == 0xFFFF) {
        var wide = this.ReadBytes(8);
        plainSize = BitConverter.ToUInt32(wide, 0);
        packedSize = BitConverter.ToUInt32(wide, 4);
      }
      if (plainSize is <= 0 or > MaxMetadataSize || packedSize is < 0 or > MaxMetadataSize)
        throw new InvalidDataException("WinZip JPEG: bad bundle sizes.");

      // Bundle metadata (4.1.2): raw LZMA with lc=3 lp=0 pb=2 and a dictionary of the size rounded
      // up to 512 bytes, clamped to 1-512 KiB; stored when the packed size is zero.
      byte[] metadata;
      if (packedSize != 0) {
        var packed = this.ReadBytes((int)packedSize);
        var dictionary = (int)Math.Clamp((plainSize + 511) & ~511L, 1024, 512 * 1024);
        try {
          metadata = new LzmaDecoder(new MemoryStream(packed), 3, 0, 2, dictionary, plainSize).Decode();
        } catch (Exception e) when (e is not InvalidDataException) {
          throw new InvalidDataException("WinZip JPEG: bad LZMA metadata.", e);
        }
        if (metadata.Length != plainSize) throw new InvalidDataException("WinZip JPEG: bad LZMA metadata.");
      } else
        metadata = this.ReadBytes((int)plainSize);
      this._output.Write(metadata);

      // Anything before the first SOI is opaque leading data (5.1).
      var parseFrom = 0;
      if (firstBundle) {
        parseFrom = metadata.AsSpan().IndexOf((ReadOnlySpan<byte>)[0xFF, 0xD8]);
        if (parseFrom < 0) throw new InvalidDataException("WinZip JPEG: no SOI marker.");
        firstBundle = false;
      }
      if (this.ParseMetadata(metadata.AsSpan(parseFrom)) == ParseResult.End) break;
      if (this._hMcus <= 0 || this._vMcus <= 0) throw new InvalidDataException("WinZip JPEG: scan before frame header.");
      this.DecodeScan(sliceValue);
    }
    return this._output.ToArray();
  }

  private void DecodeScan(int sliceValue) {
    this.ResetBins();
    int sliceHeight;
    if (sliceValue != 0) {
      var perSlice = Math.Max(1L, (1L << (sliceValue + 6)) / this._hMcus);
      var slices = (this._vMcus + perSlice - 1) / perSlice;
      sliceHeight = (int)((this._vMcus + slices - 1) / slices);
    } else
      sliceHeight = this._vMcus;

    var blocks = new short[this._scanCount][];
    var eobs = new byte[this._scanCount][];
    for (var comp = 0; comp < this._scanCount; ++comp) {
      var component = this._components[this._scan[comp].Component];
      var count = (long)this._hMcus * sliceHeight * component.H * component.V;
      if (count is <= 0 || count > MaxSliceBlocks) throw new InvalidDataException("WinZip JPEG: image too large.");
      blocks[comp] = new short[count * 64];
      eobs[comp] = new byte[count];
    }

    this._bitBuffer = 0;
    this._bitCount = 0;
    Array.Clear(this._dcPredictor);
    this._mcuCounter = 0;
    this._restartIndex = 0;

    for (var finished = 0; finished < this._vMcus;) {
      var rows = Math.Min(sliceHeight, this._vMcus - finished);
      var last = finished + rows >= this._vMcus;

      for (var comp = 0; comp < this._scanCount; ++comp) {
        this.StartSegment();
        var component = this._components[this._scan[comp].Component];
        var perRow = this._hMcus * component.H;
        var quant = this._quant[component.Quant];
        var all = blocks[comp];
        var blockRows = rows * component.V;
        for (var y = 0; y < blockRows; ++y) {
          for (var x = 0; x < perRow; ++x) {
            var current = all.AsSpan((x + y * perRow) * 64, 64);
            // The block above comes from the previous slice's last row when this slice starts.
            var northRow = y != 0 ? y - 1 : finished != 0 ? sliceHeight * component.V - 1 : -1;
            var hasNorth = northRow >= 0;
            var hasWest = x != 0;
            ReadOnlySpan<short> north = hasNorth ? all.AsSpan((x + northRow * perRow) * 64, 64) : ZeroBlock;
            ReadOnlySpan<short> west = hasWest ? all.AsSpan((x - 1 + y * perRow) * 64, 64) : ZeroBlock;
            this.DecodeBlock(comp, current, north, west, hasNorth, hasWest, quant, ref eobs[comp][x + y * perRow]);
            if (this._endOfData) throw new InvalidDataException("WinZip JPEG: coefficient data ends early.");
          }
        }
        this.EndSegment();
      }

      this.EncodeSlice(blocks, eobs, rows, last);
      finished += rows;
    }
  }
}
