#pragma warning disable CS1591
namespace Codec.AdpcmX;

/// <summary>
/// Nintendo GameCube THP ADPCM and the closely related fixed-table AFC variant.
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
  /// Fixed AFC predictor coefficient table, flattened as sixteen adjacent coefficient pairs.
  /// The values are format-defined and shared by all AFC streams.
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
  /// Decodes a THP channel using the per-channel coefficient table supplied by the container.
  /// </summary>
  public static short[] DecodeThp(ReadOnlySpan<byte> adpcm, ReadOnlySpan<short> coefs, int sampleCount) {
    if (coefs.Length < 16)
      throw new ArgumentException("THP needs 8 predictor pairs (short[16]).", nameof(coefs));
    return Decode(adpcm, coefs, sampleCount, ThpBytesPerFrame, indexMask: 0x07, indexFromHighNibble: true);
  }

  /// <summary>
  /// Decodes an AFC channel using the fixed <see cref="AfcCoefs"/> table. The header's low nibble
  /// selects the predictor pair and its high nibble is the residual scale exponent.
  /// </summary>
  public static short[] DecodeAfc(ReadOnlySpan<byte> adpcm, int sampleCount)
    => Decode(adpcm, AfcCoefs, sampleCount, AfcBytesPerFrame, indexMask: 0x0F, indexFromHighNibble: false);

  /// <summary>
  /// Encodes PCM16 into Nintendo AFC frames. For every 16-sample frame the encoder exhaustively
  /// evaluates all sixteen format-defined predictors and all sixteen residual exponents, then keeps
  /// the candidate with the lowest squared reconstruction error while feeding reconstructed samples
  /// back into the predictor exactly as <see cref="DecodeAfc"/> does.
  /// </summary>
  /// <remarks>
  /// AFC is lossy. This encoder deliberately derives its choices from the public reconstruction
  /// equation and the format-defined coefficient table rather than attempting bit parity with any
  /// particular Nintendo encoder.
  /// </remarks>
  public static byte[] EncodeAfc(ReadOnlySpan<short> pcm) {
    var frameCount = (pcm.Length + AfcSamplesPerFrame - 1) / AfcSamplesPerFrame;
    var result = new byte[frameCount * AfcBytesPerFrame];
    var hist1 = 0;
    var hist2 = 0;

    for (var frame = 0; frame < frameCount; ++frame) {
      var start = frame * AfcSamplesPerFrame;
      var count = Math.Min(AfcSamplesPerFrame, pcm.Length - start);
      var encoded = EncodeAfcFrame(pcm.Slice(start, count), hist1, hist2);
      var output = result.AsSpan(frame * AfcBytesPerFrame, AfcBytesPerFrame);
      output[0] = (byte)((encoded.Exponent << 4) | encoded.Predictor);
      for (var i = 0; i < encoded.Nibbles.Length; ++i) {
        ref var packed = ref output[1 + i / 2];
        if ((i & 1) == 0)
          packed = (byte)((encoded.Nibbles[i] & 0x0F) << 4);
        else
          packed |= (byte)(encoded.Nibbles[i] & 0x0F);
      }
      hist1 = encoded.Hist1;
      hist2 = encoded.Hist2;
    }

    return result;
  }

  private static (int Predictor, int Exponent, int[] Nibbles, int Hist1, int Hist2) EncodeAfcFrame(
      ReadOnlySpan<short> samples, int startHist1, int startHist2) {
    var bestPredictor = 0;
    var bestExponent = 0;
    long bestError = long.MaxValue;
    var bestNibbles = new int[samples.Length];
    var bestHist1 = startHist1;
    var bestHist2 = startHist2;

    for (var predictor = 0; predictor < 16; ++predictor) {
      var c1 = AfcCoefs[predictor * 2];
      var c2 = AfcCoefs[predictor * 2 + 1];
      for (var exponent = 0; exponent < 16; ++exponent) {
        var hist1 = startHist1;
        var hist2 = startHist2;
        var scale = 1 << exponent;
        long error = 0;
        var nibbles = new int[samples.Length];

        for (var i = 0; i < samples.Length; ++i) {
          var predicted = (c1 * hist1 + c2 * hist2) >> 11;
          var residual = samples[i] - predicted;
          var nibble = Math.Clamp((int)Math.Round((double)residual / scale, MidpointRounding.AwayFromZero), -8, 7);
          var reconstructed = ImaCore.Clamp16(predicted + nibble * scale);
          var delta = reconstructed - samples[i];
          error += (long)delta * delta;
          nibbles[i] = nibble & 0x0F;
          hist2 = hist1;
          hist1 = reconstructed;
        }

        if (error >= bestError)
          continue;

        bestError = error;
        bestPredictor = predictor;
        bestExponent = exponent;
        bestNibbles = nibbles;
        bestHist1 = hist1;
        bestHist2 = hist2;
        if (error == 0)
          break;
      }
      if (bestError == 0)
        break;
    }

    return (bestPredictor, bestExponent, bestNibbles, bestHist1, bestHist2);
  }

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
