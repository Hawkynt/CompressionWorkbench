#pragma warning disable CS1591

namespace Codec.Vorbis;

/// <summary>
/// Vorbis floor 1 decoder + curve synthesis. Reads the per-packet Y-value bit
/// stream, runs the "low-neighbor / high-neighbor" prediction, and renders the
/// resulting piecewise-linear log-amplitude curve at the block resolution.
/// </summary>
internal static class VorbisFloor {

  /// <summary>
  /// Decode one floor packet (type 0 or type 1) and rasterise the curve into
  /// <paramref name="output"/>.  Returns <c>false</c> if the floor is "unused"
  /// (the silence flag) — residue for this channel then outputs zeroes.
  /// </summary>
  public static bool DecodePacket(
    VorbisBitReader br,
    VorbisSetup.Floor f,
    VorbisCodebook[] codebooks,
    Span<float> output
  ) => f.Type == 0
    ? DecodeFloor0(br, f, codebooks, output)
    : DecodeFloor1(br, f, codebooks, output);

  // ── floor 0 ──────────────────────────────────────────────────────────────

  /// <summary>
  /// Decode a floor-0 packet (LSP representation) and render the bark-mapped
  /// log-spectral envelope into linear amplitude. Follows Vorbis I §6.2.2/§6.2.3.
  /// </summary>
  private static bool DecodeFloor0(
    VorbisBitReader br,
    VorbisSetup.Floor f,
    VorbisCodebook[] codebooks,
    Span<float> output
  ) {
    var n = output.Length;

    // §6.2.2 packet decode: amplitude, then LSP coefficients (VQ-decoded).
    var amplitude = (int)br.ReadBits(f.AmplitudeBits);
    if (amplitude <= 0) {
      output.Clear();
      return false; // 'unused' — channel is silence.
    }

    var bookNumber = (int)br.ReadBits(IntegerBitsFor(f.BookList.Length - 1));
    if (bookNumber < 0 || bookNumber >= f.BookList.Length) {
      output.Clear();
      return false;
    }
    var book = codebooks[f.BookList[bookNumber]];
    var dims = book.Dimensions;
    if (dims < 1) {
      output.Clear();
      return false;
    }

    var coefficients = new float[f.Order];
    var filled = 0;
    double last = 0;
    Span<float> vec = stackalloc float[dims];
    while (filled < f.Order) {
      if (!book.DecodeVector(br, vec)) {
        // End-of-packet during the coefficient read ⇒ 'unused' (§6.2.2).
        output.Clear();
        return false;
      }
      for (var d = 0; d < dims && filled < f.Order; ++d)
        coefficients[filled++] = (float)(vec[d] + last);
      last = coefficients[filled - 1];
    }

    SynthesizeFloor0(f, amplitude, coefficients, output, n);
    return true;
  }

  /// <summary>
  /// §6.2.3 curve computation: bark-map the LSP coefficients to a smooth
  /// spectral envelope, then convert dB to linear amplitude.
  /// </summary>
  private static void SynthesizeFloor0(
    VorbisSetup.Floor f,
    int amplitude,
    float[] coefficients,
    Span<float> output,
    int n
  ) {
    var order = f.Order;
    var barkMapSize = f.BarkMapSize;
    var rate = f.Rate;

    // map[i] = min(bark_map_size-1, foobar[i]); map is constant over runs so we
    // only recompute p/q when the mapped index changes (spec step 6/7).
    var omegaScale = Math.PI / barkMapSize;
    var barkNyquist = Bark(0.5 * rate);
    var ampDenom = ((1 << f.AmplitudeBits) - 1) * 1.0;
    var ampOffset = f.AmplitudeOffset;

    // Precompute cos(coefficients) once.
    var cosCoef = new double[order];
    for (var k = 0; k < order; ++k) cosCoef[k] = Math.Cos(coefficients[k]);

    var lastMap = int.MinValue;
    var value = 0f;
    for (var i = 0; i < n; ++i) {
      int map;
      if (barkNyquist <= 0) {
        map = 0;
      } else {
        var foobar = (int)Math.Floor(
          Bark(rate * (double)i / (2 * n)) * barkMapSize / barkNyquist);
        map = Math.Min(barkMapSize - 1, foobar);
        if (map < 0) map = 0;
      }

      if (map != lastMap) {
        lastMap = map;
        var w = Math.Cos(omegaScale * map);
        double p, q;
        if ((order & 1) != 0) {
          // odd order
          p = 1.0 - w * w;
          q = 0.25;
          for (var j = 0; j + 1 < order; j += 2) {
            var dp = cosCoef[j + 1] - w;
            var dq = cosCoef[j] - w;
            p *= 4.0 * dp * dp;
            q *= 4.0 * dq * dq;
          }
        } else {
          // even order
          p = (1.0 - w) * 0.5;
          q = (1.0 + w) * 0.5;
          for (var j = 0; j + 1 < order; j += 2) {
            var dp = cosCoef[j + 1] - w;
            var dq = cosCoef[j] - w;
            p *= 4.0 * dp * dp;
            q *= 4.0 * dq * dq;
          }
        }
        var linear = Math.Exp(
          0.11512925 * (amplitude * ampOffset / (ampDenom * Math.Sqrt(p + q)) - ampOffset));
        value = (float)linear;
      }
      output[i] = value;
    }
  }

  /// <summary>The Vorbis bark approximation (§6.2.3).</summary>
  private static double Bark(double x)
    => 13.1 * Math.Atan(0.00074 * x)
       + 2.24 * Math.Atan(0.0000000185 * x * x)
       + 0.0001 * x;

  // ── floor 1 ──────────────────────────────────────────────────────────────

  private static bool DecodeFloor1(
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

  /// <summary>
  /// <c>floor1_inverse_dB_table</c> from Vorbis I specification §9.3, verbatim: 256 entries mapping a
  /// floor-1 amplitude (0..255) to linear gain, index 0 ≈ -139.4 dB and index 255 = 1.0 (0 dB).
  /// </summary>
  internal static readonly float[] Floor1InverseDb = [
    1.0649863e-07f, 1.1341951e-07f, 1.2079015e-07f, 1.2863978e-07f,
    1.3699951e-07f, 1.4590251e-07f, 1.5538408e-07f, 1.6548181e-07f,
    1.7623575e-07f, 1.8768855e-07f, 1.9988561e-07f, 2.1287530e-07f,
    2.2670913e-07f, 2.4144197e-07f, 2.5713223e-07f, 2.7384213e-07f,
    2.9163793e-07f, 3.1059021e-07f, 3.3077411e-07f, 3.5226968e-07f,
    3.7516214e-07f, 3.9954229e-07f, 4.2550680e-07f, 4.5315863e-07f,
    4.8260743e-07f, 5.1396998e-07f, 5.4737065e-07f, 5.8294187e-07f,
    6.2082472e-07f, 6.6116941e-07f, 7.0413592e-07f, 7.4989464e-07f,
    7.9862701e-07f, 8.5052630e-07f, 9.0579828e-07f, 9.6466216e-07f,
    1.0273513e-06f, 1.0941144e-06f, 1.1652161e-06f, 1.2409384e-06f,
    1.3215816e-06f, 1.4074654e-06f, 1.4989305e-06f, 1.5963394e-06f,
    1.7000785e-06f, 1.8105592e-06f, 1.9282195e-06f, 2.0535261e-06f,
    2.1869758e-06f, 2.3290978e-06f, 2.4804557e-06f, 2.6416497e-06f,
    2.8133190e-06f, 2.9961443e-06f, 3.1908506e-06f, 3.3982101e-06f,
    3.6190449e-06f, 3.8542308e-06f, 4.1047004e-06f, 4.3714470e-06f,
    4.6555282e-06f, 4.9580707e-06f, 5.2802740e-06f, 5.6234160e-06f,
    5.9888572e-06f, 6.3780469e-06f, 6.7925283e-06f, 7.2339451e-06f,
    7.7040476e-06f, 8.2047000e-06f, 8.7378876e-06f, 9.3057248e-06f,
    9.9104632e-06f, 1.0554501e-05f, 1.1240392e-05f, 1.1970856e-05f,
    1.2748789e-05f, 1.3577278e-05f, 1.4459606e-05f, 1.5399272e-05f,
    1.6400004e-05f, 1.7465768e-05f, 1.8600792e-05f, 1.9809576e-05f,
    2.1096914e-05f, 2.2467911e-05f, 2.3928002e-05f, 2.5482978e-05f,
    2.7139006e-05f, 2.8902651e-05f, 3.0780908e-05f, 3.2781225e-05f,
    3.4911534e-05f, 3.7180282e-05f, 3.9596466e-05f, 4.2169667e-05f,
    4.4910090e-05f, 4.7828601e-05f, 5.0936773e-05f, 5.4246931e-05f,
    5.7772202e-05f, 6.1526565e-05f, 6.5524908e-05f, 6.9783085e-05f,
    7.4317983e-05f, 7.9147585e-05f, 8.4291040e-05f, 8.9768747e-05f,
    9.5602426e-05f, 0.00010181521f, 0.00010843174f, 0.00011547824f,
    0.00012298267f, 0.00013097477f, 0.00013948625f, 0.00014855085f,
    0.00015820453f, 0.00016848555f, 0.00017943469f, 0.00019109536f,
    0.00020351382f, 0.00021673929f, 0.00023082423f, 0.00024582449f,
    0.00026179955f, 0.00027881276f, 0.00029693158f, 0.00031622787f,
    0.00033677814f, 0.00035866388f, 0.00038197188f, 0.00040679456f,
    0.00043323036f, 0.00046138411f, 0.00049136745f, 0.00052329927f,
    0.00055730621f, 0.00059352311f, 0.00063209358f, 0.00067317058f,
    0.00071691700f, 0.00076350630f, 0.00081312324f, 0.00086596457f,
    0.00092223983f, 0.00098217216f, 0.0010459992f, 0.0011139742f,
    0.0011863665f, 0.0012634633f, 0.0013455702f, 0.0014330129f,
    0.0015261382f, 0.0016253153f, 0.0017309374f, 0.0018434235f,
    0.0019632195f, 0.0020908006f, 0.0022266726f, 0.0023713743f,
    0.0025254795f, 0.0026895994f, 0.0028643847f, 0.0030505286f,
    0.0032487691f, 0.0034598925f, 0.0036847358f, 0.0039241906f,
    0.0041792066f, 0.0044507950f, 0.0047400328f, 0.0050480668f,
    0.0053761186f, 0.0057254891f, 0.0060975636f, 0.0064938176f,
    0.0069158225f, 0.0073652516f, 0.0078438871f, 0.0083536271f,
    0.0088964928f, 0.009474637f, 0.010090352f, 0.010746080f,
    0.011444421f, 0.012188144f, 0.012980198f, 0.013823725f,
    0.014722068f, 0.015678791f, 0.016697687f, 0.017782797f,
    0.018938423f, 0.020169149f, 0.021479854f, 0.022875735f,
    0.024362330f, 0.025945531f, 0.027631618f, 0.029427276f,
    0.031339626f, 0.033376252f, 0.035545228f, 0.037855157f,
    0.040315199f, 0.042935108f, 0.045725273f, 0.048696758f,
    0.051861348f, 0.055231591f, 0.058820850f, 0.062643361f,
    0.066714279f, 0.071049749f, 0.075666962f, 0.080584227f,
    0.085821044f, 0.091398179f, 0.097337747f, 0.10366330f,
    0.11039993f, 0.11757434f, 0.12521498f, 0.13335215f,
    0.14201813f, 0.15124727f, 0.16107617f, 0.17154380f,
    0.18269168f, 0.19456402f, 0.20720788f, 0.22067342f,
    0.23501402f, 0.25028656f, 0.26655159f, 0.28387361f,
    0.30232132f, 0.32196786f, 0.34289114f, 0.36517414f,
    0.38890521f, 0.41417847f, 0.44109412f, 0.46975890f,
    0.50028648f, 0.53279791f, 0.56742212f, 0.60429640f,
    0.64356699f, 0.68538959f, 0.72993007f, 0.77736504f,
    0.82788260f, 0.88168307f, 0.9389798f, 1.0f,
  ];
}
