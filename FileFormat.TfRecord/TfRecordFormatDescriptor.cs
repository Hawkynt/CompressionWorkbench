#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.TfRecord;

public sealed class TfRecordFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable {
  public string Id => "TfRecord";
  public string DisplayName => "TensorFlow TFRecord";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".tfrecord";
  public IReadOnlyList<string> Extensions => [".tfrecord", ".tfrecords"];
  public IReadOnlyList<string> CompoundExtensions => [];

  // TFRecord has no header or magic bytes — detection is by extension only.
  // The reader validates the first record's length-CRC to reject false positives.
  public IReadOnlyList<MagicSignature> MagicSignatures => [];

  public IReadOnlyList<FormatMethodInfo> Methods => [new("tfrecord", "TFRecord")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "TensorFlow TFRecord — sequence of CRC-32C-protected length-prefixed records";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var r = new TfRecordReader(stream);
    return r.Entries
      .Select((e, i) => new ArchiveEntryInfo(i, e.Name, e.Size, e.Size, "Stored", false, false, null,
                                             e.IsCorrupt ? "corrupt" : null))
      .ToList();
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var r = new TfRecordReader(stream);
    foreach (var e in r.Entries) {
      if (files != null && !MatchesFilter(e.Name, files)) continue;
      // Skip records flagged corrupt — we can't trust either their boundary or contents.
      if (e.IsCorrupt) continue;
      WriteFile(outputDir, e.Name, r.Extract(e));
    }
  }

  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    using var w = new TfRecordWriter(output, leaveOpen: true);
    foreach (var (_, data) in FormatHelpers.FlatFiles(inputs))
      w.AddRecord(data);
  }
}
