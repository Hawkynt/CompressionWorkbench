#pragma warning disable CS1591
using System.Text;
using Compression.Registry;

namespace FileFormat.Oma;

/// <summary>
/// Read-only stream-info view of a Sony OpenMG (.oma / .aa3 / .at3) file. There is no ATRAC
/// decoder (out of scope); the descriptor parses the leading "ea3" ID3v2-style tag (TIT2 / TPE1
/// / TALB … text frames) and the 96-byte binary "EA3" header (codec id + coding parameters),
/// then slices out the coded audio payload. The archive view surfaces the byte-exact
/// <c>FULL.oma</c> (Kind <c>Container</c>), the coded <c>stream.bin</c> payload (Kind
/// <c>Stream</c>, Method = the carried codec name) and <c>metadata.ini</c> (Kind <c>Tag</c>).
/// </summary>
public sealed class OmaFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations,
  IArchiveInMemoryExtract {

  public string Id => "Oma";
  public string DisplayName => "Sony OpenMG (OMA/AA3)";
  public FormatCategory Category => FormatCategory.Audio;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".oma";
  public IReadOnlyList<string> Extensions => [".oma", ".aa3", ".at3"];
  public IReadOnlyList<string> CompoundExtensions => [];
  // The leading "ea3" ID3v2-style tag identifier (the binary "EA3" header is at a variable offset).
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new(OmaHeader.TagMagic, Confidence: 0.90),
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("oma", "OpenMG")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "Sony OpenMG (ATRAC3/3plus) container; tag + stream info (no decode).";

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
      new("FULL.oma", "Container", blob, "oma"),
    };

    var header = OmaHeader.TryParse(blob);
    var info = new StringBuilder();
    if (header is null) {
      info.AppendLine("codec=unknown");
      info.AppendLine("note=no parseable OpenMG ea3/EA3 header found.");
      entries.Add(new("metadata.ini", "Tag", Encoding.UTF8.GetBytes(info.ToString())));
      return entries;
    }

    // Surface the coded payload after the EA3 header (Method = carried codec name).
    var payloadLength = blob.Length - header.PayloadOffset;
    if (payloadLength > 0) {
      var payload = new byte[payloadLength];
      Array.Copy(blob, header.PayloadOffset, payload, 0, payloadLength);
      entries.Add(new("stream.bin", "Stream", payload, header.CodecName));
    }

    info.AppendLine($"codec={header.CodecName}");
    info.AppendLine($"codec_id={header.CodecId}");
    info.AppendLine($"coding_params=0x{header.CodingParams:X6}");
    if (header.SampleRate > 0)
      info.AppendLine($"sample_rate={header.SampleRate}");
    info.AppendLine($"tag_size={header.TagSize}");
    info.AppendLine($"ea3_header_offset={header.Ea3HeaderOffset}");
    info.AppendLine($"payload_offset={header.PayloadOffset}");
    info.AppendLine($"payload_bytes={payloadLength}");
    foreach (var (frame, value) in header.Tags)
      info.AppendLine($"{frame}={value}");

    entries.Add(new("metadata.ini", "Tag", Encoding.UTF8.GetBytes(info.ToString())));
    return entries;
  }
}
