#pragma warning disable CS1591
namespace Codec.AdpcmX;

/// <summary>
/// Nintendo GameCube THP ADPCM (ffmpeg <c>adpcm_thp</c> / <c>adpcm_thp_le</c>) and the closely
/// related fixed-table AFC variant (ffmpeg <c>adpcm_afc</c>).
/// <para>
/// Both reconstruct a sample as <c>out = clip16(((c1*hist1 + c2*hist2) &gt;&gt; 11) + (signNibble &lt;&lt; exp))</c>:
/// the second-order predictor contribution is the only term shifted right by 11, the scaled
/// residual is added on top, and the two histories shift forward. This is the canonical Nintendo
/// DSP family (the same predictor pairs as <c>Codec.DspAdpcm</c>) — the variants differ only in
/// frame layout and where the coefficient pairs come from.
/// </para>
/// <list type="bullet">
///   <item><b>THP</b> — 8-byte frame (1 header + 7 data = 14 samples). Header high nibble (&amp; 7)
///         selects a predictor pair from the per-channel <c>short[16]</c> table supplied by the
///         container, low nibble is the scale exponent. THP_LE only changes how the container's
///         coefficients are byte-ordered, so once decoded to <c>short[]</c> the math is identical.</item>
///   <item><b>AFC</b> — 9-byte frame (1 header + 8 data = 16 samples). Header high nibble is the
///         scale exponent, low nibble (0..15) indexes the fixed 16-pair <see cref="AfcCoefs"/>
///         table; there is no per-stream table.</item>
/// </list>
/// </summary>
public static class Thp {

  /// <summary>Bytes per THP frame (1 header + 7 data = 14 samples).</summary>
  public const int ThpBytesPerFrame = 8;

  /// <summary>Samples per THP frame.</summary>
  public const int ThpSamplesPerFrame = 14;

  /// <summary>Bytes per AFC frame (1 header + 8 data = 16 samples).</summary>
  public const int AfcBytesPerFrame = 9;

  /// <summary>Samples per AFC frame.</summary>
  public const int AfcSamplesPerFrame = 16;

  /// <summary>
  /// The fixed AFC predictor coefficient table (ffmpeg <c>afc_coeffs[2][16]</c>) flattened into
  /// the DSP <c>short[16*2]</c> layout: <c>AfcCoefs[2*i]</c> = factor1, <c>AfcCoefs[2*i+1]</c> =
  /// factor2 for index <c>i</c> (0..15). ffmpeg stores two parallel rows of sixteen; the rows are
  /// interleaved here so the AFC index selects an adjacent pair.
  /// </summary>
  public static readonly short[] AfcCoefs = [
    0, 0,
    2048, 0,
    0, 2048,
    1024, 1024,
    4096, -2048,
    3584, -1536,
    3072, -1024,
    4608, -2560,
    4200, -2248,
    4800, -2300,
    5120, -3072,
    2048, -2048,
    1024, -1024,
    -1024, 1024,
    -1024, 0,
    -2048, 0,
  ];

  /// <summary>
  /// Decodes a THP channel using the per-channel coefficient table supplied by the container
  /// (already byte-ordered into native <c>short[16]</c>, so this serves both THP and THP_LE). The
  /// header's high nibble (masked to 0..7) selects the predictor pair, the low nibble the exponent.
  /// </summary>
  public static short[] DecodeThp(ReadOnlySpan<byte> adpcm, ReadOnlySpan<short> coefs, int sampleCount) {
    if (coefs.Length < 16)
      throw new ArgumentException("THP needs 8 predictor pairs (short[16]).", nameof(coefs));
    return Decode(adpcm, coefs, sampleCount, ThpBytesPerFrame, indexMask: 0x07, indexFromHighNibble: true);
  }

  /// <summary>
  /// Decodes an AFC channel using the fixed <see cref="AfcCoefs"/> table. The header's low nibble
  /// (0..15) indexes the table; the high nibble is the scale exponent.
  /// </summary>
  public static short[] DecodeAfc(ReadOnlySpan<byte> adpcm, int sampleCount)
    => Decode(adpcm, AfcCoefs, sampleCount, AfcBytesPerFrame, indexMask: 0x0F, indexFromHighNibble: false);

  // Shared THP/AFC reconstruction. For THP the predictor index is the high nibble (&7) and the
  // exponent the low nibble; for AFC those roles are swapped. The body is (bytesPerFrame-1)*2
  // nibbles, HIGH nibble of each byte first.
  private static short[] Decode(ReadOnlySpan<byte> adpcm, ReadOnlySpan<short> coefs, int sampleCount,
                                int bytesPerFrame, int indexMask, bool indexFromHighNibble) {
    if (sampleCount < 0)
      throw new ArgumentOutOfRangeException(nameof(sampleCount));

    var output = new short[sampleCount];
    var produced = 0;
    var hist1 = 0;
    var hist2 = 0;
    var pos = 0;
    var dataBytes = bytesPerFrame - 1;

    while (produced < sampleCount && pos + bytesPerFrame <= adpcm.Length) {
      var header = adpcm[pos];
      int index, exp;
      if (indexFromHighNibble) {
        index = (header >> 4) & indexMask;
        exp = header & 0x0F;
      } else {
        exp = (header >> 4) & 0x0F;
        index = header & indexMask;
      }
      var c1 = coefs[2 * index];
      var c2 = coefs[2 * index + 1];

      for (var b = 0; b < dataBytes && produced < sampleCount; ++b) {
        var dataByte = adpcm[pos + 1 + b];
        for (var n = 0; n < 2 && produced < sampleCount; ++n) {
          var nibble = n == 0 ? (dataByte >> 4) & 0x0F : dataByte & 0x0F;
          var s = ImaCore.SignExtend4(nibble);
          var sample = ImaCore.Clamp16(((c1 * hist1 + c2 * hist2) >> 11) + (s << exp));
          output[produced++] = (short)sample;
          hist2 = hist1;
          hist1 = sample;
        }
      }
      pos += bytesPerFrame;
    }

    return output;
  }
}
