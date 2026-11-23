using T = FileFormat.StuffItX.StuffItXJpegTables;
using static FileFormat.StuffItX.StuffItXJpeg;

namespace FileFormat.StuffItX;

/// <summary>Four rows of a model table that are mixed into one distribution, and their width.</summary>
internal readonly record struct StuffItXJpegKeys(int A, int B, int C, int D, int Width, bool Sign = false);

/// <summary>
/// The mode-2 coefficient model: byte-sized adaptive statistics for the DC magnitude, the horizontal
/// and vertical extent of a block's nonzero region, each AC magnitude and each sign, mixed from four
/// contexts with fixed weights over a base distribution.
/// </summary>
/// <remarks>Converted from KaitoKit's <c>StuffItXJPEGModels.swift</c> (MIT, see THIRD-PARTY-NOTICE.KaitoKit.txt).</remarks>
internal sealed class StuffItXJpegModel {
  private readonly byte[] _statistics = new byte[0x39d30];
  private readonly byte[] _signStatistics = new byte[0x2aa2];
  private readonly int[] _frequencies = new int[16];

  private void Combine(StuffItXJpegKeys k, int[] baseline, int w0, int w1, int w2, int w3) {
    var data = k.Sign ? this._signStatistics : this._statistics;
    if (k.A < 0 || k.B < 0 || k.C < 0 || k.D < 0 || Math.Max(Math.Max(k.A, k.B), Math.Max(k.C, k.D)) > data.Length - k.Width)
      throw Malformed("coefficient model context range");
    var f = this._frequencies;
    for (var i = 0; i < k.Width; ++i)
      f[i] = baseline[i] + w0 * data[k.A + i] + w1 * data[k.B + i] + w2 * data[k.C + i] + w3 * data[k.D + i];
  }

  private void Update(StuffItXJpegKeys k, int symbol, int inc, int threshold) {
    var data = k.Sign ? this._signStatistics : this._statistics;
    UpdateRow(data, k.A, k.Width, symbol, inc, threshold);
    UpdateRow(data, k.B, k.Width, symbol, inc, threshold);
    UpdateRow(data, k.C, k.Width, symbol, inc, threshold);
    if (!k.Sign) UpdateRow(data, k.D, k.Width, symbol, inc, threshold);
  }

  private static void UpdateRow(byte[] data, int row, int width, int symbol, int inc, int threshold) {
    data[row + symbol] = unchecked((byte)(data[row + symbol] + inc));
    if (data[row + symbol] < threshold) return;
    for (var i = 0; i < width; ++i) data[row + i] >>= 1;
  }

  public int Symbol(StuffItXJpegRange r, StuffItXJpegKeys k, int inc, int threshold) {
    var s = r.Value(this._frequencies.AsSpan(0, k.Width));
    this.Update(k, s, inc, threshold);
    return s;
  }

  public int Magnitude(StuffItXJpegRange r, StuffItXJpegKeys k, int inc, int threshold) {
    var s = this.Symbol(r, k, inc, threshold);
    if (s < 6) return s;
    if (s < 15) return 5 + (1 << (s - 6)) + r.Bits(s - 6);
    return 5 + r.Bits(15);
  }

  private StuffItXJpegKeys DcDist(int c, int l, int u, (int A, int B, int D, int E) p) {
    var (a, b, d, e) = p;
    int diff1 = Math.Abs(a - b), diff2 = Math.Abs(b - d), bs = BitLength(l) + BitLength(u);
    var i0 = 56 * (c == 0 ? 0 : 1) + 8 * bs + BitLength((diff1 / 4 + diff2 / 4 + Math.Abs(d - e) / 4 + Math.Abs(e - a) / 4) / 128);
    var i1 = bs * 36 + BitLength(diff1 / 512) * 6 + BitLength(diff2 / 512);
    var i2 = BitLength(diff1 / 256) * 49 + BitLength(Math.Abs(a - d) / 256) * 7 + BitLength(Math.Abs(a - e) / 256);
    var i3 = 10 * (c == 0 ? 0 : 1) + BitLength(diff1 / 32);
    var k = new StuffItXJpegKeys((i0 + T.DcStarts[0]) * 16, (i1 + T.DcStarts[1]) * 16, (i2 + T.DcStarts[2]) * 16, (i3 + T.DcStarts[3]) * 16, 16);
    this.Combine(k, DcBase, T.DcWeights[0], T.DcWeights[1], T.DcWeights[2], T.DcWeights[3]);
    return k;
  }

  private static readonly int[] DcBase = [4, 4, 4, 4, 3, 3, 3, 3, 2, 2, 2, 2, 1, 1, 1, 1];

  public int Dc(StuffItXJpegRange r, int c, int l, int u, (int, int, int, int) p) {
    var k = this.DcDist(c, l, u, p);
    var v = this.Magnitude(r, k, 8, 248);
    return v != 0 && r.Bit() == 0 ? -v : v;
  }

  public StuffItXJpegKeys HDist(int c, int dc, int[] up, int[] left, int l, int u, int cb, (int, int, int, int) near) {
    var dcbin = Math.Clamp(dc, -8191, 8191) / 1024 + 7;
    var a = Activity((Math.Abs(up[1]) * 10 + Math.Abs(left[0]) * 23) / 33 / 8);
    int avg = (near.Item1 + near.Item2 + near.Item3 + near.Item4 + 2) / 4, lh = l & 7, uh = u & 7;
    var i0 = ((c == 0 ? 0 : 1) << 9) | (uh << 6) | (lh << 3) | a;
    var i1 = (c << 9) + (uh << 6) + ((dcbin * 4) & ~7) + avg;
    var i2 = (c << 3) | (c == 2 ? cb & 7 : a);
    var i3 = (c << 7) + ((c == 2 ? cb & 7 : a) << 4) + dcbin;
    const int baseOffset = 0x2d70;
    var k = new StuffItXJpegKeys(baseOffset + (i0 + T.HorizontalStarts[0]) * 8, baseOffset + (i1 + T.HorizontalStarts[1]) * 8,
      baseOffset + (i2 + T.HorizontalStarts[2]) * 8, baseOffset + (i3 + T.HorizontalStarts[3]) * 8, 8);
    this.Combine(k, T.LimitBase, 32, 12, 1, 5);
    if (up[0] == 0) this._frequencies[uh] *= 2;
    if (c == 2) this._frequencies[cb & 7] += this._frequencies[cb & 7] / 4;
    return k;
  }

  public StuffItXJpegKeys VDist(int c, int[] up, int[] left, int l, int u, int h, int cb, (int, int, int, int) near) {
    var a = Activity((Math.Abs(up[0]) * 23 + Math.Abs(left[1]) * 10) / 33 / 8);
    int avg = (near.Item1 + near.Item2 + near.Item3 + near.Item4 + 2) / 4, lv = l >> 3, uv = u >> 3, cv = cb >> 3;
    var i0 = ((c == 0 ? 0 : 1) * 512 + uv * 64 + lv * 8) | a;
    var i1 = c * 512 + lv * 8 + cv + avg * 64;
    var i2 = ((c * 64) | (a * 8)) + (c == 2 ? cv : h);
    var i3 = ((c * 512) | (a * 64)) + h * 8 + (c == 2 ? cv : lv);
    const int baseOffset = 0x8430;
    var k = new StuffItXJpegKeys(baseOffset + (i0 + T.VerticalStarts[0]) * 8, baseOffset + (i1 + T.VerticalStarts[1]) * 8,
      baseOffset + (i2 + T.VerticalStarts[2]) * 8, baseOffset + (i3 + T.VerticalStarts[3]) * 8, 8);
    if (c == 2) this.Combine(k, T.LimitBase, 20, 8, 12, 2);
    else this.Combine(k, T.LimitBase, 20, 8, 6, 24);
    if (left[0] == 0) this._frequencies[lv] *= 2;
    if (c == 2) this._frequencies[cv] += this._frequencies[cv] / 4;
    return k;
  }

  /// <summary>The distribution of one AC magnitude, from its neighbours in the block, the blocks around it and the predictions.</summary>
  public StuffItXJpegKeys AcDist(int pos, short[] dq, int dqo, int c, int[] coefficients, int cb, int[] up, int[] left, int[] q,
      int extent, int nv, int nh, int[] zx, int[] zy, int[] sizes, int ln, int un, int urn) {
    int y = pos / 8, x = pos & 7, h = extent & 7, v = extent >> 3;
    int w1 = T.PredictorWeights[pos], w2 = T.PredictorWeights[x * 8 + y];
    int pn = Math.Abs(up[x]) * w1 + Math.Abs(left[y]) * w2, pw = w1 + w2;
    var direct = pw == 0 ? 0 : pn / (q[pos] * pw);
    int total = 0, weight = 0, sk = 0;
    if (x + 1 < h && y != 0) { total += Math.Abs((int)dq[dqo + pos - 6]); weight += 1; }
    if (x >= 2 && pos >= 11) { total += Math.Abs((int)dq[dqo + pos - 10]); weight += 1; }
    if (x >= 2 && (y != 0 || x >= 3)) { total += Math.Abs((int)dq[dqo + pos - 2]) * 2; weight += 2; sk += sizes[pos - 2]; }
    if ((x != 0 && y != 0) || (x > 1 && y == 0)) { total += Math.Abs((int)dq[dqo + pos - 1]) * 3; weight += 3; sk += sizes[pos - 1]; }
    if (pos >= 9) { total += Math.Abs((int)dq[dqo + pos - 8]) * 3; weight += 3; sk += sizes[pos - 8]; }
    if (x < h && y != 0) { total += Math.Abs((int)dq[dqo + pos - 7]) * 4; weight += 4; sk += sizes[pos - 7]; }
    if (x != 0 && pos >= 10) { total += Math.Abs((int)dq[dqo + pos - 9]) * 3; weight += 3; }
    if (pos >= 17) { total += Math.Abs((int)dq[dqo + pos - 16]) * 2; weight += 2; sk += sizes[pos - 16]; }
    if (cb >= 0) { total += Math.Abs(coefficients[cb + pos] * q[pos]) * 20; weight += 20; }
    if (urn >= 0) { total += Math.Abs(coefficients[urn + pos] * q[pos]) * 2; weight += 2; }
    if (un >= 0) { total += Math.Abs(coefficients[un + pos] * q[pos]) * 4; weight += 4; }
    if (ln >= 0) { total += Math.Abs(coefficients[ln + pos] * q[pos]) * 4; weight += 4; }
    total >>= 3;
    if (weight != 0) {
      total += pn >> 3;
      weight += pw;
    }
    int edge;
    if (pos == extent) edge = nv != 0 && nh != 0 ? 2 : 1;
    else if (y == v) edge = nv != 0 ? 4 : 3;
    else if (x == h) edge = nh != 0 ? 4 : 3;
    else edge = 0;
    var av = weight != 0 ? Category((total << 4) / (q[pos] * weight)) : 0;
    var fine = weight != 0 ? Math.Min(16383, total * 24 / (q[pos] * weight)) : 0;
    if (fine >= 8) fine = 8 + BitLength(fine - 8);
    int cc = c == 0 ? 0 : 1, group = edge * 512 + T.AcPositions[64 * cc + pos] * 16;
    var aux = Math.Abs(I16(cb >= 0 ? coefficients[cb + pos] : direct));
    var auxbin = aux < 2 ? 0 : aux < 4 ? 1 : aux < 10 ? 2 : aux < 37 ? 3 : aux <= 144 ? 4 : 5;
    int i0 = group + av, i1 = 2560 + 21 * (pos + 64 * cc) + sk + (edge & 1) * 2688 + auxbin;
    int i2 = 7936 + fine * 2 + (edge & 1), i3 = 7984 + group + Category(direct >> 1);
    const int baseOffset = 0x10a30;
    var k = new StuffItXJpegKeys(baseOffset + i0 * 16, baseOffset + i1 * 16, baseOffset + i2 * 16, baseOffset + i3 * 16, 16);
    var wi = cc * 64 + pos;
    this.Combine(k, edge != 0 ? T.AcEdgeBase : T.AcBase,
      T.AcWeights[wi] & 255, T.AcWeights[128 + wi] & 255, T.AcWeights[256 + wi] & 255, T.AcWeights[384 + wi] & 255);
    this._frequencies[0] = this._frequencies[0] * T.ZeroWeights[zx[x] * 8 + zy[y]] / 16;
    if (pos == extent && (nv == 0 || nh == 0)) this._frequencies[0] = 0;
    return k;
  }

  /// <summary>The distribution of one AC sign.</summary>
  public StuffItXJpegKeys SignDist(int pos, int[] coefficients, int co, int c, int hint, int[] up, int[] left, int[] q, int ln, int un) {
    int y = pos / 8, x = pos & 7;
    int vert = pos >= 17 ? SignClass(coefficients[co + pos - 16])
      : un >= 0 ? SignClass(I16(coefficients[un + pos] - coefficients[un + pos + 8] + coefficients[un + pos + 16] - coefficients[un + pos + 24])) : 0;
    int hor = x >= 2 && pos >= 3 ? SignClass(coefficients[co + pos - 2])
      : ln >= 0 ? SignClass(I16(coefficients[ln + pos] - coefficients[ln + pos + 1] + coefficients[ln + pos + 2] - coefficients[ln + pos + 3])) : 0;
    int chosen = x < y ? left[y] : up[x], pred1 = up[x] / q[pos], pred2 = left[y] / q[pos];
    var chosenQ = x < y ? pred2 : pred1;
    int sclass = SignClass(I16((int)((uint)chosen >> 3))), mclass = Cat4(coefficients[co + pos]);
    int combined = mclass * 3 + sclass, i0 = vert + hor * 3, i1 = (combined * 4) | Cat4(I16(chosenQ));
    var i2 = (c == 0 ? 0 : 2700) + Math.Min(y, 4) * 5 + Math.Min(x, 4) + (vert + hor * 3) * 25 + combined * 225;
    var k = new StuffItXJpegKeys((i0 + T.SignStarts[0]) * 2, (i1 + T.SignStarts[1]) * 2, (i2 + T.SignStarts[2]) * 2, 0, 2, true);
    this.Combine(k, SignBase, T.SignWeights[0], T.SignWeights[1], T.SignWeights[2], 0);
    if (x + y > 11) return k;
    var f = this._frequencies;
    var magnitude = Math.Abs(coefficients[co + pos]);
    int e0 = 0, e1 = 0;
    void Extra(int pred, int value) {
      var delta = Math.Abs(Math.Abs(pred) - magnitude);
      var close = delta < 3 ? 3 : delta < 9 ? 2 : delta < 32 ? 1 : 0;
      if (pred == 0 || close == 0) return;
      var amp = Math.Abs(value >> 3);
      var at = amp <= 64 ? T.SignAt2[amp] : 14;
      var add = f[pred > 0 ? 1 : 0] * close * at / 32;
      if (value > 0) e1 += add;
      else e0 += add;
    }
    Extra(pred1, up[x]);
    Extra(pred2, left[y]);
    f[0] += e0;
    f[1] += e1;
    if (hint < 2) {
      var at = magnitude <= 128 ? T.SignAt0[magnitude] : 14;
      f[hint] += f[hint] / 6 * T.SignFactor[pos] + f[hint] / 2 * at;
    }
    return k;
  }

  private static readonly int[] SignBase = [8, 8];

  /// <summary>The value an edge of a neighbouring block predicts across the boundary.</summary>
  private static int EdgePredictor(short[] block, int offset, int stride) {
    var value = 0;
    for (var i = 1; i < 8; ++i) value += ((i & 1) == 1 ? -1 : 1) * block[offset + i * stride];
    if (Math.Abs(value) <= 2047) value += value / 4;
    return Math.Clamp(block[offset] + value, -8191, 8191);
  }

  /// <summary>The predicted DC and the four contexts it leaves for the DC model.</summary>
  public static (int Prediction, (int, int, int, int) Contexts) DcPrediction(short[] dq, int left, int up, int dl, int ur, int ul, int q) {
    if (left < 0) {
      if (up < 0) return (0, (0, 0, 0, 0));
      var u0 = EdgePredictor(dq, up, 8);
      return (I16(u0 / q), (0, u0, 2048, 3072));
    }
    if (up < 0) {
      var l0 = EdgePredictor(dq, left, 1);
      return (I16(l0 / q), (l0, 1024, 2048, 3072));
    }
    int l = EdgePredictor(dq, left, 1), u = EdgePredictor(dq, up, 8);
    var wu = T.DcWlt[BitLength(Math.Abs(dq[left] - ul) >> 6)] + 1;
    var wl = T.DcWlt[BitLength(Math.Abs(dq[up] - ul) >> 6)] + 1;
    var urp = ur >= 0 ? EdgePredictor(dq, ur, 8) : u + EdgePredictor(dq, up, 1) - dq[up] + dq[up + 9];
    var dlp = dl >= 0 ? EdgePredictor(dq, dl, 1) : EdgePredictor(dq, left, 8) + l - dq[left] + dq[left + 9];
    int urdc = dq[ur >= 0 ? ur : up], dldc = dq[dl >= 0 ? dl : left];
    int wur = 4 * T.DcWl1[BitLength(Math.Abs(urdc - l) / q)], wdl = 4 * T.DcWl1[BitLength(Math.Abs(dldc - u) / q)];
    wu += wdl;
    wl += wur;
    var cross = T.DcWl2[BitLength(Math.Abs(dldc - urdc) / q)];
    wdl += cross;
    if (dq[up] == u) {
      wu *= 2;
      wl = (wl + 1) / 2;
      wur = 0;
    } else wur += cross;
    if (dq[left] == l) wl *= 2;
    return (I16((u * wu + l * wl + urp * wur + dlp * wdl) / ((wu + wl + wur + wdl) * q)), (l, u, urp, dlp));
  }

  /// <summary>The prediction of one row or column of a neighbour's dequantized coefficients, weighted by the profile.</summary>
  public static int DirectionalPrediction(short[] dq, int start, int stride, int profile) {
    var value = 0;
    for (var i = 0; i < 8; ++i) value += ((i & 1) == 1 ? -1 : 1) * ((dq[start + i * stride] * T.Factors[profile * 8 + i]) >> 4);
    return Math.Clamp(value, -8191, 8191);
  }
}

/// <summary>
/// The coefficient blocks of mode 2: quantized coefficients (every plane for progressive images, two
/// block rows otherwise), dequantized coefficients and nonzero extents of the last two rows, with row
/// tags so a reused slot is never taken for a neighbour that does not exist.
/// </summary>
internal sealed class StuffItXJpegBlockStore {
  private readonly (int Width, int Height, int Offset, int Ring)[] _layout = new (int, int, int, int)[3];
  private readonly int[] _extents;
  private readonly int[] _tags;
  private readonly byte[] _present;
  private readonly bool _keepAll;
  private readonly int _componentCount;
  private readonly int _blockCount;
  private int _decoded;

  public int[] Coefficients { get; }
  public short[] Dequantized { get; }

  public StuffItXJpegBlockStore(StuffItXJpegGeometry geometry, bool keepAll) {
    this._keepAll = keepAll;
    this._componentCount = geometry.Components.Length;
    this._blockCount = geometry.Blocks;
    int full = 0, ring = 0;
    for (var c = 0; c < this._componentCount; ++c) {
      var p = geometry.Components[c];
      int width = geometry.Width * p.Horizontal, height = geometry.Height * p.Vertical;
      this._layout[c] = (width, height, full, ring);
      full += width * height;
      ring += width * 2;
    }
    this.Coefficients = new int[(keepAll ? full : ring) * 64];
    this.Dequantized = new short[ring * 64];
    this._extents = new int[keepAll ? full : ring];
    this._tags = Enumerable.Repeat(-1, ring).ToArray();
    this._present = new byte[keepAll ? full : 0];
  }

  private int Index(int c, int row, int col) {
    if (c < 0 || c >= this._componentCount) return -1;
    var l = this._layout[c];
    if (row < 0 || row >= l.Height || col < 0 || col >= l.Width) return -1;
    var i = l.Ring + (row & 1) * l.Width + col;
    return this._tags[i] == row ? i : -1;
  }

  private int FullIndex(int c, int row, int col) {
    if (c < 0 || c >= this._componentCount) return -1;
    var l = this._layout[c];
    if (row < 0 || row >= l.Height || col < 0 || col >= l.Width) return -1;
    return l.Offset + row * l.Width + col;
  }

  /// <summary>Offset of a decoded block's quantized coefficients, or -1.</summary>
  public int Co(int c, int row, int col) {
    if (this._keepAll) {
      var i = this.FullIndex(c, row, col);
      return i >= 0 && this._present[i] != 0 ? i * 64 : -1;
    }
    var r = this.Index(c, row, col);
    return r >= 0 ? r * 64 : -1;
  }

  /// <summary>Offset of a decoded block's dequantized coefficients, or -1.</summary>
  public int Dq(int c, int row, int col) {
    var r = this.Index(c, row, col);
    return r >= 0 ? r * 64 : -1;
  }

  /// <summary>A decoded block's nonzero extent (h + 8v), or 0.</summary>
  public int Extent(int c, int row, int col) {
    if (this._keepAll) {
      var i = this.FullIndex(c, row, col);
      return i >= 0 && this._present[i] != 0 ? this._extents[i] : 0;
    }
    var r = this.Index(c, row, col);
    return r >= 0 ? this._extents[r] : 0;
  }

  public void Commit(int c, int row, int col, int ring, int extent) {
    this._tags[ring] = row;
    var i = this._keepAll ? this._layout[c].Offset + row * this._layout[c].Width + col : ring;
    this._extents[i] = extent;
    if (this._keepAll) this._present[i] = 1;
    ++this._decoded;
  }

  /// <summary>Clears the slots of a block about to be decoded; returns its coefficient and dequantized offsets and ring slot.</summary>
  public (int Co, int Dq, int Ring) Prepare(int c, int row, int col) {
    if (c < 0 || c >= this._componentCount || this._decoded >= this._blockCount) throw Malformed("coefficient coordinates");
    var l = this._layout[c];
    if (row < 0 || row >= l.Height || col < 0 || col >= l.Width) throw Malformed("coefficient coordinates");
    var i = l.Ring + (row & 1) * l.Width + col;
    if (this._tags[i] == row) throw Malformed("duplicate coefficient block");
    var co = (this._keepAll ? l.Offset + row * l.Width + col : i) * 64;
    Array.Clear(this.Coefficients, co, 64);
    Array.Clear(this.Dequantized, i * 64, 64);
    return (co, i * 64, i);
  }

  /// <summary>Offset of any block of a fully kept plane.</summary>
  public int Full(int c, int row, int col) {
    var l = this._layout[c];
    return (l.Offset + row * l.Width + col) * 64;
  }
}

/// <summary>Decodes the mode-2 coefficient blocks one at a time.</summary>
internal sealed class StuffItXJpegBlocks(StuffItXJpegRange decoder, StuffItXJpegGeometry geometry, bool keepAll) {
  private readonly StuffItXJpegModel _model = new();
  private readonly int[] _upPred = new int[8];
  private readonly int[] _leftPred = new int[8];
  private readonly int[] _zx = new int[8];
  private readonly int[] _zy = new int[8];
  private readonly int[] _sizes = new int[64];

  public StuffItXJpegRange Decoder => decoder;
  public StuffItXJpegBlockStore Store { get; } = new(geometry, keepAll);

  /// <summary>Decodes one block; returns the offset of its quantized coefficients in <see cref="StuffItXJpegBlockStore.Coefficients"/>.</summary>
  /// <param name="hint">The block's place in its MCU: 0 has a lower-left neighbour, 3 no upper-right one.</param>
  public int Block(int c, int row, int col, int[] q, int hint, int profile) {
    var store = this.Store;
    var dqs = store.Dequantized;
    var coefficients = store.Coefficients;
    int left = store.Dq(c, row, col - 1), up = store.Dq(c, row - 1, col);
    var ur = hint != 3 ? store.Dq(c, row - 1, col + 1) : -1;
    var dl = hint == 0 ? store.Dq(c, row + 1, col - 1) : -1;
    if ((col > 0 && left < 0) || (row > 0 && up < 0)) throw Malformed("missing coefficient neighbor");
    var ul = 0;
    if (row > 0 && col > 0 && hint != 0) {
      var o = store.Dq(c, row - 1, col - 1);
      ul = o >= 0 ? dqs[o] : 0;
    }
    var (pred, contexts) = StuffItXJpegModel.DcPrediction(dqs, left, up, dl, ur, ul, q[0]);
    int le = store.Extent(c, row, col - 1), ue = store.Extent(c, row - 1, col);
    var cb = c == 2 ? store.Co(c - 1, row, col) : -1;
    var ce = c == 2 ? store.Extent(c - 1, row, col) : 0;
    int ln = store.Co(c, row, col - 1), un = store.Co(c, row - 1, col);
    var urn = hint != 3 ? store.Co(c, row - 1, col + 1) : -1;
    var near = (le, ue, store.Extent(c, row, col - 2), hint != 3 ? store.Extent(c, row - 1, col + 1) : 0);
    var (co, dq, ring) = store.Prepare(c, row, col);
    Array.Clear(this._upPred);
    Array.Clear(this._leftPred);
    Array.Clear(this._zx);
    Array.Clear(this._zy);
    Array.Clear(this._sizes);
    var upPred = this._upPred;
    var leftPred = this._leftPred;

    coefficients[co] = I16(pred + this._model.Dc(decoder, c, Math.Max(le & 7, le >> 3), Math.Max(ue & 7, ue >> 3), contexts));
    dqs[dq] = unchecked((short)(coefficients[co] * q[0]));
    if (up >= 0) {
      for (var i = 0; i < 8; ++i) upPred[i] = StuffItXJpegModel.DirectionalPrediction(dqs, up + i, 8, profile);
      upPred[0] -= dqs[dq];
    }
    if (left >= 0) {
      for (var i = 0; i < 8; ++i) leftPred[i] = StuffItXJpegModel.DirectionalPrediction(dqs, left + i * 8, 1, profile);
      leftPred[0] -= dqs[dq];
    }
    var hk = this._model.HDist(c, dqs[dq], upPred, leftPred, le, ue, ce, (near.le & 7, near.ue & 7, near.Item3 & 7, near.Item4 & 7));
    var h = this._model.Symbol(decoder, hk, 2, 64);
    var vk = this._model.VDist(c, upPred, leftPred, le, ue, h, ce, (near.le >> 3, near.ue >> 3, near.Item3 >> 3, near.Item4 >> 3));
    var v = this._model.Symbol(decoder, vk, 2, 64);
    var extent = h + 8 * v;
    this._sizes[0] = Cat4(coefficients[co]);
    int nv = 0, nh = 0;
    for (var y = 0; y <= v; ++y)
      for (var x = 0; x <= h; ++x) {
        var pos = y * 8 + x;
        if (pos == 0) continue;
        var ak = this._model.AcDist(pos, dqs, dq, c, coefficients, cb, upPred, leftPred, q, extent, nv, nh, this._zx, this._zy, this._sizes, ln, un, urn);
        var magnitude = this._model.Magnitude(decoder, ak, 2, 160);
        coefficients[co + pos] = magnitude;
        if (magnitude == 0) {
          ++this._zx[x];
          ++this._zy[y];
          continue;
        }
        this._zx[x] = 0;
        this._zy[y] = 0;
        if (y == v) ++nv;
        if (x == h) ++nh;
        var selected = y > x ? upPred[x] : leftPred[y];
        var signHint = left >= 0 && up >= 0 && Math.Abs(selected) >= 43 ? (selected > 0 ? 1 : 0) : 2;
        var sk = this._model.SignDist(pos, coefficients, co, c, signHint, upPred, leftPred, q, ln, un);
        if (this._model.Symbol(decoder, sk, 6, 129) == 0) coefficients[co + pos] = -coefficients[co + pos];
        this._sizes[pos] = Cat4(coefficients[co + pos]);
        dqs[dq + pos] = unchecked((short)(coefficients[co + pos] * q[pos]));
        if (up >= 0) upPred[x] -= (dqs[dq + pos] * T.Factors[profile * 8 + y]) >> 4;
        if (left >= 0) leftPred[y] -= (dqs[dq + pos] * T.Factors[profile * 8 + x]) >> 4;
      }
    store.Commit(c, row, col, ring, extent);
    return co;
  }
}

/// <summary>Mode 2 with a baseline (SOF0) frame: blocks decoded an MCU row at a time and Huffman-coded back.</summary>
internal sealed class StuffItXJpegBaseline {
  private readonly StuffItXJpegGeometry _geometry;
  private readonly (int Hs, int Vs, int Profile, StuffItXJpegHuffman Dc, StuffItXJpegHuffman Ac, int[] Q)[] _components;
  private readonly int[] _previousDc = new int[3];
  private readonly StuffItXJpegBlocks _blocks;
  private readonly StuffItXJpegEntropyWriter _entropy;
  private readonly int _restart;
  private int _row;
  private int _restarts;

  public bool Done => this._row == this._geometry.Height;

  public StuffItXJpegBaseline(StuffItXJpegPrefix prefix, StuffItXJpegRange decoder, StuffItXJpegOutput output) {
    if (prefix.Frame is not { } frame || prefix.Scan is not { } scan) throw Malformed("mode-2 baseline JPEG required");
    if (frame.Marker != StuffItXJpegMarker.Sof0) throw Unsupported("mode-2 baseline JPEG required");
    if (scan.Ss != 0 || scan.Se != 63 || scan.Ah != 0 || scan.Al != 0) throw Malformed("unsupported baseline scan");
    this._geometry = new StuffItXJpegGeometry(frame);
    if (!scan.Components.Select(s => s.Id).Order().SequenceEqual(this._geometry.Components.Select(g => g.Id)))
      throw Unsupported("single interleaved scan required");
    var tables = new StuffItXJpegTableSet(prefix);
    this._restart = tables.Restart;
    this._components = new (int, int, int, StuffItXJpegHuffman, StuffItXJpegHuffman, int[])[this._geometry.Components.Length];
    for (var c = 0; c < this._components.Length; ++c) {
      var p = this._geometry.Components[c];
      var selector = scan.Components.First(s => s.Id == p.Id).Selector;
      if (!tables.Huffman.TryGetValue(StuffItXJpegTableSet.DcTableKey(selector), out var dc)
          || !tables.Huffman.TryGetValue(StuffItXJpegTableSet.AcTableKey(selector), out var ac))
        throw Malformed("undefined Huffman table");
      this._components[c] = (p.Horizontal, p.Vertical, tables.FrameProfiles[c], dc, ac, tables.ScaledChecked(p.Quantization));
    }
    this._blocks = new StuffItXJpegBlocks(decoder, this._geometry, keepAll: false);
    this._entropy = new StuffItXJpegEntropyWriter(output);
  }

  public void Step() {
    if (this.Done) return;
    var coefficients = this._blocks.Store.Coefficients;
    for (var column = 0; column < this._geometry.Width; ++column) {
      for (var c = 0; c < this._components.Length; ++c) {
        var p = this._components[c];
        for (var dy = 0; dy < p.Vs; ++dy)
          for (var dx = 0; dx < p.Hs; ++dx) {
            var hint = p.Vs == 1 ? 1 : p.Hs == 2 ? dy * p.Hs + dx : 3 * dy;
            var co = this._blocks.Block(c, this._row * p.Vs + dy, column * p.Hs + dx, p.Q, hint, p.Profile);
            this._entropy.Block(coefficients, co, this._previousDc[c], p.Dc, p.Ac);
            this._previousDc[c] = coefficients[co];
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
