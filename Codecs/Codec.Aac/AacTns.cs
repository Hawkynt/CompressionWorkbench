#pragma warning disable CS1591

namespace Codec.Aac;

/// <summary>
/// One TNS filter for one window: a band-limited all-pole inverse prediction
/// filter applied across frequency (ISO/IEC 14496-3 §4.6.9).
/// </summary>
internal readonly record struct TnsFilter(int Window, int StartBin, int EndBin, int Order, bool Down, float[] Coefficients);

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

    // The filters of a window are transmitted from the top band downwards: each
    // one covers the `length` scale-factor bands below the previous filter's
    // start, and the covered range is clipped to the lower of max_sfb and the
    // rate-dependent TNS band limit (ISO/IEC 14496-3 Table 4.138).
    var bandLimit = Math.Min(ics.MaxSfb, ics.TnsMaxBands);

    for (var w = 0; w < numWindows; ++w) {
      var nFilt = (int)reader.ReadBits(nFiltBits);
      if (nFilt == 0) continue;
      var coefRes = (int)reader.ReadBits(1);
      var bottom = ics.NumSwb;
      for (var f = 0; f < nFilt; ++f) {
        var top = bottom;
        bottom = Math.Max(0, top - (int)reader.ReadBits(lengthBits));
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
        var startBin = ics.SwbOffset[Math.Min(bottom, bandLimit)];
        var endBin = ics.SwbOffset[Math.Min(top, bandLimit)];
        if (endBin <= startBin)
          continue;
        var lpc = DecodeCoefficients(coefs, coefRes == 1, coefCompress == 1, order);
        result.Filters.Add(new TnsFilter(w, startBin, endBin, order, direction, lpc));
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
  /// Applies in-place inverse TNS filtering to the spectral coefficients, each
  /// filter over the scale-factor-band range the bitstream assigned to it.
  /// </summary>
  public void Apply(float[] spectrum, IcsInfo ics) {
    if (this.IsEmpty) return;
    var windowBins = ics.IsEightShort ? AacFilterBank.ShortFrameSize : AacFilterBank.LongFrameSize;
    foreach (var filter in this.Filters) {
      if (filter.Order == 0) continue;
      var windowBase = filter.Window * windowBins;
      var size = filter.EndBin - filter.StartBin;
      if (size <= 0) continue;
      // direction 1 filters from the top of the region downwards.
      var inc = filter.Down ? -1 : 1;
      var idx = windowBase + (filter.Down ? filter.EndBin - 1 : filter.StartBin);
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
