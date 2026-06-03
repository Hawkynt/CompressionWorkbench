#pragma warning disable CS1591
namespace Codec.Cvsd;

/// <summary>
/// Continuously-variable-slope delta modulation (CVSD), the Bluetooth SCO / MIL-STD-188-113
/// style 1-bit-per-sample voice codec. Each sample is encoded as a single bit: a delta-sigma
/// comparator emits 1 when the input rises above the local reconstruction and 0 otherwise.
/// <para>
/// The slope (step size) is syllabically companded: when the last <see cref="RunLength"/>
/// output bits are all equal — i.e. the modulator is in slope overload, unable to keep up —
/// the step grows by <see cref="StepDecay"/> toward <see cref="MaxStep"/>; otherwise it
/// decays geometrically toward <see cref="MinStep"/>. The reconstruction integrator leaks by
/// a factor of 1023/1024 each sample and accumulates ±step, clamped to the 16-bit range. The
/// decoder runs the identical integrator/step machine; the encoder reuses it as its local
/// feedback path, so encode→decode reconstructs the slowly-varying envelope of the signal.
/// </para>
/// The bit-rate convention is 64 kbit/s (Bluetooth SCO), i.e. one bit per 64 kHz sample.
/// </summary>
public static class CvsdCodec {

  /// <summary>Consecutive equal bits (J of K, here 3 of 3) that signal slope overload.</summary>
  private const int RunLength = 3;

  /// <summary>Minimum integrator step (slope floor).</summary>
  private const int MinStep = 10;

  /// <summary>Maximum integrator step (slope ceiling).</summary>
  private const int MaxStep = 1280;

  /// <summary>Additive step growth applied per sample while in slope overload.</summary>
  private const int StepDecay = 10;

  /// <summary>Integrator clamp (16-bit signed reconstruction range).</summary>
  private const int AccumulatorClamp = 32767;

  // ── Shared integrator + syllabic-companding state for both directions. ───────────
  private sealed class State {
    public int Accumulator;
    public int Step = MinStep;
    public int History;     // Rolling window of the last RunLength output bits.
    public int Filled;      // How many bits the window has seen (caps the overload test).

    /// <summary>Advances the integrator and step for one decoded/encoded bit.</summary>
    public int Step1(int bit) {
      // Syllabic companding: a full run of equal bits means slope overload → grow the step,
      // otherwise relax geometrically toward the floor.
      this.History = ((this.History << 1) | bit) & ((1 << RunLength) - 1);
      if (this.Filled < RunLength)
        ++this.Filled;

      var overload = this.Filled >= RunLength &&
        (this.History == 0 || this.History == (1 << RunLength) - 1);
      if (overload)
        this.Step = Math.Min(this.Step + StepDecay, MaxStep);
      else
        this.Step = Math.Max(this.Step - (this.Step >> 5), MinStep);

      // Leaky integrator: decay toward zero, then add the signed step.
      this.Accumulator -= this.Accumulator >> 10;            // ×(1023/1024)
      this.Accumulator += bit == 1 ? this.Step : -this.Step;
      this.Accumulator = Math.Clamp(this.Accumulator, -AccumulatorClamp, AccumulatorClamp);
      return this.Accumulator;
    }
  }

  /// <summary>
  /// Decodes a CVSD bitstream to 16-bit linear PCM, one sample per bit. Bits are read
  /// MSB-first within each byte when <paramref name="msbFirst"/> is <see langword="true"/>
  /// (the common convention), LSB-first otherwise.
  /// </summary>
  public static short[] Decode(ReadOnlySpan<byte> data, bool msbFirst = true) {
    var s = new State();
    var output = new short[data.Length * 8];
    var n = 0;
    foreach (var b in data)
      for (var k = 0; k < 8; ++k) {
        var bit = msbFirst ? (b >> (7 - k)) & 1 : (b >> k) & 1;
        output[n++] = (short)s.Step1(bit);
      }
    return output;
  }

  /// <summary>
  /// Encodes 16-bit linear PCM to a CVSD bitstream, one bit per sample, packed MSB-first
  /// within each byte when <paramref name="msbFirst"/> is <see langword="true"/>. The final
  /// byte is zero-padded in its unused low (or high) bits. The comparator decides each bit by
  /// comparing the input against the local reconstruction, which is updated identically to
  /// <see cref="Decode"/>.
  /// </summary>
  public static byte[] Encode(ReadOnlySpan<short> pcm, bool msbFirst = true) {
    var s = new State();
    var bytes = new byte[(pcm.Length + 7) / 8];
    for (var i = 0; i < pcm.Length; ++i) {
      var bit = pcm[i] >= s.Accumulator ? 1 : 0;
      s.Step1(bit);
      if (bit == 1) {
        var shift = msbFirst ? 7 - (i & 7) : i & 7;
        bytes[i >> 3] |= (byte)(1 << shift);
      }
    }
    return bytes;
  }
}
