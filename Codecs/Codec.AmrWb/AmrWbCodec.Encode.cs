#pragma warning disable CS1591

using SipLib.Media;

namespace Codec.AmrWb;

/// <summary>AMR-WB encoder controls.</summary>
/// <param name="Mode">One of the nine active AMR-WB speech modes.</param>
/// <param name="EnableDtx">Enable VAD/DTX so silence may be encoded as SID/NO_DATA frames.</param>
/// <param name="PadFinalFrame">Pad an incomplete 320-sample final frame with the last input sample.</param>
public sealed record AmrWbEncoderOptions(
  AmrWbMode Mode = AmrWbMode.Mr1265,
  bool EnableDtx = false,
  bool PadFinalFrame = true
);

public static partial class AmrWbCodec {

  /// <summary>
  /// Encodes mono PCM16 at 16 kHz into concatenated AMR-WB storage/MIME frames. Each input frame
  /// spans 20 ms (320 samples). The underlying encoder is the BSD-3-Clause, pure-managed 3GPP
  /// fixed-point port shipped by SipLib; no native codec library is invoked.
  /// </summary>
  public static byte[] Encode(ReadOnlySpan<short> pcm, AmrWbEncoderOptions? options = null) {
    options ??= new AmrWbEncoderOptions();
    if (options.Mode is < AmrWbMode.Mr660 or > AmrWbMode.Mr2385)
      throw new ArgumentOutOfRangeException(nameof(options), "AMR-WB encoding mode must be one of the nine active speech modes (0-8).");
    if (pcm.Length == 0)
      return [];
    if (!options.PadFinalFrame && pcm.Length % SamplesPerFrame != 0)
      throw new ArgumentException($"AMR-WB PCM must contain whole {SamplesPerFrame}-sample frames when padding is disabled.", nameof(pcm));

    var frameCount = (pcm.Length + SamplesPerFrame - 1) / SamplesPerFrame;
    using var output = new MemoryStream();
    var encoder = new AmrWbEncoder((int)options.Mode, options.EnableDtx);
    try {
      var frame = new short[SamplesPerFrame];
      for (var f = 0; f < frameCount; ++f) {
        var offset = f * SamplesPerFrame;
        var count = Math.Min(SamplesPerFrame, pcm.Length - offset);
        pcm.Slice(offset, count).CopyTo(frame);
        if (count < SamplesPerFrame)
          Array.Fill(frame, count > 0 ? frame[count - 1] : (short)0, count, SamplesPerFrame - count);

        var rfc4867 = encoder.Encode(frame);
        if (rfc4867.Length < 2)
          throw new InvalidDataException("Managed AMR-WB encoder returned a truncated RFC 4867 payload.");

        // SipLib emits RFC 4867 octet-aligned RTP payloads: CMR byte + one ToC byte + speech bits.
        // The codec project consumes the file/storage layout, which is exactly ToC + speech bits.
        output.Write(rfc4867, 1, rfc4867.Length - 1);
      }
    } finally {
      encoder.CloseEncoder();
    }
    return output.ToArray();
  }
}
