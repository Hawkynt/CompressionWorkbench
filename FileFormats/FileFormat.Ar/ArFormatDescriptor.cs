#pragma warning disable CS1591
using Compression.Registry;
using Compression.Registry.Streaming;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Ar;

public sealed class ArFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveModifiable, IArchiveDefragmentable, IArchiveLayoutMap {

  /// <summary>Rebuild-based defrag: extracts then re-creates the AR archive in listing order.</summary>
  public void Defragment(Stream archive)
    => this.Defragment(archive, new DefragOptions { Mode = DefragMode.ConsolidateAtStart });

  /// <summary>Rebuild-based defrag: extracts then re-creates the AR archive per the requested mode.</summary>
  public void Defragment(Stream archive, DefragOptions options) {
    DefragRebuilder.Rebuild(archive, options,
      readEntries: stream => {
        var r = new ArReader(stream);
        return r.Entries.Select(e => (e.Name, e.Data));
      },
      buildImage: files => {
        using var ms = new MemoryStream();
        using (var w = new ArWriter(ms, leaveOpen: true)) {
          var entries = files.Select(f => new ArEntry { Name = f.Name, Data = f.Data }).ToList();
          w.Write(entries);
        }
        return ms.ToArray();
      });
  }

  /// <inheritdoc />
  public IEnumerable<DefragBlockInfo> EnumerateLayout(Stream archive) {
    archive.Position = 0;
    // 8-byte global header "!<arch>\n"
    yield return new DefragBlockInfo(0, ArConstants.GlobalHeaderSize, DefragBlockKind.MetadataReserved, FileName: "AR Global Header");
    var r = new ArReader(archive);
    // AR reader reads everything eagerly; reconstruct offsets by walking
    long pos = ArConstants.GlobalHeaderSize;
    foreach (var e in r.Entries) {
      // 60-byte entry header
      yield return new DefragBlockInfo(pos, ArConstants.EntryHeaderSize, DefragBlockKind.MetadataReserved, FileName: $"Header: {e.Name}");
      pos += ArConstants.EntryHeaderSize;
      if (e.Data.Length > 0)
        yield return new DefragBlockInfo(pos, e.Data.Length, DefragBlockKind.Used, FileName: e.Name);
      pos += e.Data.Length;
      if (e.Data.Length % 2 != 0)
        pos++; // padding byte
    }
  }

  public string Id => "Ar";
  public string DisplayName => "AR";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.CanModify | FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".a";
  public IReadOnlyList<string> Extensions => [".a", ".ar", ".deb"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [new([(byte)'!', (byte)'<', (byte)'a', (byte)'r', (byte)'c', (byte)'h', (byte)'>', (byte)'\n'], Confidence: 0.95)];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("ar", "AR")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "Unix ar archive, used for static libraries (.a files)";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var r = new ArReader(stream);
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(i, e.Name, e.Data.Length, e.Data.Length,
      "ar", false, false, e.ModifiedTime.DateTime)).ToList();
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var r = new ArReader(stream);
    foreach (var e in r.Entries) {
      if (files != null && !MatchesFilter(e.Name, files)) continue;
      WriteFile(outputDir, e.Name, e.Data);
    }
  }

  /// <summary>
  /// Opens a single AR entry as a bounded read-only <see cref="Stream"/>.
  /// AR stores each entry's bytes uncompressed and the reader pre-loads them
  /// into <see cref="ArEntry.Data"/>; the bounded wrapper sizes the view to
  /// the entry's data length.
  /// </summary>
  public Stream OpenEntry(Stream archive, string entryName, string? password) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(entryName);
    if (archive.CanSeek) archive.Position = 0;
    var r = new ArReader(archive, leaveOpen: true);
    foreach (var e in r.Entries) {
      if (!string.Equals(e.Name, entryName, StringComparison.OrdinalIgnoreCase)) continue;
      return new BoundedEntryStream(new MemoryStream(e.Data, writable: false),
        e.Data.Length, leaveOpen: false);
    }
    return new BoundedEntryStream(new MemoryStream(System.Array.Empty<byte>(), writable: false),
      0, leaveOpen: false);
  }

  /// <summary>Native in-memory single-entry extraction.</summary>
  public byte[] ExtractEntryToMemory(Stream archive, string entryName, string? password) {
    using var s = this.OpenEntry(archive, entryName, password);
    using var ms = new MemoryStream();
    s.CopyTo(ms);
    return ms.ToArray();
  }

  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    var entries = FormatHelpers.FilesOnly(inputs)
      .Select(f => new ArEntry { Name = f.Name, Data = f.Data })
      .ToList();
    using var w = new ArWriter(output, leaveOpen: true);
    w.Write(entries);
  }

  /// <summary>
  /// Adds (or replaces by name) files inside an existing AR archive.
  /// Uses <see cref="ArModifier"/> for true random-access I/O — Add is
  /// O(touched bytes) (append at EOF after a quick header walk); Remove
  /// is O(image-size-after-target) because AR has no central directory
  /// and trailing entries must be shifted.
  /// </summary>
  public void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs) {
    foreach (var (name, data) in FormatHelpers.FilesOnly(inputs)) {
      ArModifier.RemoveFile(archive, name, wipeData: true);
      ArModifier.AddFile(archive, name, data);
    }
  }

  /// <summary>
  /// Removes named entries from an existing AR archive. Uses
  /// <see cref="ArModifier"/> for in-place compaction.
  /// </summary>
  public void Remove(Stream archive, string[] entryNames) {
    foreach (var name in entryNames)
      ArModifier.RemoveFile(archive, name, wipeData: true);
  }
}
