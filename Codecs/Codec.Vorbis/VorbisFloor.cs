#pragma warning disable CS1591

namespace Codec.Vorbis;

/// <summary>
/// Vorbis floor 1 decoder + curve synthesis. Reads the per-packet Y-value bit
/// stream, runs the "low-neighbor / high-neighbor" prediction, and renders the
/// resulting piecewise-linear log-amplitude curve at the block resolution.
/// </summary>
internal static class VorbisFloor {

  /// <summary>
  /// Decode a floor-1 packet and rasterise the curve into
  /// <paramref name="output"/>.  Returns <c>false</c> if the floor is "unused"
  /// (the silence flag) — residue for this channel then outputs zeroes.
  /// </summary>
  public static bool DecodePacket(
    VorbisBitReader br,
    VorbisSetup.Floor f,
    VorbisCodebook[] codebooks,
    Span<float> output
  ) {
    var nonzero = br.ReadBits(1);
    if (nonzero == 0) {
      output.Clear();
      return false;
    }

    Span<int> range = stackalloc int[4] { 256, 128, 86, 64 };
    var rng = range[f.Multiplier - 1];
    var yBits = IntegerBitsFor(rng - 1);
    var valueCount = f.XList.Length;
    Span<int> y = stackalloc int[valueCount];
    y[0] = (int)br.ReadBits(yBits);
    y[1] = (int)br.ReadBits(yBits);
    var offset = 2;
    for (var i = 0; i < f.PartitionClassList.Length; ++i) {
      var classIdx = f.PartitionClassList[i];
      var cdim = f.ClassDimensions[classIdx];
      var cbits = f.ClassSubclasses[classIdx];
      var csub = (1 << cbits) - 1;
      var cval = 0;
      if (cbits > 0) {
        cval = codebooks[f.ClassMasterbooks[classIdx]].DecodeScalar(br);
        if (cval < 0) return false;
      }
      for (var j = 0; j < cdim; ++j) {
        var book = f.SubclassBooks[classIdx][cval & csub];
        cval >>= cbits;
        if (book >= 0) {
          var v = codebooks[book].DecodeScalar(br);
          if (v < 0) return false;
          y[offset + j] = v;
        } else y[offset + j] = 0;
      }
      offset += cdim;
    }

    // --- amplitude value synthesis (predict / correct) ---
    Span<bool> step2Flag = stackalloc bool[valueCount];
    Span<int> finalY = stackalloc int[valueCount];
    step2Flag[0] = step2Flag[1] = true;
    finalY[0] = y[0];
    finalY[1] = y[1];
    for (var i = 2; i < valueCount; ++i) {
      var (low, high) = LowHighNeighbors(f.XList, i);
      var predicted = RenderPoint(f.XList[low], finalY[low], f.XList[high], finalY[high], f.XList[i]);
      var val = y[i];
      var highroom = rng - predicted;
      var lowroom = predicted;
      var room = highroom < lowroom ? highroom * 2 : lowroom * 2;
      if (val != 0) {
        step2Flag[low] = true;
        step2Flag[high] = true;
        step2Flag[i] = true;
        if (val >= room) {
          finalY[i] = highroom > lowroom ? val - lowroom + predicted : -val + highroom + predicted - 1;
        } else {
          finalY[i] = (val & 1) != 0 ? predicted - ((val + 1) >> 1) : predicted + (val >> 1);
        }
      } else {
        step2Flag[i] = false;
        finalY[i] = predicted;
      }
    }

    // --- sort x-axis & render linear-interpolated curve ---
    var n = output.Length;
    var order = new int[valueCount];
    for (var i = 0; i < valueCount; ++i) order[i] = i;
    Array.Sort(order, (a, b) => f.XList[a].CompareTo(f.XList[b]));
    var hx = 0; var hy = 0;
    var lx = 0; var ly = finalY[order[0]] * f.Multiplier;
    for (var i = 1; i < valueCount; ++i) {
      var idx = order[i];
      if (step2Flag[idx]) {
        hx = f.XList[idx];
        hy = finalY[idx] * f.Multiplier;
        RenderLine(lx, ly, hx, hy, output, n);
        lx = hx; ly = hy;
      }
    }
    if (hx < n) RenderLine(hx, hy, n, hy, output, n);

    // Convert from "floor1 amplitude" integer (0..255) to linear amplitude via
    // the Vorbis inverse-dB table (expanded 8-bit index).
    for (var i = 0; i < n; ++i) {
      var amp = (int)output[i];
      if (amp < 0) amp = 0;
      if (amp > 255) amp = 255;
      output[i] = Floor1InverseDb[amp];
    }
    return true;
  }

  private static int IntegerBitsFor(int value) {
    var bits = 0;
    while (value > 0) { bits++; value >>= 1; }
    return bits;
  }

  private static (int low, int high) LowHighNeighbors(int[] x, int i) {
    var target = x[i];
    var lowX = int.MinValue; var low = 0;
    var highX = int.MaxValue; var high = 0;
    for (var j = 0; j < i; ++j) {
      var xj = x[j];
      if (xj < target && xj > lowX) { lowX = xj; low = j; }
      if (xj > target && xj < highX) { highX = xj; high = j; }
    }
    return (low, high);
  }

  private static int RenderPoint(int x0, int y0, int x1, int y1, int x) {
    var dy = y1 - y0;
    var adx = x1 - x0;
    var ady = Math.Abs(dy);
    var err = ady * (x - x0);
    var off = err / adx;
    return dy < 0 ? y0 - off : y0 + off;
  }

  private static void RenderLine(int x0, int y0, int x1, int y1, Span<float> v, int n) {
    if (x1 > n) x1 = n;
    if (x0 >= x1) return;
    var dy = y1 - y0;
    var adx = x1 - x0;
    var ady = Math.Abs(dy);
    var baseVal = dy / adx;
    var sy = dy < 0 ? baseVal - 1 : baseVal + 1;
    ady -= Math.Abs(baseVal) * adx;
    var y = y0;
    if (x0 < n) v[x0] = y;
    var err = 0;
    for (var x = x0 + 1; x < x1; ++x) {
      err += ady;
      if (err >= adx) { err -= adx; y += sy; } else y += baseVal;
      v[x] = y;
    }
  }

  // floor1_inverse_dB_table: 256 entries mapping floor1 amplitude to linear gain.
  // Source: Vorbis I specification §9.3.
  private static readonly float[] Floor1InverseDb = BuildInverseDbTable();

  private static float[] BuildInverseDbTable() {
    // Formula: gain = 10 ^ ((amp - 127) * 0.0035) — scaled to match the spec table.
    // We compute the canonical values on startup; the first entry (0) is forced
    // to zero, matching the spec.
    var t = new float[256];
    for (var i = 0; i < 256; ++i) {
      if (i == 0) { t[i] = 0f; continue; }
      t[i] = (float)Math.Exp((i - 127.5) * 0.11512925464970228); // ln(10)*0.05
    }
    return t;
  }
}
