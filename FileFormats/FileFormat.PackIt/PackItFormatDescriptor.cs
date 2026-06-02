#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.PackIt;

public sealed class PackItFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveModifiable, IArchiveDefragmentable, IArchiveLayoutMap {

  /// <summary>Rebuild-based defrag: extracts then re-creates the PackIt archive in listing order.</summary>
  public void Defragment(Stream archive)
    => this.Defragment(archive, new DefragOptions { Mode = DefragMode.ConsolidateAtStart });

  /// <summary>Rebuild-based defrag: extracts then re-creates the PackIt archive per the requested mode.</summary>
  public void Defragment(Stream archive, DefragOptions options) {
    DefragRebuilder.Rebuild(archive, options,
      readEntries: stream => {
        var r = new PackItReader(stream, leaveOpen: true);
        return r.Entries.Select(e => (e.Name, r.Extract(e)));
      },
      buildImage: files => {
        using var ms = new MemoryStream();
        using (var w = new PackItWriter(ms, leaveOpen: true)) {
          foreach (var (n, d) in files) w.AddFile(n, d);
        }
        return ms.ToArray();
      });
  }


  /// <inheritdoc />
  public IEnumerable<DefragBlockInfo> EnumerateLayout(Stream archive) {
    archive.Position = 0;
    var r = new PackItReader(archive);
    foreach (var e in r.Entries) {
      var totalSize = e.DataForkSize + e.ResourceForkSize;
      if (totalSize > 0)
        yield return new DefragBlockInfo(e.DataOffset, totalSize, DefragBlockKind.Used, FileName: e.Name);
    }
  }

  public string Id => "PackIt";
  public string DisplayName => "PackIt";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.CanModify | FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries;

  /// <summary>
  /// Adds (or replaces by name) files inside an existing PackIt archive.
  /// Uses <see cref="PackItModifier"/> — Add appends Stored at EOF; Remove
  /// walks the entry chain and shifts trailing bytes (no central directory).
  /// </summary>
  public void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs) {
    foreach (var (name, data) in FilesOnly(inputs)) {
      var flat = Path.GetFileName(name);
      PackItModifier.RemoveFile(archive, flat, wipeData: true);
      PackItModifier.AddFile(archive, flat, data);
    }
  }

  /// <summary>Removes named entries; uses <see cref="PackItModifier"/>.</summary>
  public void Remove(Stream archive, string[] entryNames) {
    foreach (var name in entryNames)
      PackItModifier.RemoveFile(archive, Path.GetFileName(name), wipeData: true);
  }

  public string DefaultExtension => ".pit";
  public IReadOnlyList<string> Extensions => [".pit"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new([(byte)'P', (byte)'M', (byte)'a', (byte)'g'], Confidence: 0.85),
    new([(byte)'P', (byte)'M', (byte)'a', (byte)'4'], Confidence: 0.85),
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("packit", "PackIt")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "PackIt classic Macintosh archive (.pit), Harry Chesley, 1984";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var r = new PackItReader(stream, leaveOpen: true);
    return r.Entries
      .Select((e, i) => new ArchiveEntryInfo(
        i,
        e.Name,
        e.DataForkSize,
        e.DataForkSize,
        e.IsCompressed ? "Huffman" : "Stored",
        false,
        false,
        DateTime.MinValue))
      .ToList();
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var r = new PackItReader(stream, leaveOpen: true);
    foreach (var e in r.Entries) {
      if (files != null && !MatchesFilter(e.Name, files)) continue;
      WriteFile(outputDir, e.Name, r.Extract(e));
    }
  }

  /// <summary>
  /// Opens a single PackIt entry as a bounded read-only stream. The reader
  /// already produces the decoded data-fork bytes for each entry; the
  /// matched bytes are wrapped in a
  /// <see cref="Compression.Registry.Streaming.BoundedEntryStream"/> sized
  /// to the data fork's logical length so the resource fork tail and
  /// adjacent entries cannot leak.
  /// </summary>
  public Stream OpenEntry(Stream archive, string entryName, string? password) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(entryName);
    if (archive.CanSeek) archive.Position = 0;
    var r = new PackItReader(archive, leaveOpen: true);
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
    using var w = new PackItWriter(output, leaveOpen: true);
    foreach (var (name, data) in FormatHelpers.FlatFiles(inputs))
      w.AddFile(name, data);
  }
}
