#pragma warning disable CS1591
using System.Text;
using Codec.Pcm;
using Compression.Registry;

namespace FileFormat.EspsSd;

/// <summary>
/// Exposes an Entropic ESPS sampled-data (<c>.sd</c>) file as an archive of
/// <c>FULL.sd</c>, <c>MONO.wav</c> and <c>metadata.ini</c>. The single-channel 16-bit
/// case is decoded: the byte order comes from the <c>0x00006A1A</c> check code at
/// offset 16, the sample data starts at the header-declared data offset, and the
/// sample rate is taken from the <c>record_freq</c> generic header item (default
/// 16000 Hz). READ-ONLY.
/// </summary>
public sealed class EspsSdFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations,
  IArchiveInMemoryExtract {

  public string Id => "EspsSd";
  public string DisplayName => "ESPS sampled data (.sd)";
  public FormatCategory Category => FormatCategory.Audio;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".sd";
  public IReadOnlyList<string> Extensions => [".sd"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    // Check code 0x00006A1A at offset 16 — big-endian and byte-swapped little-endian.
    new([0x00, 0x00, 0x6A, 0x1A], Offset: 16, Confidence: 0.90),
    new([0x1A, 0x6A, 0x00, 0x00], Offset: 16, Confidence: 0.90),
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "ESPS (.sd) sampled data; single-channel 16-bit → MONO.wav.";

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
    var parsed = new EspsSdReader().Read(blob);

    var entries = new List<AudioPseudoArchive.Entry> {
      new("FULL.sd", "Container", blob),
    };
    if (parsed.SampleData.Length > 0)
      entries.Add(new("MONO.wav", "Channel",
        PcmCodec.ToWavBlob(parsed.SampleData, 1, parsed.SampleRate, 16)));

    var info = new StringBuilder();
    info.AppendLine($"byte_order={(parsed.BigEndian ? "big-endian" : "little-endian")}");
    info.AppendLine($"data_offset={parsed.DataOffset}");
    info.AppendLine($"sample_rate={parsed.SampleRate}");
    info.AppendLine($"sample_rate_source={(parsed.RateFromHeader ? "record_freq" : "default")}");
    info.AppendLine("channels=1");
    info.AppendLine("bits=16");
    entries.Add(new("metadata.ini", "Tag", Encoding.UTF8.GetBytes(info.ToString())));

    return entries;
  }
}
