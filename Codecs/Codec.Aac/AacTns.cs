#pragma warning disable CS1591

namespace Codec.Aac;

/// <summary>
/// One TNS filter for one window: a band-limited all-pole inverse prediction
/// filter applied across frequency (ISO/IEC 14496-3 §4.6.9).
/// </summary>
internal readonly record struct TnsFilter(int Window, int StartBand, int EndBand, int Order, bool Down, float[] Coefficients);

/// <summary>
/// Temporal Noise Shaping (TNS) inverse filter per ISO/IEC 14496-3 §4.6.9.
/// TNS applies a per-band linear-prediction filter in the frequency domain to
/// shape quantisation noise across time, used for transient signals in AAC-LC.
/// </summary>
internal sealed class TnsData {

  public List<TnsFilter> Filters { get; } = [];

  /// <summary>True when the channel carried no TNS filters.</summary>
  public bool IsEmpty => this.Filters.Count == 0;

  /// <summary>
  /// Reads the tns_data block for one ICS. The window count is 8 for EIGHT_SHORT,
  /// otherwise 1; the long-window path uses wider count/length/order fields.
  /// </summary>
  public static TnsData Decode(AacBitReader reader, IcsInfo ics) {
    var result = new TnsData();
    var isShort = ics.IsEightShort;
    var numWindows = isShort ? 8 : 1;
    var nFiltBits = isShort ? 1 : 2;
    var lengthBits = isShort ? 4 : 6;
    var orderBits = isShort ? 3 : 5;
    var maxOrder = isShort ? 7 : 12; // AAC-LC long order is capped at 12

    for (var w = 0; w < numWindows; ++w) {
      var nFilt = (int)reader.ReadBits(nFiltBits);
      if (nFilt == 0) continue;
      var coefRes = (int)reader.ReadBits(1);
      for (var f = 0; f < nFilt; ++f) {
        _ = reader.ReadBits(lengthBits); // band length (relative to running start)
        var order = (int)reader.ReadBits(orderBits);
        if (order == 0) continue;
        if (order > maxOrder)
          throw new InvalidDataException($"AAC TNS order {order} exceeds the LC limit.");
        var direction = reader.ReadBits(1) == 1;
        var coefCompress = (int)reader.ReadBits(1);
        var coefBits = coefRes + 3 - coefCompress;
        var coefs = new float[order];
        for (var i = 0; i < order; ++i)
          coefs[i] = (int)reader.ReadBits(coefBits);
        var lpc = DecodeCoefficients(coefs, coefRes == 1, coefCompress == 1, order);
        result.Filters.Add(new TnsFilter(w, 0, 0, order, direction, lpc));
      }
    }
    return result;
  }

  // Inverse-quantise the transmitted reflection (PARCOR) coefficients and run the
  // step-up recursion to obtain LPC coefficients (ISO/IEC 14496-3 §4.6.9.3).
  private static float[] DecodeCoefficients(float[] raw, bool highRes, bool compressed, int order) {
    var coefBits = (highRes ? 4 : 3) - (compressed ? 1 : 0);
    var signBit = 1 << (coefBits - 1);
    var fullRange = 1 << coefBits;
    // De-quantisation factor: maps the signed integer index to a reflection
    // coefficient via a sine mapping (Table 4.91 / §4.6.9.3).
    var iqfac = ((1 << (coefBits - 1)) - 0.5) / (Math.PI / 2.0);
    var iqfacInv = ((1 << (coefBits - 1)) + 0.5) / (Math.PI / 2.0);

    var parcor = new double[order];
    for (var i = 0; i < order; ++i) {
      var q = (int)raw[i];
      if ((q & signBit) != 0) q -= fullRange; // sign-extend
      parcor[i] = Math.Sin(q / (q >= 0 ? iqfac : iqfacInv));
    }

    // Step-up: reflection coefficients -> direct-form LPC (a[1..order]).
    var a = new double[order + 1];
    for (var m = 0; m < order; ++m) {
      var tmp = new double[order + 1];
      tmp[m + 1] = parcor[m];
      for (var i = 1; i <= m; ++i)
        tmp[i] = a[i] + parcor[m] * a[m + 1 - i];
      for (var i = 1; i <= m + 1; ++i)
        a[i] = tmp[i];
    }
    var lpc = new float[order];
    for (var i = 0; i < order; ++i) lpc[i] = (float)a[i + 1];
    return lpc;
  }

  /// <summary>
  /// Applies in-place inverse TNS filtering to one window's spectral coefficients.
  /// The filter region spans the full coded band; AAC-LC applies it across the
  /// scale-factor bands the encoder selected (here: the whole long/short window).
  /// </summary>
  public void Apply(float[] spectrum, IcsInfo ics) {
    if (this.IsEmpty) return;
    var windowBins = ics.IsEightShort ? AacFilterBank.ShortFrameSize : AacFilterBank.LongFrameSize;
    foreach (var filter in this.Filters) {
      if (filter.Order == 0) continue;
      var start = filter.Window * windowBins;
      var size = windowBins;
      var inc = filter.Down ? 1 : -1;
      var idx = filter.Down ? start : start + size - 1;
      var history = new float[filter.Order];
      for (var n = 0; n < size; ++n) {
        var y = spectrum[idx];
        for (var j = 0; j < filter.Order; ++j)
          y -= filter.Coefficients[j] * history[j];
        for (var j = filter.Order - 1; j > 0; --j)
          history[j] = history[j - 1];
        history[0] = y;
        spectrum[idx] = y;
        idx += inc;
      }
    }
  }
}
