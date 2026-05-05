namespace Codec.ALaw;

/// <summary>
/// G.711 A-law codec: 8-bit logarithmic samples decoded to 16-bit linear PCM.
/// European-style companding used in WAV format code 6 and AIFC <c>alaw</c>/<c>ALAW</c>.
/// </summary>
public static class ALawCodec {

  /// <summary>
  /// Decodes one A-law byte to a 16-bit signed linear sample. Based on the ITU-T G.711
  /// reference implementation (toggle every even bit, extract sign/exponent/mantissa,
  /// apply bias, then shift by exponent).
  /// </summary>
  public static short DecodeSample(byte a) {
    a ^= 0x55;
    // Bit 7 set (after XOR) indicates positive sample; cleared indicates negative
    // per ITU-T G.711 A-law convention.
    var positive = (a & 0x80) != 0;
    var exponent = (a >> 4) & 0x07;
    var mantissa = a & 0x0F;
    int sample;
    if (exponent == 0)
      sample = (mantissa << 4) + 8;
    else
      sample = ((mantissa << 4) + 0x108) << (exponent - 1);
    return positive ? (short)sample : (short)-sample;
  }

  /// <summary>
  /// Encodes one 16-bit signed linear sample to an A-law byte (used for test round-trip).
  /// </summary>
  public static byte EncodeSample(short pcm) {
    int sign;
    int absVal;
    // Positive samples get bit 7 set (pre-XOR); negative samples get bit 7 cleared.
    if (pcm < 0) { sign = 0x00; absVal = -pcm; }
    else { sign = 0x80; absVal = pcm; }
    if (absVal > 32635) absVal = 32635;
    int exponent, mantissa;
    if (absVal >= 256) {
      exponent = 7;
      for (var mask = 0x4000; (absVal & mask) == 0 && exponent > 0; mask >>= 1) --exponent;
      mantissa = (absVal >> (exponent + 3)) & 0x0F;
    } else {
      exponent = 0;
      mantissa = absVal >> 4;
    }
    return (byte)((sign | (exponent << 4) | mantissa) ^ 0x55);
  }

  /// <summary>
  /// Decodes a full A-law byte buffer to 16-bit linear PCM.
  /// </summary>
  public static short[] Decode(ReadOnlySpan<byte> alaw) {
    var pcm = new short[alaw.Length];
    for (var i = 0; i < alaw.Length; ++i) pcm[i] = DecodeSample(alaw[i]);
    return pcm;
  }

  /// <summary>
  /// Encodes a 16-bit linear PCM buffer to A-law bytes (used for tests).
  /// </summary>
  public static byte[] Encode(ReadOnlySpan<short> pcm) {
    var a = new byte[pcm.Length];
    for (var i = 0; i < pcm.Length; ++i) a[i] = EncodeSample(pcm[i]);
    return a;
  }
}
