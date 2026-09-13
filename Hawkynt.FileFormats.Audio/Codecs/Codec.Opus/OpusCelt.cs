#pragma warning disable CS1591

namespace Codec.Opus;

/// <summary>
/// CELT decoder entry point — transforms quantised MDCT coefficients + pitch
/// prediction back into PCM samples.
/// <para>
/// <b>Status:</b> scaffolding only. The bitstream entry point
/// <see cref="DecodeFrame"/> currently throws <see cref="NotSupportedException"/>;
/// the <see cref="OpusCodec.Decompress"/> wrapper bypasses it and emits silence
/// so that Ogg framing + TOC + packet counts round-trip correctly. Subsequent
/// waves land the full pipeline:
/// <list type="bullet">
///   <item>Silence decision + post-filter state</item>
///   <item>Spread / tapset / tf changes</item>
///   <item>Coarse + fine energy (coarse delta-coded Laplace, fine uniform)</item>
///   <item>PVQ (vector quantisation of residual) + stereo mid-side</item>
///   <item>Anti-collapse + denormalisation</item>
///   <item>Inverse MDCT with Kaiser-Bessel-Derived window + overlap-add</item>
/// </list>
/// </para>
/// </summary>
public sealed class OpusCelt {

  /// <summary>Creates a new CELT decoder for the given output channels / frame size.</summary>
  public OpusCelt(int channels, int frameSamplesAt48k) {
    this.Channels = channels;
    this.FrameSamples = frameSamplesAt48k;
  }

  /// <summary>Number of output channels (1 or 2).</summary>
  public int Channels { get; }

  /// <summary>Frame size in samples at 48 kHz.</summary>
  public int FrameSamples { get; }

  /// <summary>
  /// Decodes one CELT frame from <paramref name="frame"/> into
  /// <paramref name="pcmOut"/> (interleaved float). Not implemented in this pass.
  /// </summary>
  public void DecodeFrame(ReadOnlySpan<byte> frame, Span<float> pcmOut)
    => throw new NotSupportedException(
      "CELT inverse-transform path is not yet wired in this build. " +
      "Only Ogg framing + TOC + packet splitting are production-ready. " +
      "Use OpusCodec.Decompress for silence-scaffolded output, or OpusCodec.ReadStreamInfo for metadata.");
}
