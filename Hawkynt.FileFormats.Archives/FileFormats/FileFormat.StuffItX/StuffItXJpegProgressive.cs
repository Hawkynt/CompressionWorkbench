using T = FileFormat.StuffItX.StuffItXJpegTables;
using static FileFormat.StuffItX.StuffItXJpeg;

namespace FileFormat.StuffItX;

/// <summary>
/// The entropy bits of one progressive scan. The last byte is held back even when complete: the scan's
/// saved final byte replaces it, padding bits and all.
/// </summary>
internal sealed class StuffItXJpegDelayedBits(StuffItXJpegOutput output) {
  private int _value;
  private int _count;

  private void Flush() {
    output.Append(this._value);
    if (this._value == 255) output.Append(0);
    this._value = 0;
    this._count = 0;
  }

  public void Put(int v, int n) {
    if (n is < 0 or > 16 || v < 0 || v >= 1 << n) throw Malformed("progressive entropy bit value");
    var remaining = n;
    while (remaining > 0) {
      if (this._count == 8) this.Flush();
      var take = Math.Min(8 - this._count, remaining);
      remaining -= take;
      this._value |= ((v >> remaining) & ((1 << take) - 1)) << (8 - this._count - take);
      this._count += take;
    }
  }

  public void Symbol(StuffItXJpegHuffman table, int symbol) {
    if (symbol is < 0 or > 255) throw Malformed("progressive Huffman symbol range");
    this.Put(table.Codes[symbol], table.Lengths[symbol]);
  }

  public void Signed(int v) {
    var n = BitLength(Math.Abs(v));
    this.Put(v >= 0 ? v : v + (1 << n) - 1, n);
  }

  public void Finish(int savedByte) {
    this._value = savedByte;
    this.Flush();
  }

  public void Restart(int index) {
    if (this._count < 8) {
      var n = 8 - this._count;
      this.Put((1 << n) - 1, n);
    }
    this.Flush();
    output.Append(StuffItXJpegMarker.Prefix);
    output.Append(StuffItXJpegMarker.Rst0 + index % 8);
  }
}

/// <summary>Huffman-codes the blocks of one progressive scan: DC first and refinement, AC first and refinement with EOB runs.</summary>
internal sealed class StuffItXJpegScanEncoder(StuffItXJpegScan scan, StuffItXJpegTableSet tables, StuffItXJpegOutput output) {
  private readonly byte[] _correction = new byte[1024];
  private readonly int[] _values = new int[64];
  private readonly int[] _local = new int[64];
  private int _correctionCount;
  private int _eob;

  public StuffItXJpegDelayedBits Bits { get; } = new(output);
  public int[] LastDc { get; } = new int[3];
  public int Restarts { get; set; }

  private void FlushEob(StuffItXJpegHuffman table) {
    if (this._eob == 0) return;
    var size = BitLength(this._eob) - 1;
    this.Bits.Symbol(table, size << 4);
    this.Bits.Put(this._eob - (1 << size), size);
    for (var i = 0; i < this._correctionCount; ++i) this.Bits.Put(this._correction[i], 1);
    this._eob = 0;
    this._correctionCount = 0;
  }

  private void Corrections(int count) {
    for (var i = 0; i < count; ++i) this.Bits.Put(this._local[i], 1);
  }

  public void Block(int[] coefficients, int co, int component, int selector) {
    var key = scan.Ss != 0 ? StuffItXJpegTableSet.AcTableKey(selector) : StuffItXJpegTableSet.DcTableKey(selector);
    if (!tables.Huffman.TryGetValue(key, out var table)) throw Malformed("undefined progressive Huffman table");
    if (scan.Ss == 0) {
      if (scan.Ah == 0) {
        var value = coefficients[co] >> scan.Al;
        var difference = value - this.LastDc[component];
        var size = BitLength(Math.Abs(difference));
        if (size > 11) throw Malformed("progressive DC difference range");
        this.Bits.Symbol(table, size);
        this.Bits.Signed(difference);
        this.LastDc[component] = value;
      } else this.Bits.Put((coefficients[co] >> scan.Al) & 1, 1);
      return;
    }
    for (var i = scan.Ss; i <= scan.Se; ++i)
      if (Math.Abs(coefficients[co + T.Zigzag[i]]) > 1023) throw Malformed("progressive AC range");
    if (scan.Ah == 0) this.AcFirst(coefficients, co, table);
    else this.AcRefine(coefficients, co, table);
  }

  private void AcFirst(int[] coefficients, int co, StuffItXJpegHuffman table) {
    var run = 0;
    for (var pos = scan.Ss; pos <= scan.Se; ++pos) {
      var v = coefficients[co + T.Zigzag[pos]];
      var magnitude = Math.Abs(v) >> scan.Al;
      if (magnitude == 0) {
        ++run;
        continue;
      }
      this.FlushEob(table);
      for (; run >= 16; run -= 16) this.Bits.Symbol(table, 240);
      this.Bits.Symbol(table, 16 * run + BitLength(magnitude));
      this.Bits.Signed(v > 0 ? magnitude : -magnitude);
      run = 0;
    }
    if (run != 0 && ++this._eob == 32767) this.FlushEob(table);
  }

  private void AcRefine(int[] coefficients, int co, StuffItXJpegHuffman table) {
    var values = this._values;
    for (var i = 0; i < 64; ++i) values[i] = Math.Abs(coefficients[co + T.Zigzag[i]]) >> scan.Al;
    int lastNew = 0, localCount = 0, run = 0;
    for (var i = scan.Ss; i <= scan.Se; ++i)
      if (values[i] == 1) lastNew = i;
    for (var pos = scan.Ss; pos <= scan.Se; ++pos) {
      var magnitude = values[pos];
      if (magnitude == 0) {
        ++run;
        continue;
      }
      while (run >= 16 && pos <= lastNew) {
        this.FlushEob(table);
        this.Bits.Symbol(table, 240);
        this.Corrections(localCount);
        localCount = 0;
        run -= 16;
      }
      if (magnitude > 1) {
        this._local[localCount++] = magnitude & 1;
        continue;
      }
      this.FlushEob(table);
      this.Bits.Symbol(table, 16 * run + 1);
      this.Bits.Put(coefficients[co + T.Zigzag[pos]] > 0 ? 1 : 0, 1);
      this.Corrections(localCount);
      run = 0;
      localCount = 0;
    }
    if (run == 0 && localCount == 0) return;
    ++this._eob;
    // under 938 corrections stay pending; one block adds at most 63
    for (var i = 0; i < localCount; ++i) this._correction[this._correctionCount + i] = (byte)this._local[i];
    this._correctionCount += localCount;
    if (this._eob == 32767 || this._correctionCount >= 938) this.FlushEob(table);
  }

  public void Finish(int savedByte) {
    if (scan.Ss != 0) {
      if (scan.Components.Length == 0 || !tables.Huffman.TryGetValue(StuffItXJpegTableSet.AcTableKey(scan.Components[^1].Selector), out var table))
        throw Malformed("undefined progressive Huffman table");
      this.FlushEob(table);
    }
    this.Bits.Finish(savedByte);
  }
}

/// <summary>
/// Mode 2 with a progressive (SOF2) frame: every coefficient is decoded and kept, then the scans are
/// Huffman-coded back in their original order and split, each behind the headers that preceded it.
/// </summary>
internal sealed class StuffItXJpegProgressive {
  private sealed record ScanItem(byte[] Header, StuffItXJpegScan Scan, int SavedByte, StuffItXJpegTableSet Tables);

  private readonly StuffItXJpegFrame _frame;
  private readonly StuffItXJpegGeometry _geometry;
  private readonly StuffItXJpegOutput _output;
  private readonly StuffItXJpegRange _decoder;
  private readonly StuffItXJpegHeaderModel _model;
  private readonly List<ScanItem> _scans = [];
  private readonly StuffItXJpegBlocks _blocks;
  private readonly StuffItXJpegTableSet _tables;
  private int _nextScan;
  private bool _reconstructed;

  public bool Done => this._reconstructed && this._nextScan == this._scans.Count;

  public static StuffItXJpegScan ParseScan(byte[] body, int[] ids) {
    if (body.Length == 0 || body[0] is < 1 or > 3 || body.Length != 2 * body[0] + 4) throw Malformed("progressive scan length");
    var components = new (int Id, int Selector)[body[0]];
    for (var i = 0; i < components.Length; ++i) components[i] = (body[1 + 2 * i], body[2 + 2 * i]);
    var members = components.Select(c => c.Id).ToArray();
    if (members.Distinct().Count() != members.Length || !members.All(ids.Contains)) throw Malformed("progressive component references");
    if (!components.All(c => c.Selector >> 4 <= 3 && (c.Selector & 15) <= 3)) throw Malformed("progressive Huffman selector");
    int ss = body[^3], se = body[^2], ah = body[^1] >> 4, al = body[^1] & 15;
    if (ss > se || se > 63 || (ss == 0 && se != 0) || (ss != 0 && members.Length != 1)) throw Malformed("progressive spectral selection");
    if (ah > 13 || al > 13 || (ah != 0 && ah != al + 1)) throw Malformed("progressive successive approximation");
    if (ss == 0 && members.Length != 1) {
      if (!members.Order().SequenceEqual(ids)) throw Malformed("progressive DC scan arrangement");
      components = [.. components.OrderBy(c => c.Id)];
    }
    return new StuffItXJpegScan(components, ss, se, ah, al);
  }

  public StuffItXJpegProgressive(StuffItXJpegPrefix prefix, StuffItXJpegRange decoder, StuffItXJpegHeaderModel model, StuffItXJpegOutput output) {
    if (prefix.Frame is not { } frame) throw Malformed("mode-2 progressive JPEG required");
    if (frame.Marker != StuffItXJpegMarker.Sof2) throw Unsupported("mode-2 progressive JPEG required");
    this._frame = frame;
    this._decoder = decoder;
    this._model = model;
    this._output = output;
    this._geometry = new StuffItXJpegGeometry(frame);
    this._tables = new StuffItXJpegTableSet(prefix);
    this._blocks = new StuffItXJpegBlocks(decoder, this._geometry, keepAll: true);
    this.Headers(prefix);
  }

  /// <summary>Reads every scan header (and the segments between scans) ahead of the coefficients.</summary>
  private void Headers(StuffItXJpegPrefix prefix) {
    var ids = this._geometry.Components.Select(c => c.Id).ToArray();
    var last = prefix.Segments[^1];
    var header = prefix.Header;
    var body = prefix.Scan == null ? null : prefix.Body(last);
    this._output.Append(header.GetRange(0, last.Offset).ToArray());
    var before = prefix.Scan == null ? [] : header.GetRange(last.Offset, header.Count - last.Offset).ToArray();
    var literalBytes = prefix.Literals;
    var markers = 0;
    var progression = Enumerable.Repeat(-1, 3 * 64).ToArray();
    int Get() => this._model.Byte(this._decoder);
    while (body != null) {
      if (this._scans.Count >= 31) throw Malformed("progressive scan count limit");
      var scan = ParseScan(body, ids);
      foreach (var member in scan.Components) {
        var c = Array.IndexOf(ids, member.Id);
        for (var p = scan.Ss; p <= scan.Se; ++p) {
          var prior = progression[c * 64 + p];
          if (literalBytes == 0 && ((scan.Ah == 0 && prior != -1) || (scan.Ah != 0 && prior != scan.Ah))) throw Malformed("inconsistent progressive approximation sequence");
          progression[c * 64 + p] = scan.Al;
        }
      }
      this._scans.Add(new ScanItem(before, scan, Get(), this._tables.Copy()));
      before = [];
      body = null;
      for (;;) {
        var (marker, token) = StuffItXJpegEnvelope.WireToken(Get);
        if (marker is not { } m) {
          this._output.Append(token);
          literalBytes += token.Length;
          continue;
        }
        if (++markers > 4096) throw Malformed("progressive marker count limit");
        if (m is StuffItXJpegMarker.Soi or StuffItXJpegMarker.Eoi) break;
        if (m is StuffItXJpegMarker.StuffedZero or StuffItXJpegMarker.Tem or StuffItXJpegMarker.Sof0 or StuffItXJpegMarker.Sof2 or StuffItXJpegMarker.Fill
            || StuffItXJpegMarker.IsRst(m))
          throw Unsupported("unsupported progressive inter-scan marker");
        int high = Get(), low = Get(), length = high * 256 + low;
        if (length < 2) throw Malformed("progressive header length limit");
        var data = new byte[length - 2];
        for (var i = 0; i < data.Length; ++i) data[i] = (byte)Get();
        byte[] bytes = [StuffItXJpegMarker.Prefix, (byte)m, (byte)high, (byte)low, .. data];
        if (m == StuffItXJpegMarker.Dht) before = bytes;
        else if (m == StuffItXJpegMarker.Sos) before = [.. before, .. bytes];
        else this._output.Append(bytes);
        if (m == StuffItXJpegMarker.Sos) {
          body = data;
          break;
        }
        this._tables.Segment(m, data);
      }
    }
    if (literalBytes == 0)
      for (var c = 0; c < ids.Length; ++c)
        if (progression[c * 64] == -1) throw Malformed("missing progressive DC scan");
  }

  private void Reconstruct() {
    for (var c = 0; c < this._geometry.Components.Length; ++c) {
      var component = this._geometry.Components[c];
      var q = this._tables.ScaledChecked(component.Quantization);
      for (var row = 0; row < this._geometry.Height * component.Vertical; ++row)
        for (var col = 0; col < this._geometry.Width * component.Horizontal; ++col)
          this._blocks.Block(c, row, col, q, 1, this._tables.FrameProfiles[c]);
    }
    this._reconstructed = true;
  }

  public void Step() {
    if (!this._reconstructed) this.Reconstruct();
    if (this._nextScan >= this._scans.Count) return;
    var item = this._scans[this._nextScan];
    var scan = item.Scan;
    var encoder = new StuffItXJpegScanEncoder(scan, item.Tables, this._output);
    this._output.Append(item.Header);
    var ids = this._geometry.Components.Select(c => c.Id).ToArray();
    var store = this._blocks.Store;
    var ordinal = 0;
    void EndUnit() {
      // the restart interval counts base MCUs; a pending EOB run carries across a restart
      ++ordinal;
      var restart = this._tables.Restart;
      if (restart == 0 || ordinal >= this._geometry.Width * this._geometry.Height || ordinal % restart != 0) return;
      encoder.Bits.Restart(encoder.Restarts++);
      Array.Clear(encoder.LastDc);
    }
    if (scan.Components.Length > 1 || scan.Ss == 0) {
      for (var row = 0; row < this._geometry.Height; ++row)
        for (var col = 0; col < this._geometry.Width; ++col) {
          foreach (var member in scan.Components) {
            var c = Array.IndexOf(ids, member.Id);
            var p = this._geometry.Components[c];
            for (var dy = 0; dy < p.Vertical; ++dy)
              for (var dx = 0; dx < p.Horizontal; ++dx)
                encoder.Block(store.Coefficients, store.Full(c, row * p.Vertical + dy, col * p.Horizontal + dx), c, member.Selector);
          }
          EndUnit();
        }
    } else {
      var member = scan.Components[0];
      var c = Array.IndexOf(ids, member.Id);
      StuffItXJpegComponent p = this._geometry.Components[c], first = this._geometry.Components[0];
      var width = (this._frame.Width * p.Horizontal + 8 * first.Horizontal - 1) / (8 * first.Horizontal);
      var height = (this._frame.Height * p.Vertical + 8 * first.Vertical - 1) / (8 * first.Vertical);
      var padded = this._geometry.Width * p.Horizontal;
      for (var flat = 0; flat < width * height; ++flat) {
        encoder.Block(store.Coefficients, store.Full(c, flat / padded, flat % padded), c, member.Selector);
        EndUnit();
      }
    }
    encoder.Finish(item.SavedByte);
    ++this._nextScan;
  }
}
