#pragma warning disable CS1591
namespace Codec.Brr;

/// <summary>
/// Nintendo SNES S-DSP BRR (Bit Rate Reduction) encoder and decoder. The S-DSP plays
/// audio in fixed 9-byte blocks, each yielding 16 mono 16-bit samples:
/// <list type="bullet">
///   <item>byte 0 — header: high nibble = <c>range</c> (shift amount, valid 0..12),
///     bits 2..3 = <c>filter</c> (0..3), bit 1 = loop flag, bit 0 = end flag.</item>
///   <item>bytes 1..8 — 16 signed 4-bit nibbles, HIGH nibble of each byte first.</item>
/// </list>
/// Each nibble <c>n</c> is sign-extended to -8..7. For a valid <c>range &lt;= 12</c> the
/// scaled value is <c>v = (s &lt;&lt; range) &gt;&gt; 1</c>; for the invalid ranges 13..15 the
/// hardware effectively discards the shift and contributes <c>v = s &gt;&gt; 4</c> (so a
/// negative nibble yields -1, everything else 0). A second-order predictor based on the
/// two previous reconstructed samples is then added (integer math, arithmetic-shift floor):
/// <list type="bullet">
///   <item>filter 0: + 0</item>
///   <item>filter 1: + h1 * 15 / 16</item>
///   <item>filter 2: + h1 * 61 / 32 − h2 * 15 / 16</item>
///   <item>filter 3: + h1 * 115 / 64 − h2 * 13 / 16</item>
/// </list>
/// The result is clamped to 16 bits and then wrapped to 15 bits exactly as the S-DSP does
/// (<c>sample = (short)(v &lt;&lt; 1) &gt;&gt; 1</c>), so a value that overflows the 15-bit range
/// folds rather than saturates. The wrapped sample feeds the history for the next nibble.
/// </summary>
public static class BrrCodec {

  /// <summary>Size in bytes of one BRR block (1 header byte + 8 data bytes).</summary>
  public const int BlockSize = 9;

  /// <summary>Number of PCM samples carried by one BRR block.</summary>
  public const int SamplesPerBlock = 16;

  /// <summary>Highest legal range (shift) value; 13..15 are treated as the invalid case.</summary>
  public const int MaxRange = 12;

  // ── decode ────────────────────────────────────────────────────────────────

  /// <summary>
  /// Decodes a BRR stream to 16-bit PCM. Decoding stops after the first block whose end
  /// flag is set, or when the input runs out of whole 9-byte blocks (a trailing partial
  /// block is ignored). The predictor history starts at zero.
  /// </summary>
  public static short[] Decode(ReadOnlySpan<byte> blocks) {
    var blockCount = blocks.Length / BlockSize;
    if (blockCount == 0)
      return [];

    var output = new short[blockCount * SamplesPerBlock];
    var produced = 0;
    var hist1 = 0;
    var hist2 = 0;

    for (var b = 0; b < blockCount; ++b) {
      var offset = b * BlockSize;
      var header = blocks[offset];
      var range = (header >> 4) & 0x0F;
      var filter = (header >> 2) & 0x03;
      var end = (header & 0x01) != 0;

      for (var i = 0; i < SamplesPerBlock; ++i) {
        var raw = blocks[offset + 1 + (i >> 1)];
        // HIGH nibble first.
        var nibble = (i & 1) == 0 ? raw >> 4 : raw & 0x0F;
        var sample = DecodeSample(SignExtend4(nibble), range, filter, ref hist1, ref hist2);
        output[produced++] = (short)sample;
      }

      if (end)
        break;
    }

    return produced == output.Length ? output : output[..produced];
  }

  /// <summary>
  /// Reconstructs one sample from a sign-extended nibble, applies the predictor for the
  /// given <paramref name="filter"/>, performs the 16-bit clamp and 15-bit wrap, and rolls
  /// the history forward. Shared by <see cref="Decode"/> and the encoder's trial loop so
  /// both reproduce the hardware path identically.
  /// </summary>
  private static int DecodeSample(int s, int range, int filter, ref int hist1, ref int hist2) {
    int v;
    if (range <= MaxRange)
      v = (s << range) >> 1;
    else
      // Invalid range (13..15): the shift is effectively dropped; only the sign survives.
      v = s >> 4; // -8..-1 → -1, 0..7 → 0

    switch (filter) {
      case 1:
        v += hist1 * 15 / 16;
        break;
      case 2:
        v += hist1 * 61 / 32 - hist2 * 15 / 16;
        break;
      case 3:
        v += hist1 * 115 / 64 - hist2 * 13 / 16;
        break;
      // filter 0: no predictor term.
    }

    v = Clamp16(v);
    // 15-bit wrap: keep the low 15 bits as a signed value (S-DSP behaviour).
    var wrapped = (short)(v << 1) >> 1;

    hist2 = hist1;
    hist1 = wrapped;
    return wrapped;
  }

  // ── encode ──────────────────────────────────────────────────────────────────

  /// <summary>
  /// Encodes mono 16-bit PCM into BRR blocks. Each group of <see cref="SamplesPerBlock"/>
  /// samples is encoded by brute-forcing every filter (0..3) and every legal range (0..12)
  /// and keeping the combination with the lowest reconstruction error, exactly tracking the
  /// decoder's history so playback matches. The final sample group is zero-padded to a full
  /// block; the last emitted block carries the end flag (and the loop flag is left clear).
  /// </summary>
  public static byte[] Encode(ReadOnlySpan<short> pcm) {
    var blockCount = (pcm.Length + SamplesPerBlock - 1) / SamplesPerBlock;
    if (blockCount == 0)
      return [];

    var output = new byte[blockCount * BlockSize];

    var hist1 = 0;
    var hist2 = 0;

    Span<short> source = stackalloc short[SamplesPerBlock];
    Span<int> bestNibbles = stackalloc int[SamplesPerBlock];
    Span<int> tryNibbles = stackalloc int[SamplesPerBlock];

    for (var b = 0; b < blockCount; ++b) {
      var srcStart = b * SamplesPerBlock;
      for (var i = 0; i < SamplesPerBlock; ++i) {
        var idx = srcStart + i;
        source[i] = idx < pcm.Length ? pcm[idx] : (short)0;
      }

      var bestError = long.MaxValue;
      var bestRange = 0;
      var bestFilter = 0;
      var bestHist1 = hist1;
      var bestHist2 = hist2;

      for (var filter = 0; filter < 4; ++filter) {
        for (var range = 0; range <= MaxRange; ++range) {
          var h1 = hist1;
          var h2 = hist2;
          long error = 0;

          for (var i = 0; i < SamplesPerBlock; ++i) {
            // Predictor contribution for this filter from the current history.
            var predicted = Predict(filter, h1, h2);
            // The decoder computes v = (s << range) >> 1 + predicted; invert for the ideal s.
            var target = source[i] - predicted;
            // s ≈ (target * 2) >> range, rounded, clamped to the 4-bit signed range.
            var scaled = range <= MaxRange ? RoundShift(target << 1, range) : 0;
            if (scaled > 7) scaled = 7;
            else if (scaled < -8) scaled = -8;
            tryNibbles[i] = scaled & 0x0F;

            var reconstructed = DecodeSample(scaled, range, filter, ref h1, ref h2);
            var diff = (long)reconstructed - source[i];
            error += diff * diff;
          }

          if (error >= bestError)
            continue;

          bestError = error;
          bestRange = range;
          bestFilter = filter;
          bestHist1 = h1;
          bestHist2 = h2;
          tryNibbles.CopyTo(bestNibbles);
          if (error == 0)
            break; // exact fit for this filter; no better range
        }
      }

      var blockStart = b * BlockSize;
      var endFlag = b == blockCount - 1 ? 0x01 : 0x00;
      output[blockStart] = (byte)((bestRange << 4) | (bestFilter << 2) | endFlag);
      for (var i = 0; i < SamplesPerBlock; i += 2)
        output[blockStart + 1 + (i >> 1)] = (byte)((bestNibbles[i] << 4) | bestNibbles[i + 1]);

      hist1 = bestHist1;
      hist2 = bestHist2;
    }

    return output;
  }

  /// <summary>Predictor term for a filter from the two history samples (matches <see cref="DecodeSample"/>).</summary>
  private static int Predict(int filter, int hist1, int hist2) => filter switch {
    1 => hist1 * 15 / 16,
    2 => hist1 * 61 / 32 - hist2 * 15 / 16,
    3 => hist1 * 115 / 64 - hist2 * 13 / 16,
    _ => 0,
  };

  /// <summary>Arithmetic right shift with round-to-nearest (away from zero on the half).</summary>
  private static int RoundShift(int value, int shift) {
    if (shift <= 0)
      return value;
    var half = 1 << (shift - 1);
    return (value + half) >> shift;
  }

  /// <summary>Sign-extends a 4-bit value (0..15) to the signed range -8..7.</summary>
  private static int SignExtend4(int nibble) => (nibble & 0x08) != 0 ? nibble - 16 : nibble;

  private static int Clamp16(int value) => value > 32767 ? 32767 : value < -32768 ? -32768 : value;
}
