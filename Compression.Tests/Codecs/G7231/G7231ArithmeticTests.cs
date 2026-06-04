#pragma warning disable CS1591
using Codec.G7231;

namespace Compression.Tests.Codecs.G7231;

/// <summary>
/// Hand-walked unit tests for the load-bearing G.723.1 fixed-point arithmetic: LSP VQ inverse
/// quantization (DC + 3-band codebook add-back) and the fixed-codebook pulse/grid unpack for both
/// the 6.3 kbit/s MP-MLQ and 5.3 kbit/s ACELP excitations. These pin the table indexing and the
/// integer math against values computed independently from the verbatim FFmpeg tables.
/// </summary>
[TestFixture]
public class G7231ArithmeticTests {

  // ── LSP VQ inverse quantization ────────────────────────────────────────────────────

  [Test]
  public void InverseQuant_ZeroIndices_YieldDcLsp() {
    // band entries [0] are all {0,...}; with prev_lsp == dc_lsp the predictive term is 0, so the
    // result is exactly the DC component (already monotonic → stability check is a no-op).
    var lsp = G7231Decoder.TestInverseQuant(0, 0, 0, badFrame: false);
    Assert.That(lsp, Is.EqualTo(G7231Tables.DcLsp));
  }

  [Test]
  public void InverseQuant_AddsBandVectorAndDcComponent() {
    // For a chosen index in each band, cur_lsp[i] = band[idx][k] + dc_lsp[i] (predictive term 0),
    // before the monotonic stability ordering. Use index 1 in each band.
    const int b0 = 1, b1 = 1, b2 = 1;
    var expected = new int[10];
    expected[0] = G7231Tables.LspBand0[b0 * 3 + 0] + G7231Tables.DcLsp[0];
    expected[1] = G7231Tables.LspBand0[b0 * 3 + 1] + G7231Tables.DcLsp[1];
    expected[2] = G7231Tables.LspBand0[b0 * 3 + 2] + G7231Tables.DcLsp[2];
    expected[3] = G7231Tables.LspBand1[b1 * 3 + 0] + G7231Tables.DcLsp[3];
    expected[4] = G7231Tables.LspBand1[b1 * 3 + 1] + G7231Tables.DcLsp[4];
    expected[5] = G7231Tables.LspBand1[b1 * 3 + 2] + G7231Tables.DcLsp[5];
    expected[6] = G7231Tables.LspBand2[b2 * 4 + 0] + G7231Tables.DcLsp[6];
    expected[7] = G7231Tables.LspBand2[b2 * 4 + 1] + G7231Tables.DcLsp[7];
    expected[8] = G7231Tables.LspBand2[b2 * 4 + 2] + G7231Tables.DcLsp[8];
    expected[9] = G7231Tables.LspBand2[b2 * 4 + 3] + G7231Tables.DcLsp[9];

    // Apply the same stability ordering the decoder uses so we compare the final vector.
    StabilityOrder(expected, minDist: 0x100);

    var lsp = G7231Decoder.TestInverseQuant(b0, b1, b2, badFrame: false);
    for (var i = 0; i < 10; ++i)
      Assert.That((int)lsp[i], Is.EqualTo(expected[i]), $"lsp[{i}]");
  }

  [Test]
  public void InverseQuant_BadFrame_ForcesZeroIndicesAndStaysStable() {
    // A bad frame zeroes the indices and uses the wider stability distance; the result must still
    // be a strictly-increasing (stable) LSP vector.
    var lsp = G7231Decoder.TestInverseQuant(50, 60, 70, badFrame: true);
    for (var i = 1; i < 10; ++i)
      Assert.That((int)lsp[i], Is.GreaterThanOrEqualTo((int)lsp[i - 1]), "LSPs must be ordered");
  }

  /// <summary>The decoder's in-place monotonic stability ordering (min_dist clamp), Section LSP.</summary>
  private static void StabilityOrder(int[] lsp, int minDist) {
    lsp[0] = Math.Max(lsp[0], 0x180);
    lsp[9] = Math.Min(lsp[9], 0x7e00);
    for (var j = 1; j < 10; ++j) {
      var temp = minDist + lsp[j - 1] - lsp[j];
      if (temp > 0) {
        temp >>= 1;
        lsp[j - 1] -= temp;
        lsp[j] += temp;
      }
    }
  }

  // ── 6.3 kbit/s MP-MLQ pulse/grid unpack ─────────────────────────────────────────────

  [Test]
  public void MpMlq_PulsePosBeyondCodebook_ProducesSilence() {
    // pulse_pos >= max_pos[index] is the documented "no pulses" guard.
    var vec = G7231Decoder.TestMpMlqExcitation(
      pulsePos: 593775, pulseSign: 0x3F, gridIndex: 0, ampIndex: 5, index: 0, diracTrain: 0, pitchLag: 60);
    Assert.That(vec, Is.All.EqualTo((short)0));
  }

  [Test]
  public void MpMlq_FirstCombinatorialPulse_PlacesGainOnGrid() {
    // index 0 uses j0 = PULSE_MAX - pulses[0] = 0. With pulse_pos = 0 the very first combinatorial
    // term (combinatorial[0][0]) is subtracted; pulse_pos 0 < that term → a pulse is placed at
    // grid position grid_index + 2*0 = 0, sign positive (pulse_sign bit clear), amp = gain[amp].
    var amp = 7;
    var expectedGain = G7231Tables.FixedCbGain[amp];
    var vec = G7231Decoder.TestMpMlqExcitation(
      pulsePos: 0, pulseSign: 0, gridIndex: 0, ampIndex: amp, index: 0, diracTrain: 0, pitchLag: 60);
    Assert.That((int)vec[0], Is.EqualTo(expectedGain), "first pulse on grid 0");
  }

  [Test]
  public void MpMlq_GridIndexShiftsPulsePosition() {
    // grid_index 1 offsets each placed pulse by one sample (grid_index + 2*i).
    var amp = 3;
    var vec = G7231Decoder.TestMpMlqExcitation(
      pulsePos: 0, pulseSign: 0, gridIndex: 1, ampIndex: amp, index: 0, diracTrain: 0, pitchLag: 60);
    Assert.That((int)vec[0], Is.EqualTo(0), "even grid slot is empty when grid_index = 1");
    Assert.That((int)vec[1], Is.EqualTo(G7231Tables.FixedCbGain[amp]), "pulse lands at grid_index 1");
  }

  // ── 5.3 kbit/s ACELP algebraic codebook unpack ──────────────────────────────────────

  [Test]
  public void Acelp_PlacesFourPulsesByPositionAndSign() {
    // The ACELP codebook places 4 pulses: offset = ((cb_pos & 7) << 3) + cb_shift + i for i in
    // {0,2,4,6}, each pulse ±cb_gain depending on the low cb_sign bit (consumed per pulse).
    var amp = 4;
    var gain = G7231Tables.FixedCbGain[amp];
    // cb_pos: low 3 bits per pulse give position groups 0,0,0,0; cb_sign all-ones → all +gain.
    // Use ad_cb_gain index 1 (its pitch_contrib lag computes < SUBFRAME_LEN-2 only for some
    // values); pick pitch_lag large so the harmonic-enhancement branch is skipped.
    var vec = G7231Decoder.TestAcelpExcitation(
      pulsePos: 0, pulseSign: 0xF, gridIndex: 0, ampIndex: amp, adCbGain: 0, adCbLag: 1, pitchLag: 80);

    // pos = ((0 & 7) << 3) + 0 + i = i for i in {0,2,4,6}; all signs bit-0 set → +gain.
    foreach (var i in new[] { 0, 2, 4, 6 })
      Assert.That((int)vec[i], Is.EqualTo(gain), $"ACELP pulse at offset {i}");
    Assert.That((int)vec[1], Is.EqualTo(0), "odd offsets stay empty");
  }

  [Test]
  public void Acelp_SignBitSelectsPolarity() {
    var amp = 4;
    var gain = G7231Tables.FixedCbGain[amp];
    // cb_sign = 0 → first pulse uses (cb_sign & 1)==0 → -gain.
    var vec = G7231Decoder.TestAcelpExcitation(
      pulsePos: 0, pulseSign: 0, gridIndex: 0, ampIndex: amp, adCbGain: 0, adCbLag: 1, pitchLag: 80);
    Assert.That((int)vec[0], Is.EqualTo(-gain), "sign bit clear → negative pulse");
  }
}
