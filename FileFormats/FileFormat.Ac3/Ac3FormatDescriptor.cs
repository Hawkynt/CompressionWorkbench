#pragma warning disable CS1591
using System.Text;
using Compression.Registry;

namespace FileFormat.Ac3;

/// <summary>
/// Read-only stream-info view of a raw AC-3 / E-AC-3 (Dolby Digital / Dolby Digital Plus)
/// elementary stream. There is no decoder; the descriptor parses the sync-frame headers
/// (syncinfo + BSI: sample rate, frame size, channel arrangement via acmod, LFE, dialnorm) and
/// distinguishes AC-3 (bsid ≤ 10) from E-AC-3 (bsid = 16). It walks the stream by each frame's
/// size to count frames and estimate duration. The file <em>is</em> the stream, so the only
/// surfaced payload is the byte-exact <c>FULL.ac3</c> (Kind <c>Container</c>) alongside
/// <c>metadata.ini</c> (Kind <c>Tag</c>).
/// </summary>
public sealed class Ac3FormatDescriptor : IFormatDescriptor, IArchiveFormatOperations,
  IArchiveInMemoryExtract {

  public string Id => "Ac3";
  public string DisplayName => "AC-3 / E-AC-3";
  public FormatCategory Category => FormatCategory.Audio;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".ac3";
  public IReadOnlyList<string> Extensions => [".ac3", ".eac3"];
  public IReadOnlyList<string> CompoundExtensions => [];
  // 16-bit 0x0B77 sync word; low confidence keeps false positives down for arbitrary streams.
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new(Ac3SyncFrame.SyncWord, Confidence: 0.60),
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("ac3", "AC-3")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "AC-3 / E-AC-3 (Dolby Digital) elementary stream; stream info (no decode).";

  public List<ArchiveEntryInfo> List(Stream stream, string? password)
    => AudioPseudoArchive.List(BuildEntries(stream));

  public void Extract(Stream stream, string outputDir, string? password, string[]? files)
    => AudioPseudoArchive.Extract(BuildEntries(stream), outputDir, files);

  public void ExtractEntry(Stream input, string entryName, Stream output, string? password)
    => AudioPseudoArchive.ExtractEntry(BuildEntries(input), entryName, output);

  private static IReadOnlyList<AudioPseudoArchive.Entry> BuildEntries(Stream stream) {
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    var blob = ms.ToArray();

    var entries = new List<AudioPseudoArchive.Entry> {
      new("FULL.ac3", "Container", blob, "ac3"),
    };
    entries.Add(new("metadata.ini", "Tag", Encoding.UTF8.GetBytes(BuildMetadata(blob))));
    return entries;
  }

  private static string BuildMetadata(byte[] blob) {
    var info = new StringBuilder();

    var firstOffset = IndexOfSync(blob, 0);
    if (firstOffset < 0 || Ac3SyncFrame.TryParse(blob, firstOffset) is not { } first) {
      info.AppendLine("codec=AC-3");
      info.AppendLine("frames=0");
      info.AppendLine("note=no parseable AC-3 sync frame found.");
      return info.ToString();
    }

    info.AppendLine($"codec={(first.IsEnhanced ? "E-AC-3 (Dolby Digital Plus)" : "AC-3 (Dolby Digital)")}");
    var channels = Ac3SyncFrame.AcmodChannelCount(first.Acmod) + (first.LowFrequencyEffects ? 1 : 0);
    info.AppendLine($"channel_layout={Ac3SyncFrame.LayoutName(first.Acmod, first.LowFrequencyEffects)}");
    info.AppendLine($"channels={channels}");
    info.AppendLine($"acmod={first.Acmod}");
    info.AppendLine($"lfe={(first.LowFrequencyEffects ? "yes" : "no")}");
    info.AppendLine($"sample_rate={first.SampleRate}");
    info.AppendLine($"bitrate={first.Bitrate}");
    info.AppendLine($"bsid={first.Bsid}");
    info.AppendLine($"dialnorm=-{first.DialNorm} dBFS");

    // Walk frames by size, counting and accumulating samples (1536 PCM samples per AC-3 frame;
    // E-AC-3 frames carry a variable block count handled inside FrameSize-driven walking).
    var frames = 0;
    long totalSamples = 0;
    var pos = firstOffset;
    while (pos + 6 <= blob.Length) {
      if (Ac3SyncFrame.TryParse(blob, pos) is not { } header)
        break;
      ++frames;
      totalSamples += 1536;     // 6 blocks × 256 samples per full AC-3 frame
      pos += header.FrameSize;
    }

    var duration = first.SampleRate > 0 ? (double)totalSamples / first.SampleRate : 0;
    info.AppendLine($"frames={frames}");
    info.AppendLine($"duration_seconds={duration:0.###}");
    return info.ToString();
  }

  private static int IndexOfSync(byte[] blob, int start) {
    for (var i = start; i + 1 < blob.Length; ++i)
      if (blob[i] == 0x0B && blob[i + 1] == 0x77)
        return i;
    return -1;
  }
}
