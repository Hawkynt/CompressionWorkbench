namespace Codec.MuLaw;

/// <summary>
/// G.711 μ-law codec: 8-bit logarithmic samples decoded to 16-bit linear PCM.
/// The μ-law quantisation is symmetric around zero and uses a bias of 0x84 plus a
/// sign/exponent/mantissa encoding. This is the lossless inverse of the ITU-T G.711
/// reference algorithm.
/// </summary>
public static class MuLawCodec {

  private const short Bias = 0x84;

  /// <summary>
  /// Decodes one μ-law byte to a 16-bit signed linear sample.
  /// </summary>
  public static short DecodeSample(byte mu) {
    mu = (byte)~mu;
    var sign = (mu & 0x80) != 0;
    var exponent = (mu >> 4) & 0x07;
    var mantissa = mu & 0x0F;
    var sample = (short)(((mantissa << 3) + Bias) << exponent);
    sample -= Bias;
    return sign ? (short)-sample : sample;
  }

  /// <summary>
  /// Encodes one 16-bit signed linear sample to a μ-law byte (used for test round-trip).
  /// </summary>
  public static byte EncodeSample(short pcm) {
    const short Clip = 32635;
    var sign = (pcm >> 8) & 0x80;
    if (sign != 0) pcm = (short)-pcm;
    if (pcm > Clip) pcm = Clip;
    pcm = (short)(pcm + Bias);
    var exponent = 7;
    for (var mask = 0x4000; (pcm & mask) == 0 && exponent > 0; mask >>= 1) --exponent;
    var mantissa = (pcm >> (exponent + 3)) & 0x0F;
    return (byte)~(sign | (exponent << 4) | mantissa);
  }

  /// <summary>
  /// Decodes a full μ-law byte buffer to 16-bit linear PCM.
  /// </summary>
  public static short[] Decode(ReadOnlySpan<byte> mulaw) {
    var pcm = new short[mulaw.Length];
    for (var i = 0; i < mulaw.Length; ++i) pcm[i] = DecodeSample(mulaw[i]);
    return pcm;
  }

  /// <summary>
  /// Encodes a 16-bit linear PCM buffer to μ-law bytes (used for tests).
  /// </summary>
  public static byte[] Encode(ReadOnlySpan<short> pcm) {
    var mu = new byte[pcm.Length];
    for (var i = 0; i < pcm.Length; ++i) mu[i] = EncodeSample(pcm[i]);
    return mu;
  }
}
