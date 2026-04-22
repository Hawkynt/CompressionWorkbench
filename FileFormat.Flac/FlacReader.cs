#pragma warning disable CS1591
namespace FileFormat.Flac;

/// <summary>
/// Thin back-compat shim over <see cref="Codec.Flac.FlacCodec"/>. New code should call
/// the codec class directly; this wrapper stays for one release cycle so existing
/// callers don't break.
/// </summary>
public static class FlacReader {
  /// <inheritdoc cref="Codec.Flac.FlacCodec.Decompress"/>
  public static void Decompress(Stream input, Stream output)
    => Codec.Flac.FlacCodec.Decompress(input, output);

  /// <summary>Back-compat mirror of <see cref="Codec.Flac.FlacCodec.AudioProperties"/>.</summary>
  public readonly record struct AudioProperties(int SampleRate, int Channels, int BitsPerSample, long TotalSamples);

  /// <inheritdoc cref="Codec.Flac.FlacCodec.ReadAudioProperties"/>
  public static AudioProperties ReadAudioProperties(ReadOnlySpan<byte> flacBytes) {
    var p = Codec.Flac.FlacCodec.ReadAudioProperties(flacBytes);
    return new AudioProperties(p.SampleRate, p.Channels, p.BitsPerSample, p.TotalSamples);
  }
}
