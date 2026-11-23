using Compression.Core.Dictionary.Lzma;
using static Compression.Core.Entropy.WinZipJpeg.WinZipJpegDecoder;

namespace Compression.Core.Entropy.WinZipJpeg;

/// <summary>
/// Encoder for ZIP compression method 96 (WinZip JPEG), the inverse of <see cref="WinZipJpegDecoder"/>:
/// the JPEG's Huffman-coded scan is decoded to DCT coefficients, which are arithmetic-coded with
/// the same context model; the marker segments around the scan travel as LZMA-packed metadata.
/// </summary>
/// <remarks>
/// <para>
/// Method 96 reproduces a JPEG by re-applying canonical Huffman coding (ZRL runs, EOB after the last
/// non-zero coefficient, one-bit padding before restart markers and at the end of the scan). It
/// carries a single sequential, full-spectrum, interleaved Huffman scan (SOF0/SOF1). Any other
/// JPEG — progressive, arithmetic-coded, lossless, multi-scan, or coded non-canonically — cannot be
/// represented, so <see cref="TryEncode"/> refuses it. Every payload is decoded again and compared
/// with the input before it is returned.
/// </para>
/// <para>
/// The coefficient model and the probability tables are those of the decoder (converted from
/// horsicq/XArchive, MIT). The arithmetic encoder is its inverse: an MPS keeps the code value and
/// adds <c>logP</c> to the log range; an LPS adds the MPS sub-range to the code value and moves the
/// log range by <c>logP + logQP</c>. The code value is kept exactly, so the byte stream needs no
/// carry stuffing beyond the zero byte the decoder skips after two 0xFF bytes.
/// </para>
/// </remarks>
public static class WinZipJpegEncoder {

  /// <summary>Encodes a JPEG as a method-96 stream; false when method 96 cannot represent it.</summary>
  public static bool TryEncode(ReadOnlySpan<byte> jpeg, out byte[] payload) {
    payload = [];
    byte[] candidate;
    try {
      candidate = new Session(jpeg.ToArray()).Run();
    } catch (Exception e) when (e is NotSupportedException or InvalidDataException or IndexOutOfRangeException or ArgumentException) {
      return false;
    }
    byte[] check;
    try {
      check = Decode(candidate);
    } catch (Exception e) when (e is NotSupportedException or InvalidDataException) {
      return false;
    }
    if (!check.AsSpan().SequenceEqual(jpeg)) return false;
    payload = candidate;
    return true;
  }

  /// <summary>Encodes without the final check; throws when the JPEG cannot be represented (diagnostics).</summary>
  internal static byte[] EncodeUnchecked(ReadOnlySpan<byte> jpeg) => new Session(jpeg.ToArray()).Run();

  // ── log-domain arithmetic encoder ────────────────────────────────────

  private sealed class ArithmeticEncoder {
    private long _lr, _lrm;
    private readonly List<byte> _code = [];

    public void Start() {
      this._lr = 0x1001;
      this._lrm = this._lr;
      this._code.Clear();
    }

    /// <summary>Adds Antilog[lr mod 1024] (shifted right by <paramref name="shiftRight"/>) at the weight 2^-(lr/1024 + 9).</summary>
    private void AddAntilog(long lr, int shiftRight) {
      long value = WinZipJpegTables.Antilog[(int)(lr & 0x3FF)] >> shiftRight;
      var bit = (lr >> 10) + 9; // 1-based bit position of the value's least significant bit
      var index = (int)((bit - 1) / 8);
      var shift = 7 - (int)((bit - 1) % 8);
      while (this._code.Count <= index) this._code.Add(0);
      var acc = value << shift;
      for (var i = index; acc != 0; --i) {
        if (i < 0) throw new InvalidOperationException("WinZip JPEG: arithmetic code overflow.");
        acc += this._code[i];
        this._code[i] = (byte)acc;
        acc >>= 8;
      }
    }

    private void UpdateMps(ref Bin bin) {
      if (bin.K <= 5) QSmaller(ref bin);
      bin.K = 0;
      this._lrm = this._lr + NMaxLP[bin.Index];
    }

    private void QBigger(ref Bin bin) {
      if (bin.Index >= 48) return;
      var dlrm = this._lrm - this._lr;
      var saved = 0;
      int max = NMaxLP[bin.Index];
      if (dlrm >= max / 2) {
        dlrm = max - dlrm;
        if (dlrm <= max / 4) Double(ref bin.Index, ref saved);
        Double(ref bin.Index, ref saved);
      } else {
        if (dlrm >= max / 4) Increment(ref bin.Index, ref saved);
        Increment(ref bin.Index, ref saved);
      }
      if (bin.Index <= 0) {
        bin.Index = saved;
        bin.Mps ^= 1;
      }
      this._lrm = this._lr + dlrm;

      static void Increment(ref int index, ref int s) {
        if (index > 0) --index;
        else ++s;
      }

      static void Double(ref int index, ref int s) {
        if (index > 0) index -= DblI[index];
        else s += DblI[index];
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

    public void Encode(ref Bin bin, int bit) {
      this._lrm = this._lr + bin.Dlrm;
      this._lr += LogP[bin.Index];
      if (bit == bin.Mps) {
        if (this._lr >= this._lrm) this.UpdateMps(ref bin);
      } else {
        this.AddAntilog(this._lr, 0); // skip the MPS sub-range
        ++bin.K;
        this.UpdateLps(ref bin);
      }
      bin.Dlrm = (int)(this._lrm - this._lr);
    }

    /// <summary>Ends the segment: a value in the middle of the final range, as many bytes as the decoder reads.</summary>
    public void Finish(Stream output) {
      this.AddAntilog(this._lr, 1);
      var shifts = this._lr / 0x2000; // the decoder shifts until its log range is at most 0x1FFF
      var digits = (int)(2 + shifts);
      while (this._code.Count < digits) this._code.Add(0);
      if (this._code.Count > digits) throw new InvalidOperationException("WinZip JPEG: arithmetic code longer than the segment.");
      byte last = 0, current = 0;
      foreach (var b in this._code) {
        output.WriteByte(b);
        last = current;
        current = b;
        if (last == 0xFF && current == 0xFF) {
          // The decoder treats the byte after 0xFF 0xFF as a carry; ours is always zero.
          output.WriteByte(0);
          last = 0xFF;
          current = 0;
        }
      }
    }
  }

  // ── JPEG parsing and Huffman decoding ────────────────────────────────

  private sealed class Session(byte[] jpeg) {
    private readonly ArithmeticEncoder _ac = new();
    private readonly MemoryStream _out = new();

    private readonly ushort[][] _quant = [new ushort[64], new ushort[64], new ushort[64], new ushort[64]];
    private readonly HuffmanTable?[] _tables = new HuffmanTable?[8];
    private int _width, _height, _componentCount;
    private readonly (int Id, int H, int V, int Quant)[] _components = new (int, int, int, int)[4];
    private int _hMcus, _vMcus;
    private int _scanCount;
    private readonly (int Component, int Dc, int Ac)[] _scan = new (int, int, int)[4];
    private int _restartInterval;

    private sealed class HuffmanTable {
      public readonly Dictionary<(int Length, int Code), byte> Codes = [];
    }

    public byte[] Run() {
      // Properties header: size 4, version 1.0, method 1, no slicing (one slice per scan).
      this._out.Write([4, 0x10, 0x01, 0x00]);

      var soi = jpeg.AsSpan().IndexOf((ReadOnlySpan<byte>)[0xFF, 0xD8]);
      if (soi < 0) throw new NotSupportedException("no SOI marker");
      var (sosEnd, scanCount) = this.ParseHeader(soi);
      if (scanCount != 1) throw new NotSupportedException("one scan only");
      this.WriteBundle(jpeg.AsSpan(0, sosEnd));

      var (blocks, eobs, scanEnd) = this.DecodeScan(sosEnd);

      // The decoder parses up to EOI in the next bundle: the rest of the file (EOI and any trailer).
      var tail = jpeg.AsSpan(scanEnd);
      if (tail.Length < 2 || tail[0] != 0xFF || tail[1] != 0xD9) throw new NotSupportedException("only EOI may follow the scan");

      this.EncodeScan(blocks, eobs);
      this.WriteBundle(tail);
      return this._out.ToArray();
    }

    private void WriteBundle(ReadOnlySpan<byte> metadata) {
      var size = metadata.Length;
      var dictionary = (int)Math.Clamp((size + 511L) & ~511L, 1024, 512 * 1024);
      using var packed = new MemoryStream();
      new LzmaEncoder(Math.Max(4096, dictionary)).Encode(packed, metadata, writeEndMarker: false);
      var p = packed.ToArray();
      if (size < 0xFFFF && p.Length < 0xFFFF)
        this._out.Write([(byte)size, (byte)(size >> 8), (byte)p.Length, (byte)(p.Length >> 8)]);
      else {
        this._out.Write([0xFF, 0xFF, 0xFF, 0xFF]);
        this._out.Write(BitConverter.GetBytes((uint)size));
        this._out.Write(BitConverter.GetBytes((uint)p.Length));
      }
      this._out.Write(p);
    }

    private static int U16(ReadOnlySpan<byte> d, int at) => (d[at] << 8) | d[at + 1];

    /// <summary>Reads markers from SOI to the end of the SOS segment; returns that offset.</summary>
    private (int SosEnd, int Scans) ParseHeader(int at) {
      var d = jpeg.AsSpan();
      var sawFrame = false;
      for (;;) {
        if (d[at] != 0xFF) throw new NotSupportedException("marker expected");
        while (d[at] == 0xFF) ++at;
        var marker = d[at++];
        if (marker == 0xD8) continue;
        if (marker is 0xD9 or (>= 0xD0 and <= 0xD7) or 0x01) throw new NotSupportedException("unexpected marker");
        var size = U16(d, at);
        if (size < 2 || at + size > d.Length) throw new InvalidDataException("bad segment");
        var seg = d.Slice(at, size);
        switch (marker) {
          case 0xC4: {
            var p = 2;
            while (p + 17 <= size) {
              var cls = seg[p] >> 4;
              var index = seg[p] & 0x0F;
              if (cls > 1 || index >= 4) throw new NotSupportedException("bad DHT");
              var table = new HuffmanTable();
              var valueAt = p + 17;
              var code = 0;
              for (var length = 1; length <= 16; ++length) {
                for (var j = 0; j < seg[p + length]; ++j) table.Codes[(length, code++)] = seg[valueAt++];
                code <<= 1;
              }
              this._tables[cls * 4 + index] = table;
              p = valueAt;
            }
            break;
          }
          case 0xDB: {
            var p = 2;
            while (p < size) {
              var precision = seg[p] >> 4;
              var index = seg[p] & 0x0F;
              ++p;
              if (index >= 4 || precision > 1) throw new NotSupportedException("bad DQT");
              for (var i = 0; i < 64; ++i) this._quant[index][i] = (ushort)(precision == 0 ? seg[p + i] : U16(seg, p + 2 * i));
              p += precision == 0 ? 64 : 128;
            }
            break;
          }
          case 0xDD:
            this._restartInterval = U16(seg, 2);
            break;
          case 0xC0:
          case 0xC1: {
            if (sawFrame) throw new NotSupportedException("two frames");
            sawFrame = true;
            if (seg[2] != 8) throw new NotSupportedException("only 8-bit samples");
            this._height = U16(seg, 3);
            this._width = U16(seg, 5);
            this._componentCount = seg[7];
            if (this._componentCount is < 1 or > 4 || this._width == 0 || this._height == 0) throw new NotSupportedException("bad SOF");
            int maxH = 1, maxV = 1;
            for (var i = 0; i < this._componentCount; ++i) {
              var c = 8 + i * 3;
              int h = seg[c + 1] >> 4, v = seg[c + 1] & 0x0F;
              if (h is < 1 or > 4 || v is < 1 or > 4 || seg[c + 2] >= 4) throw new NotSupportedException("bad SOF component");
              this._components[i] = (seg[c], h, v, seg[c + 2]);
              maxH = Math.Max(maxH, h);
              maxV = Math.Max(maxV, v);
            }
            if (this._componentCount == 1) {
              this._components[0] = (this._components[0].Id, 1, 1, this._components[0].Quant);
              maxH = maxV = 1;
            }
            this._hMcus = (this._width + maxH * 8 - 1) / (maxH * 8);
            this._vMcus = (this._height + maxV * 8 - 1) / (maxV * 8);
            break;
          }
          case >= 0xC2 and <= 0xCF:
            throw new NotSupportedException("not a baseline/extended Huffman JPEG");
          case 0xDA: {
            if (!sawFrame) throw new NotSupportedException("scan before frame");
            this._scanCount = seg[2];
            if (this._scanCount != this._componentCount) throw new NotSupportedException("non-interleaved scan");
            for (var i = 0; i < this._scanCount; ++i) {
              var id = seg[3 + i * 2];
              var component = Array.FindIndex(this._components, 0, this._componentCount, c => c.Id == id);
              int dc = seg[4 + i * 2] >> 4, ac = seg[4 + i * 2] & 0x0F;
              if (component < 0 || dc >= 4 || ac >= 4 || this._tables[dc] == null || this._tables[4 + ac] == null)
                throw new NotSupportedException("bad SOS component");
              this._scan[i] = (component, dc, ac);
            }
            var tail = 3 + this._scanCount * 2;
            if (seg[tail] != 0 || seg[tail + 1] != 63 || seg[tail + 2] != 0) throw new NotSupportedException("not a sequential scan");
            // A second scan after this one cannot be carried.
            return (at + size, 1);
          }
        }
        at += size;
      }
    }

    // Scan bit reader over entropy-coded data with 0xFF00 unstuffing and RSTn detection.
    private int _pos;
    private int _bitBuffer, _bitCount;

    private int ReadBit() {
      if (this._bitCount == 0) {
        if (this._pos >= jpeg.Length) throw new InvalidDataException("scan runs past the end");
        var b = jpeg[this._pos];
        if (b == 0xFF) {
          var next = jpeg[this._pos + 1];
          if (next != 0x00) throw new NotSupportedException("marker inside an MCU");
          this._pos += 2;
        } else
          ++this._pos;
        this._bitBuffer = b;
        this._bitCount = 8;
      }
      --this._bitCount;
      return (this._bitBuffer >> this._bitCount) & 1;
    }

    private int ReadBits(int n) {
      var v = 0;
      for (var i = 0; i < n; ++i) v = (v << 1) | this.ReadBit();
      return v;
    }

    private int DecodeHuffman(HuffmanTable table) {
      var code = 0;
      for (var length = 1; length <= 16; ++length) {
        code = (code << 1) | this.ReadBit();
        if (table.Codes.TryGetValue((length, code), out var symbol)) return symbol;
      }
      throw new InvalidDataException("bad Huffman code");
    }

    private static int Extend(int bits, int category) => category == 0 ? 0 : bits < 1 << (category - 1) ? bits - (1 << category) + 1 : bits;

    private (short[][] Blocks, byte[][] Eobs, int ScanEnd) DecodeScan(int start) {
      this._pos = start;
      var blocks = new short[this._scanCount][];
      var eobs = new byte[this._scanCount][];
      for (var c = 0; c < this._scanCount; ++c) {
        var comp = this._components[this._scan[c].Component];
        var count = (long)this._hMcus * this._vMcus * comp.H * comp.V;
        if (count > 1 << 22) throw new NotSupportedException("image too large");
        blocks[c] = new short[count * 64];
        eobs[c] = new byte[count];
      }
      var predictor = new int[4];
      var mcu = 0;
      var restartIndex = 0;
      for (var mcuRow = 0; mcuRow < this._vMcus; ++mcuRow) {
        for (var mcuColumn = 0; mcuColumn < this._hMcus; ++mcuColumn) {
          if (this._restartInterval != 0 && mcu == this._restartInterval) {
            // Padding must be one bits, then RSTn in sequence.
            while (this._bitCount > 0)
              if (this.ReadBit() != 1) throw new NotSupportedException("padding is not all one bits");
            if (jpeg[this._pos] != 0xFF || jpeg[this._pos + 1] != 0xD0 + restartIndex) throw new NotSupportedException("restart marker missing");
            this._pos += 2;
            restartIndex = (restartIndex + 1) & 7;
            mcu = 0;
            Array.Clear(predictor);
          }
          for (var c = 0; c < this._scanCount; ++c) {
            var comp = this._components[this._scan[c].Component];
            var dcTable = this._tables[this._scan[c].Dc]!;
            var acTable = this._tables[4 + this._scan[c].Ac]!;
            var perRow = this._hMcus * comp.H;
            for (var y = 0; y < comp.V; ++y)
              for (var x = 0; x < comp.H; ++x) {
                var index = mcuColumn * comp.H + x + (mcuRow * comp.V + y) * perRow;
                var block = blocks[c].AsSpan(index * 64, 64);
                var category = this.DecodeHuffman(dcTable);
                if (category > 15) throw new InvalidDataException("bad DC category");
                predictor[c] += Extend(this.ReadBits(category), category);
                block[0] = (short)predictor[c];
                var k = 1;
                var eob = 0;
                while (k < 64) {
                  var rs = this.DecodeHuffman(acTable);
                  var run = rs >> 4;
                  var size = rs & 0x0F;
                  if (size == 0) {
                    if (run == 15) {
                      k += 16;
                      continue;
                    }
                    if (run != 0) throw new NotSupportedException("unknown AC symbol");
                    break; // EOB
                  }
                  k += run;
                  if (k > 63) throw new InvalidDataException("AC run past the block");
                  block[k] = (short)Extend(this.ReadBits(size), size);
                  eob = k;
                  ++k;
                }
                eobs[c][index] = (byte)eob;
              }
          }
          ++mcu;
        }
      }
      while (this._bitCount > 0)
        if (this.ReadBit() != 1) throw new NotSupportedException("final padding is not all one bits");
      return (blocks, eobs, this._pos);
    }

    // ── coefficient model (inverse of the decoder's) ──────────────────

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

    private void EncodeBinarization(int value, Bin[] magnitude, int magnitudeBase, Bin[] remainder, int remainderBase, int maxBits, int cap) {
      var ones = value <= 1 ? value : Category((uint)value);
      if (ones > maxBits) throw new NotSupportedException("coefficient too large for method 96");
      for (var i = 0; i < ones; ++i) this._ac.Encode(ref magnitude[magnitudeBase + Math.Min(i, cap - 1)], 1);
      if (ones < maxBits) this._ac.Encode(ref magnitude[magnitudeBase + Math.Min(ones, cap - 1)], 0);
      for (var i = ones - 2; i >= 0; --i) this._ac.Encode(ref remainder[remainderBase + i], (value >> i) & 1);
    }

    private void EncodeAcSign(int comp, int k, int value, ReadOnlySpan<short> c, ReadOnlySpan<short> n, ReadOnlySpan<short> w, ReadOnlySpan<ushort> q) {
      var bit = value < 0 ? 1 : 0;
      int predicted;
      if (FirstRow(k) || FirstColumn(k)) {
        var bdr = Bdr(k, c, n, w, q);
        if (bdr == 0) {
          this._ac.Encode(ref this._fixedBin, bit);
          return;
        }
        predicted = bdr < 0 ? 1 : 0;
      } else if (k == 4) {
        var s = Math.Sign(n[k]) + Math.Sign(w[k]);
        if (s == 0) {
          this._ac.Encode(ref this._fixedBin, bit);
          return;
        }
        predicted = s < 0 ? 1 : 0;
      } else if (RowOf[k] == 1) {
        if (n[k] == 0) {
          this._ac.Encode(ref this._fixedBin, bit);
          return;
        }
        predicted = n[k] < 0 ? 1 : 0;
      } else if (ColumnOf[k] == 1) {
        if (w[k] == 0) {
          this._ac.Encode(ref this._fixedBin, bit);
          return;
        }
        predicted = w[k] < 0 ? 1 : 0;
      } else {
        this._ac.Encode(ref this._fixedBin, bit);
        return;
      }
      var context = Math.Min(Category((uint)Math.Abs(value)) / 2, 2);
      this._ac.Encode(ref this._acSignBins[((comp * 27 + this._signContext[k]) * 3 + context) * 2 + predicted], bit);
    }

    private void EncodeAc(int comp, int k, bool canBeZero, ReadOnlySpan<short> c, ReadOnlySpan<short> n, ReadOnlySpan<short> w, ReadOnlySpan<ushort> q) {
      var val1 = FirstRow(k) || FirstColumn(k) ? Math.Abs(Bdr(k, c, n, w, q)) : Average(k, n, w, q);
      var val2 = Sum(k, c);
      var cat1 = Category((uint)val1);
      var cat2 = Category((uint)val2);
      int value = c[k];
      if (canBeZero) {
        var zero = ((comp * 62 + (k - 1)) * 3 + Math.Min(cat1, 2)) * 6 + Math.Min(cat2, 5);
        this._ac.Encode(ref this._zeroBins[zero], value != 0 ? 1 : 0);
        if (value == 0) return;
      }
      var abs = Math.Abs(value);
      var pivot = ((comp * 63 + (k - 1)) * 5 + Math.Min(cat1, 4)) * 7 + Math.Min(cat2, 6);
      this._ac.Encode(ref this._pivotBins[pivot], abs >= 2 ? 1 : 0);
      if (abs >= 2) {
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
        this.EncodeBinarization(abs - 2, this._acMagnitudeBins, magnitude, this._acRemainderBins, remainder, 14, 9);
      }
      this.EncodeAcSign(comp, k, value, c, n, w, q);
    }

    private void EncodeDc(int comp, ReadOnlySpan<short> c, ReadOnlySpan<short> n, ReadOnlySpan<short> w, bool hasNorth, bool hasWest, ReadOnlySpan<ushort> q) {
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
      var residual = c[0] - predicted;
      var context = Math.Min(Category((uint)Sum(0, c)), 12);
      this.EncodeBinarization(Math.Abs(residual), this._dcMagnitudeBins, (comp * 13 + context) * 10, this._dcRemainderBins, (comp * 13 + context) * 14, 15, 10);
      if (residual == 0) return;
      var northSign = n[0] < predicted ? 1 : 0;
      var westSign = w[0] < predicted ? 1 : 0;
      var predictedSign = predicted < 0 ? 1 : 0;
      this._ac.Encode(ref this._dcSignBins[((comp * 2 + northSign) * 2 + westSign) * 2 + predictedSign], residual < 0 ? 1 : 0);
    }

    private void EncodeBlock(int comp, ReadOnlySpan<short> c, ReadOnlySpan<short> n, ReadOnlySpan<short> w, bool hasNorth, bool hasWest, ReadOnlySpan<ushort> q, int eob) {
      var average = !hasNorth && !hasWest ? 0
        : !hasNorth ? Sum(0, w)
        : !hasWest ? Sum(0, n)
        : (Sum(0, n) + Sum(0, w) + 1) / 2;
      var context = Math.Min(Category((uint)average), 12);
      var bits = 1;
      for (var i = 5; i >= 0; --i) {
        var bit = (eob >> i) & 1;
        this._ac.Encode(ref this._eobBins[(comp * 13 + context) * 63 + bits - 1], bit);
        bits = (bits << 1) | bit;
      }
      for (var k = eob; k >= 1; --k) this.EncodeAc(comp, k, k != eob, c, n, w, q);
      this.EncodeDc(comp, c, n, w, hasNorth, hasWest, q);
    }

    private void EncodeScan(short[][] blocks, byte[][] eobs) {
      foreach (var bins in (Bin[][])[this._eobBins, this._zeroBins, this._pivotBins, this._acMagnitudeBins, this._acRemainderBins,
                 this._acSignBins, this._dcMagnitudeBins, this._dcRemainderBins, this._dcSignBins])
        Array.Fill(bins, Bin.Fresh);
      for (var comp = 0; comp < this._scanCount; ++comp) {
        this._ac.Start();
        var component = this._components[this._scan[comp].Component];
        var perRow = this._hMcus * component.H;
        var quant = this._quant[component.Quant];
        var all = blocks[comp];
        var rows = this._vMcus * component.V;
        for (var y = 0; y < rows; ++y)
          for (var x = 0; x < perRow; ++x) {
            var hasNorth = y != 0;
            var hasWest = x != 0;
            ReadOnlySpan<short> north = hasNorth ? all.AsSpan((x + (y - 1) * perRow) * 64, 64) : ZeroBlock;
            ReadOnlySpan<short> west = hasWest ? all.AsSpan((x - 1 + y * perRow) * 64, 64) : ZeroBlock;
            this.EncodeBlock(comp, all.AsSpan((x + y * perRow) * 64, 64), north, west, hasNorth, hasWest, quant, eobs[comp][x + y * perRow]);
          }
        this._ac.Finish(this._out);
      }
    }
  }
}
