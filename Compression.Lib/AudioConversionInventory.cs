using Compression.Registry;

namespace Compression.Lib;

/// <summary>Describes how one registered format participates in the audio conversion graph.</summary>
public sealed record AudioConversionCapability(
  string FormatId,
  string DisplayName,
  bool CanDecodePcm,
  bool CanEncodePcm,
  bool CanDemuxEncoded,
  bool CanMuxEncoded,
  bool CanReadPseudoArchive,
  bool CanCreatePseudoArchive,
  IReadOnlyList<string> EncodeCodecs,
  IReadOnlyList<string> MuxCodecs
) {
  public bool CanBeSource => this.CanDecodePcm || this.CanDemuxEncoded || this.CanReadPseudoArchive;
  public bool CanBeTarget => this.CanEncodePcm || this.CanMuxEncoded || this.CanCreatePseudoArchive;
}

/// <summary>
/// Enumerates the actual registered audio conversion surface. This is capability-based,
/// not documentation-based: adding an encoder/muxer automatically changes the inventory.
/// </summary>
public static class AudioConversionInventory {

  public static IReadOnlyList<AudioConversionCapability> Enumerate() {
    FormatRegistry.Initialize();
    return FormatRegistry.All
      .Where(IsAudioCandidate)
      .Select(Describe)
      .OrderBy(static item => item.FormatId, StringComparer.OrdinalIgnoreCase)
      .ToArray();
  }

  public static AudioConversionCapability Describe(IFormatDescriptor descriptor) {
    ArgumentNullException.ThrowIfNull(descriptor);

    var pcmSource = AudioAdapterResolver.ResolvePcmSource(descriptor);
    var pcmTarget = AudioAdapterResolver.ResolvePcmTarget(descriptor);
    var demux = AudioAdapterResolver.ResolveDemuxSource(descriptor);
    var mux = AudioAdapterResolver.ResolveMuxTarget(descriptor);
    var archive = descriptor as IArchiveFormatOperations;
    var creator = AudioAdapterResolver.ResolvePseudoArchiveTarget(descriptor);

    return new AudioConversionCapability(
      descriptor.Id,
      descriptor.DisplayName,
      pcmSource is not null,
      pcmTarget is not null,
      demux is not null,
      mux is not null,
      archive is not null,
      creator is not null,
      pcmTarget?.SupportedEncodeCodecs.ToArray() ?? [],
      mux?.SupportedMuxCodecs.ToArray() ?? []);
  }

  private static bool IsAudioCandidate(IFormatDescriptor descriptor)
    => descriptor.Category == FormatCategory.Audio
       || descriptor is IAudioContainerFormat
       || AudioAdapterResolver.ResolvePcmSource(descriptor) is not null
       || AudioAdapterResolver.ResolvePcmTarget(descriptor) is not null
       || AudioAdapterResolver.ResolveDemuxSource(descriptor) is not null
       || AudioAdapterResolver.ResolveMuxTarget(descriptor) is not null;
}
