using Compression.Registry;
using FileFormat.Asf;

namespace Compression.Lib;

/// <summary>Central resolver for native and non-invasive audio conversion capabilities.</summary>
internal static class AudioAdapterResolver {
  private static readonly WavAudioAdapter Wav = new();
  private static readonly CafAudioAdapter Caf = new();
  private static readonly Ac3AudioAdapter Ac3 = new();
  private static readonly DtsAudioAdapter Dts = new();
  // Two ASF adapters, deliberately. FileFormat.Asf.AsfAudioAdapter is the packet-preserving
  // demux/mux route: it derives presentation time and duration from the codec's own granule
  // positions and honours the container preroll. Compression.Lib.AsfAudioAdapter adds the PCM
  // encode/decode routes this package needs and times objects from the declared byte rate, which
  // a VBR stream does not carry. Each is bound only to the routes it is right for; the unqualified
  // name would silently resolve to the same-namespace one for all four.
  private static readonly FileFormat.Asf.AsfAudioAdapter AsfPackets = FileFormat.Asf.AsfAudioAdapter.Instance;
  private static readonly AsfAudioAdapter AsfPcm = AsfAudioAdapter.Instance;

  public static IAudioPcmSource? ResolvePcmSource(IFormatDescriptor descriptor)
    => descriptor as IAudioPcmSource ?? descriptor.Id switch {
      "Wav" => Wav,
      "Caf" => Caf,
      "Ac3" => Ac3,
      "Dts" => Dts,
      "Asf" => AsfPcm,
      _ => AudioFormatAdapters.ResolvePcmSource(descriptor),
    };

  public static IAudioPcmTarget? ResolvePcmTarget(IFormatDescriptor descriptor)
    => descriptor as IAudioPcmTarget ?? descriptor.Id switch {
      "Wav" => Wav,
      "Caf" => Caf,
      "Ac3" => Ac3,
      "Dts" => Dts,
      "Asf" => AsfPcm,
      _ => AudioFormatAdapters.ResolvePcmTarget(descriptor),
    };

  public static IAudioDemuxSource? ResolveDemuxSource(IFormatDescriptor descriptor)
    => descriptor as IAudioDemuxSource ?? descriptor.Id switch {
      "Wav" => G711PacketAdapter.Wav,
      "Aiff" => G711PacketAdapter.Aiff,
      "Au" => G711PacketAdapter.Au,
      "Caf" => Caf,
      "Asf" => AsfPackets,
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
      "Asf" => AsfPackets,
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
