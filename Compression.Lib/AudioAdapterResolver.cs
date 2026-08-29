using Compression.Registry;

namespace Compression.Lib;

/// <summary>Central resolver for native and non-invasive audio conversion capabilities.</summary>
internal static class AudioAdapterResolver {
  private static readonly WavAudioAdapter Wav = new();
  private static readonly CafAudioAdapter Caf = new();
  private static readonly Ac3AudioAdapter Ac3 = new();
  private static readonly DtsAudioAdapter Dts = new();

  public static IAudioPcmSource? ResolvePcmSource(IFormatDescriptor descriptor)
    => descriptor as IAudioPcmSource ?? descriptor.Id switch {
      "Wav" => Wav,
      "Caf" => Caf,
      "Ac3" => Ac3,
      "Dts" => Dts,
      _ => AudioFormatAdapters.ResolvePcmSource(descriptor),
    };

  public static IAudioPcmTarget? ResolvePcmTarget(IFormatDescriptor descriptor)
    => descriptor as IAudioPcmTarget ?? descriptor.Id switch {
      "Wav" => Wav,
      "Caf" => Caf,
      "Ac3" => Ac3,
      "Dts" => Dts,
      _ => AudioFormatAdapters.ResolvePcmTarget(descriptor),
    };
}
