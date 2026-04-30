#pragma warning disable CS1591
using System.Globalization;
using System.Text;
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Akb;

/// <summary>
/// Square Enix AKB audio bank descriptor — surfaces per-entry raw audio payloads plus a synthetic
/// <c>metadata.ini</c> entry containing bank-wide header fields (sample rate, channel mode, loop points).
/// </summary>
public sealed class AkbFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable {
  public string Id => "Akb";
  public string DisplayName => "Square Enix AKB";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".akb";
  public IReadOnlyList<string> Extensions => [".akb"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new("AKB1"u8.ToArray(), Confidence: 0.95),
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("akb-v2", "AKB v2")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "Square Enix audio bank (Final Fantasy / Kingdom Hearts era)";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    ArgumentNullException.ThrowIfNull(stream);
    var origin = stream.Position;
    try {
      stream.Position = 0;
      using var reader = new AkbReader(stream, leaveOpen: true);
      var result = new List<ArchiveEntryInfo>(reader.Entries.Count + 1);
      for (var i = 0; i < reader.Entries.Count; ++i) {
        var e = reader.Entries[i];
        result.Add(new ArchiveEntryInfo(i, e.Name, e.Size, e.Size, "Stored", false, false, null));
      }
      var meta = BuildMetadata(reader);
      result.Add(new ArchiveEntryInfo(reader.Entries.Count, AkbConstants.MetadataEntryName, meta.Length, meta.Length, "Stored", false, false, null));
      return result;
    } finally {
      stream.Position = origin;
    }
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    ArgumentNullException.ThrowIfNull(stream);
    ArgumentNullException.ThrowIfNull(outputDir);
    stream.Position = 0;
    using var reader = new AkbReader(stream, leaveOpen: true);
    foreach (var e in reader.Entries) {
      if (files != null && !MatchesFilter(e.Name, files)) continue;
      WriteFile(outputDir, e.Name, reader.Extract(e));
    }
    if (files == null || MatchesFilter(AkbConstants.MetadataEntryName, files))
      WriteFile(outputDir, AkbConstants.MetadataEntryName, BuildMetadata(reader));
  }

  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    using var w = new AkbWriter(output, leaveOpen: true);
    foreach (var (name, data) in FlatFiles(inputs)) {
      // Skip a metadata.ini supplied as input — we synthesize that on read, persisting it as a real
      // payload would round-trip as a phantom audio entry on the next List() call.
      if (string.Equals(name, AkbConstants.MetadataEntryName, StringComparison.OrdinalIgnoreCase))
        continue;
      w.AddEntry(name, data);
    }
  }

  private static byte[] BuildMetadata(AkbReader reader) {
    var sb = new StringBuilder();
    sb.AppendLine("[akb]");
    sb.Append("version = ").AppendLine(reader.VersionByte.ToString(CultureInfo.InvariantCulture));
    sb.Append("channel_mode = ").AppendLine(reader.ChannelMode.ToString(CultureInfo.InvariantCulture));
    sb.Append("sample_rate = ").AppendLine(reader.SampleRate.ToString(CultureInfo.InvariantCulture));
    sb.Append("loop_start = ").AppendLine(reader.LoopStart.ToString(CultureInfo.InvariantCulture));
    sb.Append("loop_end = ").AppendLine(reader.LoopEnd.ToString(CultureInfo.InvariantCulture));
    sb.Append("content_offset = ").AppendLine(reader.ContentOffset.ToString(CultureInfo.InvariantCulture));
    sb.Append("content_size = ").AppendLine(reader.ContentSize.ToString(CultureInfo.InvariantCulture));
    sb.Append("entry_count = ").AppendLine(reader.Entries.Count.ToString(CultureInfo.InvariantCulture));
    return Encoding.UTF8.GetBytes(sb.ToString());
  }
}
