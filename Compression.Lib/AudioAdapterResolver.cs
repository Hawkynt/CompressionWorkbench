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

  public static IAudioDemuxSource? ResolveDemuxSource(IFormatDescriptor descriptor)
    => descriptor as IAudioDemuxSource ?? descriptor.Id switch {
      "Wav" => G711PacketAdapter.Wav,
      "Aiff" => G711PacketAdapter.Aiff,
      "Au" => G711PacketAdapter.Au,
      "Caf" => Caf,
      "Mp3" => Mp3AudioPacketAdapter.Instance,
      "WavPack" => WavPackAudioPacketAdapter.Instance,
      _ => null,
    };

  public static IAudioMuxTarget? ResolveMuxTarget(IFormatDescriptor descriptor)
    => descriptor as IAudioMuxTarget ?? descriptor.Id switch {
      "Wav" => G711PacketAdapter.Wav,
      "Aiff" => G711PacketAdapter.Aiff,
      "Au" => G711PacketAdapter.Au,
      "Caf" => Caf,
      "Mp3" => Mp3AudioPacketAdapter.Instance,
      "WavPack" => WavPackAudioPacketAdapter.Instance,
      _ => null,
    };

  public static IArchiveCreatable? ResolvePseudoArchiveTarget(IFormatDescriptor descriptor)
    => descriptor is IArchiveCreatable creator &&
       (descriptor.Category == FormatCategory.Audio || descriptor is IAudioContainerFormat)
      ? creator
      : null;
}
