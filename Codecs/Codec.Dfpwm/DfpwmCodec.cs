#pragma warning disable CS1591

namespace Codec.Dfpwm;

/// <summary>
/// DFPWM1a (Dynamic Filter Pulse Width Modulation, "1a" variant) codec — the
/// 1-bit-per-sample scheme used by ComputerCraft speakers. The decoder is ported
/// verbatim from ffmpeg <c>libavcodec/dfpwmdec.c</c>: a predictive charge
/// integrator with an adaptive strength and an anti-jerk plus first-order low-pass
/// output filter. Each input byte yields 8 unsigned-8 PCM samples, decoded
/// LSB-first. The encoder is the matching ffmpeg <c>dfpwmenc.c</c> algorithm so a
/// round-trip is stable.
/// <para>
/// DFPWM is headerless: callers must know the sample rate (ComputerCraft uses
/// 48000 Hz mono by convention) and channel count out of band.
/// </para>
/// </summary>
public static class DfpwmCodec {

  /// <summary>Low-pass coefficient passed to the decoder (ffmpeg uses 140).</summary>
  private const int Fs = 140;

  /// <summary>Default sample rate for raw DFPWM (ComputerCraft convention).</summary>
  public const int DefaultSampleRate = 48000;

  // ── Decode ───────────────────────────────────────────────────────────────

  /// <summary>
  /// Decodes raw DFPWM1a bytes to unsigned 8-bit PCM (one byte → 8 samples).
  /// The state machine matches ffmpeg's <c>au_decompress</c> exactly.
  /// </summary>
  public static byte[] Decompress(ReadOnlySpan<byte> dfpwm) {
    var output = new byte[dfpwm.Length * 8];

    // DFPWMState: fq (filtered charge), q (charge), s (strength), lt (last target).
    var fq = 0;
    var q = 0;
    var s = 0;
    var lt = -128;

    var outPos = 0;
    foreach (var inByte in dfpwm) {
      var d = (uint)inByte;
      for (var j = 0; j < 8; ++j) {
        var t = (d & 1) != 0 ? 127 : -128;
        d >>= 1;

        var nq = q + ((s * (t - q) + 512) >> 10);
        if (nq == q && nq != t)
          nq += t == 127 ? 1 : -1;
        var lq = q;
        q = nq;

        var st = t != lt ? 0 : 1023;
        var ns = s;
        if (ns != st)
          ns += st != 0 ? 1 : -1;
        if (ns < 8) ns = 8;
        s = ns;

        var ov = t != lt ? (nq + lq + 1) >> 1 : nq;

        fq += (Fs * (ov - fq) + 0x80) >> 8;
        ov = fq;

        output[outPos++] = (byte)(ov + 128);

        lt = t;
      }
    }

    return output;
  }

  // ── Encode ───────────────────────────────────────────────────────────────

  /// <summary>
  /// Encodes unsigned 8-bit PCM to DFPWM1a (8 samples → one byte). Mirrors ffmpeg's
  /// <c>dfpwm_enc</c>: the same predictive integrator with the anti-jerk handling,
  /// emitting one bit per sample LSB-first. A trailing partial byte is zero-padded.
  /// </summary>
  public static byte[] Compress(ReadOnlySpan<byte> pcmU8) {
    var output = new byte[(pcmU8.Length + 7) / 8];

    var q = 0;
    var s = 0;
    var lt = -128;

    for (var i = 0; i < pcmU8.Length; ++i) {
      var v = pcmU8[i] - 128; // back to signed centred sample
      var t = v > q || (v == q && v == 127) ? 127 : -128;
      var bit = t == 127 ? 1 : 0;

      var nq = q + ((s * (t - q) + 512) >> 10);
      if (nq == q && nq != t)
        nq += t == 127 ? 1 : -1;
      q = nq;

      var st = t != lt ? 0 : 1023;
      var ns = s;
      if (ns != st)
        ns += st != 0 ? 1 : -1;
      if (ns < 8) ns = 8;
      s = ns;

      lt = t;

      output[i >> 3] |= (byte)(bit << (i & 7));
    }

    return output;
  }
}
