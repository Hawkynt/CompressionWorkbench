#pragma warning disable CS1591
using Codec.Aac;

namespace FileFormat.Matroska;

/// <summary>
/// Wraps bare AAC access units (Matroska <c>A_AAC</c> blocks) in ADTS frame headers so
/// they can be fed to the ADTS-only <see cref="AacCodec"/>. Each access unit becomes one
/// ADTS frame; the header's profile/sample-rate-index/channel-config come from the
/// track's AudioSpecificConfig (object type 2 → AAC-LC → ADTS profile 1).
/// </summary>
internal static class AacAdtsWrapper {

  internal static byte[] Wrap(IReadOnlyList<byte[]> accessUnits, int sampleRateIndex, int channelConfig) {
    using var ms = new MemoryStream();
    foreach (var au in accessUnits) {
      if (au.Length == 0) continue;
      var frameLength = AacAdtsReader.ShortHeaderLength + au.Length;
      var header = AacAdtsReader.BuildHeader(profile: 1, sampleRateIndex, channelConfig, frameLength);
      ms.Write(header, 0, header.Length);
      ms.Write(au, 0, au.Length);
    }
    return ms.ToArray();
  }
}
