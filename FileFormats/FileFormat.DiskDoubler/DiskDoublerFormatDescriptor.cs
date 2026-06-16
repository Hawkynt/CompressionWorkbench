#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.DiskDoubler;

public sealed class DiskDoublerFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveDefragmentable, IArchiveLayoutMap {

  public void Defragment(Stream archive)
    => throw new NotSupportedException(
      "DiskDoubler wraps a single Macintosh file (data fork + resource fork) — defragmentation isn't meaningful.");
  public void Defragment(Stream archive, DefragOptions options) => this.Defragment(archive);


  /// <inheritdoc />
  public IEnumerable<DefragBlockInfo> EnumerateLayout(Stream archive) {
    archive.Position = 0;
    var r = new DiskDoublerReader(archive);
    foreach (var e in r.Entries) {
      if (e.CompressedSize > 0)
        yield return new DefragBlockInfo(e.DataOffset, e.CompressedSize, DefragBlockKind.Used, FileName: e.Name);
    }
  }

  public string Id => "DiskDoubler";
  public string DisplayName => "DiskDoubler";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".dd";
  public IReadOnlyList<string> Extensions => [".dd", ".sea"];
  public IReadOnlyList<string> CompoundExtensions => [];
  // DiskDoubler has no universally reliable magic bytes; detection is primarily by extension.
  // The header version identifier at offset 0 varies by version so we use a low-confidence hint.
  public IReadOnlyList<MagicSignature> MagicSignatures => [];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("diskdoubler", "DiskDoubler")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "DiskDoubler compressed Macintosh file (Salient Software, 1989-1993)";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var r = new DiskDoublerReader(stream, leaveOpen: true);
    return r.Entries
      .Select((e, i) => new ArchiveEntryInfo(
        i,
        e.Name,
        e.OriginalSize,
        e.CompressedSize,
        $"Method {e.Method}",
        false,
        false,
        DateTime.MinValue))
      .ToList();
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var r = new DiskDoublerReader(stream, leaveOpen: true);
    foreach (var e in r.Entries) {
      if (files != null && !MatchesFilter(e.Name, files)) continue;
      WriteFile(outputDir, e.Name, r.Extract(e));
    }
  }

  /// <summary>
  /// Opens a single DiskDoubler entry as a bounded read-only stream. The
  /// reader produces the decoded data; the matched bytes are wrapped in a
  /// <see cref="Compression.Registry.Streaming.BoundedEntryStream"/> sized
  /// to the entry's original length.
  /// </summary>
  public Stream OpenEntry(Stream archive, string entryName, string? password) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(entryName);
    if (archive.CanSeek) archive.Position = 0;
    var r = new DiskDoublerReader(archive, leaveOpen: true);
    foreach (var e in r.Entries) {
      if (!string.Equals(e.Name, entryName, StringComparison.OrdinalIgnoreCase)) continue;
      var bytes = r.Extract(e);
      return new Compression.Registry.Streaming.BoundedEntryStream(
        new MemoryStream(bytes, writable: false), bytes.Length, leaveOpen: false);
    }
    return new Compression.Registry.Streaming.BoundedEntryStream(
      new MemoryStream(System.Array.Empty<byte>(), writable: false), 0, leaveOpen: false);
  }

  /// <summary>Native in-memory single-entry extraction routed through the bounded <see cref="OpenEntry"/>.</summary>
  public byte[] ExtractEntryToMemory(Stream archive, string entryName, string? password) {
    using var s = this.OpenEntry(archive, entryName, password);
    using var memoryStream = new MemoryStream();
    s.CopyTo(memoryStream);
    return memoryStream.ToArray();
  }

  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    // WORM: store the first input as a DiskDoubler data fork (method 0 = stored).
    // DiskDoubler wraps a single Macintosh file, not a multi-file archive.
    var w = new DiskDoublerWriter();
    foreach (var i in inputs) {
      if (i.IsDirectory) continue;
      w.SetFile(Path.GetFileName(i.ArchiveName), i.ReadContent());
      break;
    }
    w.WriteTo(output);
  }
}
