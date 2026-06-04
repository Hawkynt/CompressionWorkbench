#pragma warning disable CS1591
using System.Text;
using Compression.Registry;

namespace FileFormat.Dts;

/// <summary>
/// Read-only stream-info view of a raw DTS (Coherent Acoustics) elementary stream. There is no
/// DTS decoder; the descriptor parses the core frame headers (AMODE channel arrangement, SFREQ
/// sample rate, RATE bitrate, LFE flag) and walks the stream by each frame's FSIZE to count
/// frames and estimate duration. The file <em>is</em> the stream, so the only surfaced payload
/// is the byte-exact <c>FULL.dts</c> (Kind <c>Container</c>) alongside <c>metadata.ini</c>
/// (Kind <c>Tag</c>). The presence of a DTS-HD substream extension (the "DTSHDHDR" chunk or the
/// 0x64582025 extension sync) is reported in the metadata.
/// </summary>
public sealed class DtsFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations,
  IArchiveInMemoryExtract {

  /// <summary>DTS-HD container chunk magic ("DTSHDHDR").</summary>
  private static readonly byte[] DtsHdChunkMagic = "DTSHDHDR"u8.ToArray();

  /// <summary>DTS extension substream sync word (0x64582025, big-endian).</summary>
  private static readonly byte[] ExtensionSync = [0x64, 0x58, 0x20, 0x25];

  public string Id => "Dts";
  public string DisplayName => "DTS (Coherent Acoustics)";
  public FormatCategory Category => FormatCategory.Audio;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".dts";
  public IReadOnlyList<string> Extensions => [".dts", ".dtshd"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new(DtsCoreHeader.SyncWord, Confidence: 0.90),            // 7F FE 80 01 core sync
    new(DtsHdChunkMagic, Confidence: 0.95),                   // "DTSHDHDR" container chunk
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("dts", "DTS")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "DTS Coherent Acoustics elementary stream; stream info (no decode).";

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
      new("FULL.dts", "Container", blob, "dts"),
    };

    entries.Add(new("metadata.ini", "Tag", Encoding.UTF8.GetBytes(BuildMetadata(blob))));
    return entries;
  }

  private static string BuildMetadata(byte[] blob) {
    var info = new StringBuilder();
    info.AppendLine("codec=DTS (Coherent Acoustics)");

    var hasDtsHd = IndexOf(blob, DtsHdChunkMagic, 0) >= 0 || IndexOf(blob, ExtensionSync, 0) >= 0;

    // Find the first core sync word; DTS-HD files often lead with a container header chunk.
    var firstCore = IndexOf(blob, DtsCoreHeader.SyncWord, 0);
    if (firstCore < 0 || DtsCoreHeader.TryParse(blob, firstCore) is not { } first) {
      info.AppendLine("frames=0");
      info.AppendLine($"dts_hd_present={(hasDtsHd ? "yes" : "no")}");
      info.AppendLine("note=no parseable DTS core frame found.");
      return info.ToString();
    }

    var channels = DtsCoreHeader.AmodeChannelCount(first.Amode) + (first.LowFrequencyEffects ? 1 : 0);
    info.AppendLine($"channel_layout={DtsCoreHeader.AmodeName(first.Amode)}{(first.LowFrequencyEffects ? " + LFE" : "")}");
    info.AppendLine($"channels={channels}");
    info.AppendLine($"amode={first.Amode}");
    info.AppendLine($"lfe={(first.LowFrequencyEffects ? "yes" : "no")}");
    info.AppendLine($"sample_rate={first.SampleRate}");
    info.AppendLine($"bitrate={(first.Bitrate > 0 ? first.Bitrate.ToString() : "variable/open")}");
    info.AppendLine($"sample_blocks={first.SampleBlocks}");

    // Walk frames by FSIZE, counting and accumulating samples (32 PCM samples per sample block).
    var frames = 0;
    long totalSamples = 0;
    var pos = firstCore;
    while (pos + 14 <= blob.Length) {
      if (DtsCoreHeader.TryParse(blob, pos) is not { } header)
        break;
      ++frames;
      totalSamples += (long)header.SampleBlocks * 32;
      pos += header.FrameSize;
    }

    var duration = first.SampleRate > 0 ? (double)totalSamples / first.SampleRate : 0;
    info.AppendLine($"frames={frames}");
    info.AppendLine($"duration_seconds={duration:0.###}");
    info.AppendLine($"dts_hd_present={(hasDtsHd ? "yes" : "no")}");
    return info.ToString();
  }

  private static int IndexOf(byte[] haystack, byte[] needle, int start) {
    for (var i = start; i + needle.Length <= haystack.Length; ++i) {
      var match = true;
      for (var j = 0; j < needle.Length; ++j)
        if (haystack[i + j] != needle[j]) {
          match = false;
          break;
        }
      if (match)
        return i;
    }
    return -1;
  }
}
