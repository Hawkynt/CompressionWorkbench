#pragma warning disable CS1591
using Codec.Aac;

namespace FileFormat.Mp4;

/// <summary>
/// Wraps bare AAC access units (as stored in MP4 <c>mp4a</c> samples or Matroska
/// <c>A_AAC</c> blocks) in ADTS frame headers so they can be fed to the ADTS-only
/// <see cref="AacCodec"/>. Each access unit becomes one ADTS frame; the header's
/// profile/sample-rate-index/channel-config are taken from the container's
/// AudioSpecificConfig (object type 2 → AAC-LC → ADTS profile 1).
/// </summary>
internal static class AacAdtsWrapper {

  /// <summary>
  /// Returns one contiguous ADTS-framed buffer covering all <paramref name="accessUnits"/>.
  /// <paramref name="sampleRateIndex"/> and <paramref name="channelConfig"/> come from the
  /// AudioSpecificConfig parsed off the container.
  /// </summary>
  internal static byte[] Wrap(IReadOnlyList<byte[]> accessUnits, int sampleRateIndex, int channelConfig) {
    using var ms = new MemoryStream();
    foreach (var au in accessUnits) {
      if (au.Length == 0) continue;
      var frameLength = AacAdtsReader.ShortHeaderLength + au.Length;
      // AAC-LC: AudioSpecificConfig object type 2 → ADTS profile field (object type - 1) = 1.
      var header = AacAdtsReader.BuildHeader(
        profile: 1, sampleRateIndex, channelConfig, frameLength);
      ms.Write(header, 0, header.Length);
      ms.Write(au, 0, au.Length);
    }
    return ms.ToArray();
  }
}
