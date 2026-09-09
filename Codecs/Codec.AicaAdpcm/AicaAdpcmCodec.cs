#pragma warning disable CS1591
namespace Codec.AicaAdpcm;

/// <summary>
/// Yamaha AICA 4-bit ADPCM (Sega Dreamcast sound chip).
/// <para>
/// The codec is differential: every 4-bit code carries a 3-bit delta magnitude
/// plus a sign bit. The predictor starts at zero and the quantizer width at 127;
/// the width is multiplied after every sample by the AICA transition factor and
/// clamped to 127..24576. These are the values defined by the AICA FQ8005
/// Sound-block User's Manual, ADPCM tables 1 and 2.
/// </para>
/// <para>
/// A raw AICA sample packs two consecutive samples per byte, low nibble first.
/// The two-channel <see cref="Decode(ReadOnlySpan{byte}, int)"/> overload exists
/// for WAVE format tag 0x0020 (Yamaha ADPCM), whose stereo framing puts the left
/// sample in the low nibble and the right sample in the high nibble. Raw AICA
/// sample memory itself is one stream per AICA sound slot and therefore uses the
/// mono overload.
/// </para>
/// </summary>
public static class AicaAdpcmCodec {

  private static readonly int[] StepRate = [230, 230, 230, 230, 307, 409, 512, 614];

  private const int StepMin = 127;
  private const int StepMax = 24576;
  private const int InitialStep = 127;

  /// <summary>
  /// Decodes a raw mono AICA ADPCM byte stream to 16-bit PCM. Each input byte
  /// yields two samples, low nibble first.
  /// </summary>
  public static short[] Decode(ReadOnlySpan<byte> data) => Decode(data, channels: 1);

  /// <summary>
  /// Decodes Yamaha ADPCM using the WAVE 0x0020 byte layout.
  /// </summary>
  /// <remarks>
  /// Mono consumes both nibbles consecutively from one state. Stereo consumes
  /// the low nibble with the first channel state and the high nibble with the
  /// second channel state. Keeping this framing explicit is important: it is a
  /// container convention, not a claim that one raw AICA sample carries stereo.
  /// </remarks>
  public static short[] Decode(ReadOnlySpan<byte> data, int channels) {
    if (channels is < 1 or > 2)
      throw new ArgumentOutOfRangeException(nameof(channels), "Yamaha WAVE ADPCM carries one or two channels.");

    var output = new short[data.Length * 2];
    var predictor = new int[channels];
    var step = new int[channels];
    Array.Fill(step, InitialStep);

    var last = channels - 1;
    var o = 0;
    foreach (var b in data) {
      output[o++] = DecodeNibble((byte)(b & 0x0F), ref predictor[0], ref step[0]);
      output[o++] = DecodeNibble((byte)(b >> 4), ref predictor[last], ref step[last]);
    }

    return output;
  }

  /// <summary>
  /// Encodes 16-bit PCM to a raw mono AICA ADPCM byte stream. Two samples are
  /// packed per byte, low nibble first. An odd final sample is padded to a full
  /// byte; callers whose container stores an exact sample count can trim the
  /// decoded padding sample using that count.
  /// </summary>
  public static byte[] Encode(ReadOnlySpan<short> pcm) {
    var output = new byte[(pcm.Length + 1) / 2];
    var predictor = 0;
    var step = InitialStep;

    for (var i = 0; i < pcm.Length; i += 2) {
      var lo = EncodeNibble(pcm[i], ref predictor, ref step);
      var hi = i + 1 < pcm.Length ? EncodeNibble(pcm[i + 1], ref predictor, ref step) : (byte)0;
      output[i / 2] = (byte)((hi << 4) | lo);
    }

    return output;
  }

  private static short DecodeNibble(byte nibble, ref int predictor, ref int step) {
    var magnitude = nibble & 0x07;
    var diff = ((2 * magnitude + 1) * step) >> 3;
    predictor += (nibble & 8) != 0 ? -diff : diff;
    predictor = Math.Clamp(predictor, short.MinValue, short.MaxValue);
    step = Math.Clamp((step * StepRate[magnitude]) >> 8, StepMin, StepMax);
    return (short)predictor;
  }

  private static byte EncodeNibble(short sample, ref int predictor, ref int step) {
    var delta = (int)sample - predictor;
    var magnitude = Math.Min(7, Math.Abs(delta) * 4 / step);
    var nibble = (byte)(magnitude | (delta < 0 ? 8 : 0));

    // Advance through exactly the state transition the decoder applies. Table 1
    // selects magnitude at |delta| = n*step/4 boundaries; using reconstruction
    // midpoints here instead makes the writer self-consistent but not AICA-compliant.
    DecodeNibble(nibble, ref predictor, ref step);
    return nibble;
  }
}
