using static FileFormat.StuffItX.StuffItXJpeg;

namespace FileFormat.StuffItX;

/// <summary>
/// The mode-1 coefficient model: three-component baseline images, coefficients coded under integer
/// frequency tables with a two-row block cache, where a block may also be a copy of its left or upper
/// neighbour.
/// </summary>
/// <remarks>Converted from KaitoKit's <c>StuffItXJPEGMode1.swift</c> (MIT, see THIRD-PARTY-NOTICE.KaitoKit.txt).</remarks>
internal sealed class StuffItXJpegMode1Blocks {
  private const int CopyLeft = 65553;
  private const int CopyUp = 65554;

  private readonly int[] _dcRows = new int[2 * 81 * 19];
  private readonly int[] _acRows = new int[2 * 2404 * 25];
  private readonly int[] _signRows = Enumerable.Repeat(1, 2 * 1377 * 2).ToArray();
  private readonly int[] _frequencies = new int[25];
  private readonly (int Width, int Height, int Ring)[] _layout = new (int, int, int)[3];
  private readonly int[] _metrics;
  private readonly int[] _tags;
  private readonly int[] _scratch = new int[64];
  private readonly int _maxBlocks;
  private int _decoded;

  public StuffItXJpegRange Decoder { get; }

  /// <summary>The two-row block cache; <see cref="Block"/> returns offsets into it.</summary>
  public int[] Cache { get; }

  public StuffItXJpegMode1Blocks(StuffItXJpegRange decoder, StuffItXJpegGeometry geometry) {
    this.Decoder = decoder;
    this._maxBlocks = geometry.Blocks;
    var ring = 0;
    for (var c = 0; c < 3; ++c) {
      var p = geometry.Components[c];
      int width = geometry.Width * p.Horizontal, height = geometry.Height * p.Vertical;
      this._layout[c] = (width, height, ring);
      ring += width * 2;
    }
    this.Cache = new int[ring * 64];
    this._metrics = new int[ring];
    this._tags = Enumerable.Repeat(-1, ring).ToArray();
    for (var c = 0; c < 2; ++c) {
      for (var i = 64; i <= 80; ++i)
        for (var s = 0; s < 19; ++s) this._dcRows[(c * 81 + i) * 19 + s] = 1;
      for (var i = 2368; i < 2404; ++i)
        for (var s = 0; s < 25; ++s) this._acRows[(c * 2404 + i) * 25 + s] = s <= 1 || s >= 19 ? 50 : 20;
    }
  }

  private static void Update(int[] rows, int row, int width, int s, int inc, int threshold) {
    rows[row + s] += inc;
    var sum = 0;
    for (var i = 0; i < width; ++i) sum += rows[row + i];
    if (sum < threshold) return;
    for (var i = 0; i < width; ++i) rows[row + i] = (rows[row + i] + 1) / 2;
  }

  private int DcRow(int c, int i) => c is >= 0 and <= 1 && i is >= 0 and <= 80 ? (c * 81 + i) * 19 : throw Malformed("mode-1 DC context range");

  private int AcRow(int c, int i) => c is >= 0 and <= 1 && i is >= 0 and < 2404 ? (c * 2404 + i) * 25 : throw Malformed("mode-1 AC context range");

  private int Dc(int c, int context) {
    int a = this.DcRow(c, context), b = this.DcRow(c, 80);
    var f = this._frequencies;
    for (var i = 0; i < 19; ++i) f[i] = 4 * this._dcRows[a + i] + this._dcRows[b + i];
    var s = this.Decoder.Value(f.AsSpan(0, 19));
    Update(this._dcRows, a, 19, s, 32, 4096);
    Update(this._dcRows, b, 19, s, 1024, 4096);
    if (s == 0) return 0;
    if (s >= 17) return 65536 + s;
    var value = (1 << (s - 1)) | this.Decoder.Bits(s - 1);
    return this.Decoder.Bit() != 0 ? value : -value;
  }

  private int Ac(int c, int context, int pos) {
    int a = this.AcRow(c, 36 * pos + context), b = this.AcRow(c, 2304 + pos), d = this.AcRow(c, 2368 + context);
    var f = this._frequencies;
    for (var i = 0; i < 25; ++i) f[i] = 16 * this._acRows[a + i] + this._acRows[b + i] + this._acRows[d + i];
    var s = this.Decoder.Value(f.AsSpan(0, 25));
    Update(this._acRows, a, 25, s, 64, 8193);
    Update(this._acRows, b, 25, s, 1024, 8193);
    Update(this._acRows, d, 25, s, 64, 8193);
    return s;
  }

  private int Sign(int c, int index) {
    if (c is < 0 or > 1 || index is < 0 or >= 1377) throw Malformed("mode-1 sign context range");
    var row = (c * 1377 + index) * 2;
    var s = this.Decoder.Value(this._signRows.AsSpan(row, 2));
    if (++this._signRows[row + s] >= 64) {
      this._signRows[row] = (this._signRows[row] + 1) / 2;
      this._signRows[row + 1] = (this._signRows[row + 1] + 1) / 2;
    }
    return s;
  }

  /// <summary>Decodes one block into the cache; returns its offset and the DC of the block its slot held before.</summary>
  /// <param name="upperLeftOverride">The upper-left DC to predict from, when 4:2:0 luma has already reused that slot.</param>
  public (int Co, int OldDc) Block(int c, int row, int col, int? upperLeftOverride = null) {
    if (c is < 0 or > 2 || this._decoded >= this._maxBlocks) throw Malformed("mode-1 coefficient coordinates or block limit");
    var l = this._layout[c];
    if (row < 0 || row >= l.Height || col < 0 || col >= l.Width) throw Malformed("mode-1 coefficient coordinates or block limit");
    int index = l.Ring + (row & 1) * l.Width + col, upper = l.Ring + ((row - 1) & 1) * l.Width + col;
    if (this._tags[index] == row) throw Malformed("duplicate mode-1 block");
    var cache = this.Cache;
    var left = col > 0 ? (index - 1) * 64 : -1;
    var up = row > 0 ? upper * 64 : -1;
    var ul = row > 0 && col > 0 ? (upper - 1) * 64 : -1;
    if ((col != 0 && this._tags[index - 1] < 0) || (row != 0 && this._tags[upper] < 0) || (row != 0 && col != 0 && this._tags[upper - 1] < 0))
      throw Malformed("missing mode-1 coefficient neighbor");
    int Metric(int i) {
      var n = this._metrics[i];
      return n < 2 ? n : 1 + BitLength(n - 1);
    }
    var context = 8 * (col > 0 ? Metric(index - 1) : 0) + (row > 0 ? Metric(upper) : 0);
    var cc = c == 0 ? 0 : 1;
    var dc = this.Dc(cc, context);
    var oldDc = cache[index * 64];
    var co = this._scratch;
    Array.Clear(co);
    if (dc == CopyLeft) {
      if (left < 0) throw Malformed("mode-1 left copy without neighbor");
      Array.Copy(cache, left, co, 0, 64);
    } else if (dc == CopyUp) {
      if (up < 0) throw Malformed("mode-1 upper copy without neighbor");
      Array.Copy(cache, up, co, 0, 64);
    } else {
      int pred;
      if (left >= 0 && up >= 0 && ul >= 0) pred = (3 * (cache[left] + cache[up]) - 2 * (upperLeftOverride ?? cache[ul])) >> 2;
      else if (up >= 0) pred = cache[up];
      else if (left >= 0) pred = cache[left];
      else pred = 0;
      co[0] = I16(dc + pred);
      for (var pos = 1; pos < 64;) {
        int y = pos / 8, x = pos & 7;
        if (y == 0) context = (up >= 0 ? Cat6(cache[up + pos]) : 0) + 6 * Cat6(co[pos - 1]);
        else if (x == 0) context = Cat3(co[pos - 8]) + (left >= 0 ? 3 * Cat4(cache[left + pos]) : 0) + 12 * Cat3(co[pos - 7]);
        else context = Cat3(co[pos - 8]) + 3 * Cat3(co[pos - 1]) + (x <= 5 && pos <= 47 ? 9 * Cat3(co[pos - 7]) : 0);
        var symbol = this.Ac(cc, context, pos);
        if (symbol is >= 19 and <= 21) {
          co[pos] = symbol - 20;
          break;
        }
        if (symbol is >= 22 and <= 24) {
          co[pos] = symbol - 23;
          pos = (pos | 7) + 1;
          continue;
        }
        var magnitude = symbol < 4 ? symbol : I16(2 + ((1 << (symbol - 3)) | this.Decoder.Bits(symbol - 3, little: true)));
        if (magnitude != 0) {
          context = (pos >= 16 ? SignClass(co[pos - 16]) : 0) + (x >= 2 ? 3 * SignClass(co[pos - 2]) : 0);
          if (up >= 0 && pos < 16) context += SignClass(cache[up + pos]);
          if (left >= 0 && x <= 1) context += 3 * SignClass(cache[left + pos]);
          if (up >= 0 && y == 1) context += 9 * SignClass(cache[up + pos - 8]);
          if (left >= 0 && x == 1) context += 27 * SignClass(cache[left + pos - 1]);
          context += 81 * Math.Min(y, 3) + 324 * Math.Min(x, 3);
          co[pos] = this.Sign(cc, context) != 0 ? magnitude : -magnitude;
        }
        ++pos;
      }
    }
    var metric = 1;
    for (var i = 0; i < 64; ++i)
      if (co[i] != 0) metric = i + 1;
    if (metric >= 2 && Math.Abs(co[metric - 1]) <= 1) --metric;
    Array.Copy(co, 0, cache, index * 64, 64);
    this._metrics[index] = metric;
    this._tags[index] = row;
    ++this._decoded;
    return (index * 64, oldDc);
  }
}

/// <summary>Mode 1: a three-component baseline image, decoded an MCU row at a time and Huffman-coded back.</summary>
internal sealed class StuffItXJpegMode1 {
  private readonly StuffItXJpegGeometry _geometry;
  private readonly (int Hs, int Vs, StuffItXJpegHuffman Dc, StuffItXJpegHuffman Ac)[] _components = new (int, int, StuffItXJpegHuffman, StuffItXJpegHuffman)[3];
  private readonly int[] _previousDc = new int[3];
  private readonly StuffItXJpegMode1Blocks _blocks;
  private readonly StuffItXJpegEntropyWriter _entropy;
  private readonly int _restart;
  private int _row;
  private int _restarts;
  private int _savedUpperLeft;

  public bool Done => this._row == this._geometry.Height;

  public StuffItXJpegMode1(StuffItXJpegPrefix prefix, StuffItXJpegRange decoder, StuffItXJpegOutput output) {
    if (prefix.Frame is not { } frame || prefix.Scan is not { } scan) throw Malformed("mode-1 baseline JPEG required");
    if (frame.Marker != StuffItXJpegMarker.Sof0) throw Unsupported("mode-1 baseline JPEG required");
    var components = StuffItXJpegGeometry.ComponentsById(frame);
    var ids = components.Select(c => c.Id).ToArray();
    if (!(ids is [0, 1, 2] or [1, 2, 3]) || !scan.Components.Select(s => s.Id).Order().SequenceEqual(ids))
      throw Unsupported("mode-1 measured three-component interleaved profile required");
    if (scan.Ss != 0 || scan.Se != 63 || scan.Ah != 0 || scan.Al != 0) throw Malformed("unsupported mode-1 scan");
    this._geometry = new StuffItXJpegGeometry(frame);
    var tables = new StuffItXJpegTableSet(prefix);
    this._restart = tables.Restart;
    for (var c = 0; c < 3; ++c) {
      var p = components[c];
      var selector = scan.Components.First(s => s.Id == p.Id).Selector;
      if (!tables.Huffman.TryGetValue(StuffItXJpegTableSet.DcTableKey(selector), out var dc)
          || !tables.Huffman.TryGetValue(StuffItXJpegTableSet.AcTableKey(selector), out var ac))
        throw Malformed("undefined mode-1 Huffman table");
      this._components[c] = (p.Horizontal, p.Vertical, dc, ac);
    }
    this._blocks = new StuffItXJpegMode1Blocks(decoder, this._geometry);
    this._entropy = new StuffItXJpegEntropyWriter(output);
  }

  public void Step() {
    if (this.Done) return;
    var cache = this._blocks.Cache;
    for (var column = 0; column < this._geometry.Width; ++column) {
      for (var c = 0; c < 3; ++c) {
        var p = this._components[c];
        for (var dy = 0; dy < p.Vs; ++dy)
          for (var dx = 0; dx < p.Hs; ++dx) {
            var save = c == 0 && p.Hs == 2 && p.Vs == 2;
            int? upperLeft = save && dx == 0 && dy == 0 ? this._savedUpperLeft : null;
            var (co, replaced) = this._blocks.Block(c, this._row * p.Vs + dy, column * p.Hs + dx, upperLeft);
            if (save && dx == 1 && dy == 1) this._savedUpperLeft = replaced;
            this._entropy.Block(cache, co, this._previousDc[c], p.Dc, p.Ac);
            this._previousDc[c] = cache[co];
          }
      }
      var unit = this._row * this._geometry.Width + column + 1;
      if (this._restart != 0 && unit < this._geometry.Width * this._geometry.Height && unit % this._restart == 0) {
        this._entropy.Finish();
        this._entropy.Output.Append(StuffItXJpegMarker.Prefix);
        this._entropy.Output.Append(StuffItXJpegMarker.Rst0 + this._restarts % 8);
        ++this._restarts;
        Array.Clear(this._previousDc);
      }
    }
    ++this._row;
    if (this.Done) this._entropy.Finish(this._blocks.Decoder.Bit);
  }
}
