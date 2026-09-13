#pragma warning disable CS1591

namespace Codec.Opus;

/// <summary>
/// SILK decoder entry point — linear-predictive speech codec path used for
/// narrowband / medium-band / wideband Opus configs (0-11).
/// <para>
/// <b>Status:</b> scaffolding only. <see cref="DecodeFrame"/> throws
/// <see cref="NotSupportedException"/>; <see cref="OpusCodec.Decompress"/>
/// emits silence for the correct sample count. Full pipeline (LTP + LSF →
/// LPC synthesis + excitation decoding + stereo unmixing +
/// <see cref="OpusResampler"/> up-conversion to 48 kHz) is a follow-up wave.
/// </para>
/// </summary>
public sealed class OpusSilk {

  /// <summary>Creates a SILK decoder for the given output channels + sample rate.</summary>
  public OpusSilk(int channels, int internalSampleRate) {
    this.Channels = channels;
    this.InternalSampleRate = internalSampleRate;
  }

  /// <summary>Output channel count (1 or 2).</summary>
  public int Channels { get; }

  /// <summary>SILK's internal sample rate (8 / 12 / 16 kHz).</summary>
  public int InternalSampleRate { get; }

  /// <summary>
  /// Decodes one SILK frame. Not implemented in this pass.
  /// </summary>
  public void DecodeFrame(ReadOnlySpan<byte> frame, Span<float> pcmOut)
    => throw new NotSupportedException(
      "SILK LPC/LTP synthesis is not yet wired in this build. " +
      "Only TOC parsing + Ogg framing are production-ready for SILK configs. " +
      "Use OpusCodec.Decompress for silence-scaffolded output, or OpusCodec.ReadStreamInfo for metadata.");
}
