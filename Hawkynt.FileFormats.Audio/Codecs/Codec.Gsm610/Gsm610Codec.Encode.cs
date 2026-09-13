namespace Codec.Gsm610;

/// <summary>Configuration for GSM 06.10 full-rate encoding.</summary>
/// <param name="Channels">Number of independently encoded interleaved PCM channels.</param>
/// <param name="PadFinalFrame">Pad an incomplete final 20 ms frame with the last available sample.</param>
public sealed record Gsm610EncoderOptions(int Channels = 1, bool PadFinalFrame = true);

/// <summary>
/// Framing and channel handling for the GSM 06.10 encoder. The analysis itself is the
/// specification's fixed-point algorithm and lives beside the decoder in the companion
/// partial declaration, because both directions share one state object.
/// </summary>
public static partial class Gsm610Codec {

  /// <summary>
  /// Encodes interleaved PCM16 at 8 kHz to GSM 06.10 full-rate frames. GSM itself is a mono
  /// speech codec; multiple channels are encoded as independent 33-byte frames in channel order.
  /// </summary>
  public static byte[] Encode(ReadOnlySpan<short> pcm, Gsm610EncoderOptions? options = null) {
    options ??= new Gsm610EncoderOptions();
    if (options.Channels < 1)
      throw new ArgumentOutOfRangeException(nameof(options), "GSM channel count must be positive.");
    if (pcm.Length % options.Channels != 0)
      throw new ArgumentException("Interleaved PCM sample count must be a multiple of the channel count.", nameof(pcm));

    var inputFrames = pcm.Length / options.Channels;
    if (inputFrames == 0)
      return [];
    if (!options.PadFinalFrame && inputFrames % FrameSamples != 0)
      throw new ArgumentException($"PCM must contain whole {FrameSamples}-sample GSM frames when padding is disabled.", nameof(pcm));

    var groups = (inputFrames + FrameSamples - 1) / FrameSamples;
    var result = new byte[groups * options.Channels * FrameBytes];
    var encoders = new Gsm610State[options.Channels];
    for (var c = 0; c < encoders.Length; ++c)
      encoders[c] = new Gsm610State();

    Span<short> frame = stackalloc short[FrameSamples];
    for (var group = 0; group < groups; ++group) {
      var first = group * FrameSamples;
      var count = Math.Min(FrameSamples, inputFrames - first);
      for (var c = 0; c < options.Channels; ++c) {
        for (var i = 0; i < count; ++i)
          frame[i] = pcm[(first + i) * options.Channels + c];
        if (count < FrameSamples) {
          var pad = count > 0 ? frame[count - 1] : (short)0;
          frame[count..].Fill(pad);
        }

        var destination = result.AsSpan((group * options.Channels + c) * FrameBytes, FrameBytes);
        encoders[c].EncodeFrame(frame, destination);
      }
    }
    return result;
  }

  /// <summary>Encodes one mono PCM stream to the raw <c>.gsm</c> frame layout.</summary>
  public static byte[] EncodeRaw(ReadOnlySpan<short> pcm, bool padFinalFrame = true)
    => Encode(pcm, new Gsm610EncoderOptions(1, padFinalFrame));

  /// <summary>MSB-first bit writer over one frame.</summary>
  private ref struct BitWriter {
    private readonly Span<byte> _buffer;
    private int _bitPosition;

    public BitWriter(Span<byte> buffer) {
      this._buffer = buffer;
      this._bitPosition = 0;
    }

    public void Write(int value, int bitCount) {
      for (var bit = bitCount - 1; bit >= 0; --bit) {
        if (this._bitPosition >= this._buffer.Length * 8)
          throw new InvalidOperationException("GSM frame bit writer overflow.");
        if (((value >> bit) & 1) != 0)
          this._buffer[this._bitPosition >> 3] |= (byte)(1 << (7 - (this._bitPosition & 7)));
        ++this._bitPosition;
      }
    }
  }
}
