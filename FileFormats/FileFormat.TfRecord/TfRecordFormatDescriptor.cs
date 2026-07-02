#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.TfRecord;

/// <summary>
/// TensorFlow TFRecord — a flat sequence of CRC-32C-protected length-prefixed records.
///
/// References:
/// <list type="bullet">
///   <item><description><c>https://www.tensorflow.org/tutorials/load_data/tfrecord</c> — official TFRecord format documentation</description></item>
///   <item><description><c>https://github.com/tensorflow/tensorflow</c> — TensorFlow sources — record framing defined in the RecordWriter/RecordReader code</description></item>
/// </list>
/// </summary>
public sealed class TfRecordFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveDefragmentable, IArchiveLayoutMap {

  /// <summary>Rebuild-based defrag: extracts then re-creates the TFRecord stream in listing order.</summary>
  public void Defragment(Stream archive)
    => this.Defragment(archive, new DefragOptions { Mode = DefragMode.ConsolidateAtStart });

  /// <summary>Rebuild-based defrag: extracts then re-creates the TFRecord stream per the requested mode.</summary>
  public void Defragment(Stream archive, DefragOptions options) {
    DefragRebuilder.Rebuild(archive, options,
      readEntries: stream => {
        var r = new TfRecordReader(stream);
        return r.Entries.Where(e => !e.IsCorrupt).Select(e => (e.Name, r.Extract(e)));
      },
      buildImage: files => {
        using var ms = new MemoryStream();
        using (var w = new TfRecordWriter(ms, leaveOpen: true)) {
          foreach (var (_, d) in files) w.AddRecord(d);
        }
        return ms.ToArray();
      });
  }


  /// <inheritdoc />
  public IEnumerable<DefragBlockInfo> EnumerateLayout(Stream archive) {
    archive.Position = 0;
    var r = new TfRecordReader(archive);
    foreach (var e in r.Entries) {
      if (e.Size > 0)
        yield return new DefragBlockInfo(e.Offset, e.Size, DefragBlockKind.Used, FileName: e.Name);
    }
  }

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
