namespace Compression.Core.Audio;

/// <summary>
/// Thin back-compat shim over <see cref="Codec.Pcm.PcmCodec"/>. New code should call
/// <see cref="Codec.Pcm.PcmCodec"/> directly; this type is kept for one release cycle
/// so existing call-sites outside this repo don't break.
/// </summary>
public static class ChannelSplitter {
  /// <inheritdoc cref="Codec.Pcm.PcmCodec.LayoutNames"/>
  public static IReadOnlyList<string> LayoutNames(int channels)
    => Codec.Pcm.PcmCodec.LayoutNames(channels);

  /// <inheritdoc cref="Codec.Pcm.PcmCodec.SplitInterleavedPcm"/>
  public static IReadOnlyList<(string Name, byte[] WavBlob)> SplitInterleavedPcm(
      byte[] interleaved, int channels, int sampleRate, int bitsPerSample)
    => Codec.Pcm.PcmCodec.SplitInterleavedPcm(interleaved, channels, sampleRate, bitsPerSample);

  /// <inheritdoc cref="Codec.Pcm.PcmCodec.SplitPerChannelIntSamples"/>
  public static IReadOnlyList<(string Name, byte[] WavBlob)> SplitPerChannelIntSamples(
      int[][] perChannel, int sampleRate, int bitsPerSample)
    => Codec.Pcm.PcmCodec.SplitPerChannelIntSamples(perChannel, sampleRate, bitsPerSample);

  /// <inheritdoc cref="Codec.Pcm.PcmCodec.ToWavBlob"/>
  public static byte[] ToWavBlob(byte[] pcm, int channels, int sampleRate, int bitsPerSample, int formatCode = 1)
    => Codec.Pcm.PcmCodec.ToWavBlob(pcm, channels, sampleRate, bitsPerSample, formatCode);
}
